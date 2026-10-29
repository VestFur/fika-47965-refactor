using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The breadcrumb journal of every native -> managed boundary this port owns, plus the guard that keeps a
/// managed exception from escaping through one of them.
///
/// Why a journal and not just a log line: the runtime can die without running a single managed handler.
/// A fail fast (COR_E_EXECUTIONENGINE, 0x80131506, raised by coreclr itself with EXCEPTION_NONCONTINUABLE)
/// and STATUS_STACK_BUFFER_OVERRUN (0xC0000409) both kill the process while the instruction that caused
/// them is still on the stack, so <c>AppDomain.FirstChanceException</c> and every try/catch are bypassed.
/// What survives such a death is what was already written to disk, so this class keeps a small ring of the
/// most recent boundaries in memory (constant strings only, no allocation per entry) and
/// <see cref="Dump"/> flushes it when the fault probe sees one of those codes.
///
/// The ring coalesces consecutive equal labels, so the history keeps "which boundaries were crossed",
/// not "the same frame tick repeated four thousand times".
///
/// Round 2 (analysis/countdown-bisect-round2): keeping the journal only in memory meant the whole
/// instrument depended on the persistor thread, and at 11:17 the persistor stopped 14 s before the death
/// while the process kept running - so the trail named no countdown label at all. The journal therefore
/// writes through to the trail itself now: <see cref="Enter"/> throttled to twice per second,
/// <see cref="Mark"/>/<see cref="Returned"/>/<see cref="Skipped"/> immediately and unbuffered.
/// </summary>
internal static class FikaBoundaries
{
    private const int Capacity = 64;
    private const int RepeatSummariesOnDump = 12;

    private struct Entry
    {
        internal string Label;
        internal long Millis;
        internal int Repeats;
    }

    private static readonly object _gate = new();
    private static readonly Entry[] _ring = new Entry[Capacity];
    private static readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> _failureQuietUntil = new(StringComparer.Ordinal);
    private static int _index = -1;
    private static int _count;
    private static bool _dumped;

    /// <summary>
    /// Records that control entered a boundary. Constant label strings only (they are not copied),
    /// no allocation, no Unity API - safe to call once per frame per boundary.
    /// </summary>
    internal static void Enter(string label)
    {
        try
        {
            var now = Environment.TickCount64;
            if (_index >= 0 && ReferenceEquals(_ring[_index].Label, label))
            {
                _ring[_index].Repeats++;
                _ring[_index].Millis = now;
                return;
            }

            _index = (_index + 1) % Capacity;
            _ring[_index].Label = label;
            _ring[_index].Millis = now;
            _ring[_index].Repeats = 1;
            if (_count < Capacity)
            {
                _count++;
            }

            
        }
        catch (Exception)
        {
            // A journal must never be the reason a boundary fails.
        }
    }

    /// <summary>
    /// Records a *statement* - a label that runs once per raid, not once per frame - and puts it on disk
    /// before returning: the ring for the fatal dump plus one unbuffered WriteFile through the crash
    /// trail's label handle, on the calling thread. This is the instrument the bisect reads: the last
    /// "[Label] ..." line of the trail names the statement the process died in, even when the trail's
    /// persistor thread has stopped (the 11:17 failure). Never throttled, never batched.
    /// </summary>
    internal static void Mark(string label)
    {
        Enter(label);
        
    }

    /// <summary>Records that a statement returned. The trail gets "&lt;label&gt; - RETURNED" immediately.</summary>
    internal static void Returned(string label)
    {
        try
        {
            
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Records that a statement did not run because its switch is off. Written with the same immediacy as
    /// <see cref="Mark"/>: a trail that ends on a SKIPPED line must not be mistaken for "this ran".
    /// </summary>
    internal static void Skipped(string label)
    {
        Skipped(label, "its switch is off");
    }

    /// <summary>
    /// The same as <see cref="Skipped(string)"/>, with the reason written out. Round 3 needs it for the
    /// post-wait block: it is skipped either by its own master switch or by 'Skip Countdown Body', and the
    /// trail has to say which one so a reader never mistakes one for the other.
    /// </summary>
    internal static void Skipped(string label, string reason)
    {
        try
        {
            
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The newest boundary in the ring, as "&lt;label&gt; (&lt;ms&gt; ms ago)", or "&lt;none&gt;". Bounded
    /// like <see cref="SnapshotBounded"/> - it never waits for the journal lock - so a report written from
    /// the main thread (the pump's frame-stall label) cannot be parked behind a stuck writer.
    /// </summary>
    internal static string LastLabel()
    {
        var taken = false;
        try
        {
            taken = Monitor.TryEnter(_gate, 25);
            if (_count == 0 || _index < 0 || _ring[_index].Label == null)
            {
                return "<none>";
            }

            var entry = _ring[_index];
            return "'" + entry.Label + "' " + (Environment.TickCount64 - entry.Millis) + " ms ago";
        }
        catch (Exception)
        {
            return "<none>";
        }
        finally
        {
            if (taken)
            {
                try
                {
                    Monitor.Exit(_gate);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// Runs one boundary body. A managed exception that would otherwise escape into the engine (or into
    /// the runtime, which is what turns it into a crash) is reported once per site and swallowed, so the
    /// frame - and every other boundary - continues.
    /// </summary>
    internal static bool Run(string label, Action body)
    {
        try
        {
            body();
            return true;
        }
        catch (Exception exception)
        {
            Fail(label, exception);
            return false;
        }
    }

    /// <summary>Guarded factory: returns the fallback when the boundary body throws.</summary>
    internal static T Run<T>(string label, Func<T> body, T fallback)
    {
        try
        {
            return body();
        }
        catch (Exception exception)
        {
            Fail(label, exception);
            return fallback;
        }
    }

    /// <summary>Bounded, throttled failure report for one boundary site.</summary>
    internal static void Fail(string label, Exception exception)
    {
        try
        {
            var now = Environment.TickCount64;
            var count = 0;
            var report = false;

            lock (_gate)
            {
                _failures.TryGetValue(label, out count);
                count++;
                _failures[label] = count;
                _failureQuietUntil.TryGetValue(label, out var quietUntil);
                if (count == 1 || now >= quietUntil)
                {
                    _failureQuietUntil[label] = now + 5000;
                    report = true;
                }
            }

            if (report)
            {
                
                return;
            }

            if (count == 10 || count == 100 || count == 1000)
            {
                
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Writes the journalled boundaries, oldest first, and asks the first-chance logger for its own
    /// summary, once per process. Safe to call from the fault probe's vectored handler: it only appends
    /// to the diagnostics file.
    /// </summary>
    internal static void Dump(string reason)
    {
        lock (_gate)
        {
            if (_dumped)
            {
                return;
            }

            _dumped = true;
        }

        try
        {
            
            if (_count == 0)
            {
                
            }
            else
            {
                var text = Snapshot(Capacity);
                var start = 0;
                while (start < text.Length)
                {
                    var end = text.IndexOf('\n', start);
                    if (end < 0)
                    {
                        end = text.Length;
                    }

                    if (end > start)
                    {
                        
                    }

                    start = end + 1;
                }
            }

            var failures = DescribeFailures();
            if (failures != null)
            {
                
            }
        }
        catch (Exception)
        {
        }

        
    }

    /// <summary>
    /// Renders the last <paramref name="maxEntries"/> journalled boundaries, oldest first, one per line.
    /// Pure managed code: the crash path calls this while the runtime is dying, so it touches neither
    /// Unity nor the diagnostics sink and it never throws.
    /// </summary>
    internal static string Snapshot(int maxEntries)
    {
        var builder = new StringBuilder(2048);
        try
        {
            lock (_gate)
            {
                AppendJournal(builder, maxEntries);
            }
        }
        catch (Exception)
        {
        }

        return builder.Length == 0 ? "no boundary was journalled" : builder.ToString();
    }

    /// <summary>
    /// The same rendering as <see cref="Snapshot"/>, but it never blocks for longer than
    /// <paramref name="waitMillis"/>: when the journal lock cannot be taken in that time the ring is read
    /// without it. A torn entry (a label with the previous timestamp) is a cosmetic defect in a
    /// diagnostic line; the 11:17 failure was the opposite - the persistor called the blocking
    /// <see cref="Snapshot"/> and the trail stopped for good when that lock was held.
    /// </summary>
    internal static string SnapshotBounded(int maxEntries, int waitMillis)
    {
        var builder = new StringBuilder(2048);
        var taken = false;
        try
        {
            taken = Monitor.TryEnter(_gate, waitMillis);
        }
        catch (Exception)
        {
            taken = false;
        }

        try
        {
            AppendJournal(builder, maxEntries);
        }
        catch (Exception)
        {
        }
        finally
        {
            if (taken)
            {
                try
                {
                    Monitor.Exit(_gate);
                }
                catch (Exception)
                {
                }
            }
        }

        return builder.Length == 0 ? "no boundary was journalled" : builder.ToString();
    }

    private static void AppendJournal(StringBuilder builder, int maxEntries)
    {
        var wanted = Math.Min(_count, Math.Max(1, maxEntries));
        for (var i = _count - wanted; i < _count; i++)
        {
            var index = (_index - _count + 1 + i + Capacity * 4) % Capacity;
            var entry = _ring[index];
            if (entry.Label == null)
            {
                continue;
            }

            builder.Append("[Boundary]   ").Append(entry.Label);
            if (entry.Repeats > 1)
            {
                builder.Append(" (x").Append(entry.Repeats).Append(')');
            }

            builder.Append('\n');
        }
    }

    private static string DescribeFailures()
    {
        var builder = new StringBuilder(160);
        lock (_gate)
        {
            var shown = 0;
            foreach (var pair in _failures)
            {
                if (shown++ == RepeatSummariesOnDump)
                {
                    break;
                }

                if (builder.Length != 0)
                {
                    builder.Append(", ");
                }

                builder.Append(pair.Key).Append('x').Append(pair.Value);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
