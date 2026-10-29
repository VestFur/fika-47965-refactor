using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Fika.Core.Main.GameMode;

namespace Fika.Core.Main.Utils;

/// <summary>
/// AP (analysis/nonfika-raid-inertness): the one question every in-raid hook of this port has to ask
/// before it touches anything - "is the raid that is running right now one of ours?".
///
/// Why a new gate and not <see cref="FikaBackendUtils.ClientType"/>: that field is set when a Fika match
/// is created or joined and is *not* reset when a raid ends, so a single Fika raid in a session left it
/// at Host/Client for the rest of the process. Every "is this a Fika raid" test written as
/// <c>ClientType != None</c> was therefore true inside the next native single-player raid as well - and
/// that is the state the 2026-10-12 23:52 crash was played in: the raid was started from the map
/// selection screen without Fika (the port's own line says <c>side=single</c>), the engine ran the native
/// local game, and our in-raid hooks still acted on it.
///
/// Three rules, in this order:
///   1. the gate is armed by the *creation of our own game object*, not by the plugin being loaded and
///      not by a field a raid can leave behind;
///   2. it is disarmed the moment a raid that is not ours starts, and when our raid ends;
///   3. it self-corrects: while it is armed, a throttled look at the game the engine is really running
///      demotes it as soon as that game is provably not a <see cref="CoopGame"/>. A stale "yes" is the
///      one answer that can get us killed, so it is the one answer that is re-checked.
///
/// The switch <see cref="InertOutsideFikaRaids"/> (config "Coop / Inert Outside Fika Raids", default
/// true) turns the whole behaviour off in one line: with it off <see cref="Skip"/> always returns false
/// and every caller behaves exactly as it did before this class existed.
/// </summary>
internal static class FikaRaidGate
{
    /// <summary>
    /// Master switch of the repair. On (default): outside a Fika raid every caller of
    /// <see cref="Skip"/> keeps its hands off. Off: the pre-fix behaviour, one line to revert.
    /// </summary>
    internal static bool InertOutsideFikaRaids = true;

    /// <summary>
    /// How long a game that is not ours may be the running one before this gate believes it. Our own
    /// game object is created *after* the gate is armed (CoopGame.Create -> Singleton&lt;AbstractGame&gt;.Create),
    /// and a transit reload replaces it, so a single sighting of "not ours" is not a verdict.
    /// </summary>
    private const int MismatchGraceMillis = 3000;

    /// <summary>Throttle of the verifying look at the running game.</summary>
    private const int VerifyIntervalMillis = 500;

    /// <summary>Seconds between two "we are still skipping" summaries.</summary>
    private const int SummarySeconds = 60;

    private static readonly object Gate = new();
    private static readonly HashSet<string> FirstLineWritten = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Skips = new(StringComparer.Ordinal);

    private static bool _armed;
    private static string _armedSource = "none";
    private static long _armedAt;
    private static long _nextVerify;
    private static long _nextSummary;
    private static long _mismatchSince;
    private static int _demotions;
    private static int _skipsSinceSummary;

    /// <summary>The judgement of this class: true while this process runs a raid Fika created.</summary>
    internal static bool InFikaRaid
    {
        get
        {
            return _armed;
        }
    }

    /// <summary>
    /// True when the caller must not act. A caller writes
    /// <c>if (FikaRaidGate.Skip("Owner.Method")) return true; // native</c>
    /// and gets the whole diagnostic for free.
    /// </summary>
    internal static bool Inert
    {
        get
        {
            return InertOutsideFikaRaids && !_armed;
        }
    }

    /// <summary>
    /// The line every hook of this port uses. Returns true when the caller has to stay out; the first
    /// call per member writes<br/>
    /// <c>[Inert] skip=&lt;member&gt; reason=not-a-fika-raid side=single</c><br/>
    /// (once to the game log and once to the diagnostics file, then only counts, plus a summary per
    /// minute), so one grep answers "did anything of ours act in a raid that was not ours".
    /// </summary>
    internal static bool Skip(string member)
    {
        if (InFikaRaid || !InertOutsideFikaRaids)
        {
            return false;
        }

        // The verifying look runs before the report: an armed gate that is really looking at someone
        // else's raid has to demote itself here, not after a line was written about it.
        VerifyArmed();
        if (InFikaRaid)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Called by the one method that creates a Fika game (<c>CreateFikaGameCore</c>). This is the only
    /// place that knows a raid of ours really starts.
    /// </summary>
    internal static void Arm(string source)
    {
        _armed = true;
        _armedSource = string.IsNullOrEmpty(source) ? "unspecified" : source;
        _armedAt = Environment.TickCount64;
        _mismatchSince = 0;
        _nextVerify = 0;
        _demotions = 0;
        // The first summary of a raid comes one minute in, not after the second skipped hook: the first
        // line of every member is the evidence, the summary is only there to keep a long raid readable.
        _nextSummary = Environment.TickCount64 + SummarySeconds * 1000L;
    }

    /// <summary>
    /// Called by the raid-start patch when it hands the raid to the *native* local game
    /// (<c>clientType=None</c>, i.e. the map selection screen without Fika). This is the decisive
    /// moment for everything that follows: from here on the port must not act until it creates a game
    /// of its own again.
    /// </summary>
    internal static void NativeRaidStarted(string source)
    {
        var wasArmed = _armed;
        _armed = false;
        _mismatchSince = 0;
        if (!wasArmed && _nativeStartReported)
        {
            // One line per transition, plus one for the first native raid of the session: a session with
            // five solo raids must not write five identical lines, and the *first* one is the evidence.
            return;
        }

        _nativeStartReported = true;
    }

    /// <summary>Called when a raid of ours ends (a failed start, or the game's own clean-up).</summary>
    internal static void Disarm(string reason)
    {
        if (!_armed)
        {
            return;
        }

        _armed = false;
        _mismatchSince = 0;
    }

    /// <summary>
    /// The role this process believes it has, exactly as the diagnostics of the rally gate spell it, so
    /// every line of this port uses one vocabulary.
    /// </summary>
    internal static string Side
    {
        get
        {
            try
            {
                if (FikaBackendUtils.IsHeadless)
                {
                    return "headless";
                }

                if (FikaBackendUtils.IsServer)
                {
                    return "host";
                }

                if (FikaBackendUtils.IsClient)
                {
                    return "client";
                }
            }
            catch (Exception)
            {
            }

            return "single";
        }
    }

    private static bool _nativeStartReported;

    /// <summary>
    /// The throttled verification, callable by the per-frame driver (the plugin's Update) so the gate
    /// demotes itself even in a frame in which no hook asked anything. Never throws.
    /// </summary>
    internal static void Verify() => VerifyArmed();

    /// <summary>
    /// The verifying look described in rule 3. Cheap by construction: one throttle check, and inside the
    /// throttle one singleton read. Never throws, never blocks.
    /// </summary>
    private static void VerifyArmed()
    {
        if (!_armed)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now < _nextVerify)
        {
            return;
        }

        _nextVerify = now + VerifyIntervalMillis;
        try
        {
            var game = Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance : null;
            if (game == null)
            {
                // Between two raids there is nothing to judge; the next raid start arms or disarms us.
                return;
            }

            if (game.TryCast<CoopGame>() != null)
            {
                _mismatchSince = 0;
                return;
            }

            if (_mismatchSince == 0)
            {
                _mismatchSince = now;
                return;
            }

            if (now - _mismatchSince < MismatchGraceMillis)
            {
                return;
            }

            _armed = false;
            _mismatchSince = 0;
            _demotions++;
        }
        catch (Exception)
        {
            // A verification must never be the reason a boundary fails.
        }
    }

}
