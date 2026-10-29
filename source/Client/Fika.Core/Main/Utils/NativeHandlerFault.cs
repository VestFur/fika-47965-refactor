using System;

namespace Fika.Core.Main.Utils;

/// <summary>
/// (analysis/session-log-archive) The reporter for a natively subscribed handler that throws inside its own
/// native frame, and the bound that keeps it readable.
///
/// <para>
/// The shape it replaces: <c>BotStateManager.Update</c> pumps the game's own <c>IBotGame.UpdateByUnity</c>
/// event, the native <c>BotGroupHostility</c> subscribed its per-frame hostility recalculation to it, and that
/// native method dereferences a field that is still null while the raid world is being built. The exception
/// escaped through the invoke stub into <c>NativeManagedInvoker.Shim</c>, which reported it as
/// "[FAIL] [ManagedInvoker] the managed shim failed for Fika.Core.Main.Components.BotStateManager.Update" -
/// true, but it also meant the whole rest of that Update (the bot state batch of that tick) was skipped, and
/// on the client only the first native subscriber of the frame ran.
/// </para>
///
/// <para>
/// The three observations of that failure (2026-10-11 17:19, 2026-10-13 09:00, 2026-10-13 14:08; the last one
/// is analysis/logs-coop4-20261013-140826/Fika-diagnostics.log:28439) all sit in the window in which the
/// world is built and the bots are spawned - once before <c>SessionRun</c>, twice while the bots were created
/// - which is also why no "do not call it in that state" guard can be written for it: the event carries no
/// state of its own and the failing handler cannot be identified from the delegate. So the fix is isolation
/// plus a named report, and the frame keeps its remaining work.
/// </para>
///
/// <para>
/// Bounds: the first failure of a signature is written in full to the diagnostics file (the complete il2cpp
/// stack, the line a report needs), the following ones of the same signature at 1, 2, 4, 8 ... occurrences,
/// and everything between them is counted only. Nothing here throws.
/// </para>
/// </summary>
internal static class NativeHandlerFault
{
    private const int MaxTextLength = 300;

    private static readonly object Gate = new();
    private static string _signature;
    private static int _count;
    private static bool _detailed;

    /// <summary>
    /// Reports one failure of a natively subscribed handler and returns; the caller continues with the rest of
    /// its frame, which is what the engine's own loop does with an exception that leaves a native handler.
    /// </summary>
    internal static void Report(Exception exception)
    {
        try
        {
            var signature = Signature(exception);
            int count;
            bool withException;
            lock (Gate)
            {
                if (!string.Equals(signature, _signature, StringComparison.Ordinal))
                {
                    _signature = signature;
                    _count = 0;
                    _detailed = false;
                }

                _count++;
                count = _count;
                withException = !_detailed;
                if (withException)
                {
                    _detailed = true;
                }
                else if ((count & (count - 1)) != 0)
                {
                    // Already reported this signature at the previous power of two: count only.
                    return;
                }
            }

            var line = "[NativeUpdate] a natively subscribed handler of this event threw inside its own "
                + "native frame and was isolated; the frame continues (the state batch of this tick and the "
                + "other subscribers are not skipped any more): " + Describe(exception)
                + (count > 1 ? " | occurrence #" + count + " of this signature" : string.Empty);

            try
            {
                FikaPlugin.Instance?.FikaLogger.LogWarning(line);
            }
            catch (Exception)
            {
            }

            if (withException)
            {
                
            }
            else
            {
                
            }
        }
        catch (Exception)
        {
            // A reporter that breaks the pump it protects is worse than the failure it reports.
        }
    }

    /// <summary>
    /// Type, first line of the message and the innermost native frame: what makes two failures "the same" for
    /// the report bound, and what a reader needs to recognize the family in the next session.
    /// </summary>
    private static string Signature(Exception exception)
    {
        try
        {
            var message = exception?.Message;
            return exception?.GetType().Name + "|" + FirstLine(message) + "|" + NativeFrame(message);
        }
        catch (Exception)
        {
            return "<unreadable>";
        }
    }

    private static string Describe(Exception exception)
    {
        try
        {
            var message = FirstLine(exception?.Message);
            var frame = NativeFrame(exception?.Message);
            var text = exception?.GetType().Name + ": " + Shorten(message);
            if (!string.IsNullOrEmpty(frame))
            {
                text += " | first native frame: " + frame;
            }

            return text;
        }
        catch (Exception)
        {
            return "<unreadable exception>";
        }
    }

    /// <summary>
    /// The innermost frame of the il2cpp block. Il2CppInterop renders the native trace inside the exception
    /// message ("--- BEGIN IL2CPP STACK TRACE ---" followed by one line per frame), so this is a text read,
    /// not a reflection over the runtime.
    /// </summary>
    private static string NativeFrame(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var begin = message.IndexOf("IL2CPP STACK TRACE", StringComparison.Ordinal);
        if (begin < 0)
        {
            return null;
        }

        var at = message.IndexOf(" at ", begin, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var end = message.IndexOf('\n', at);
        var line = end < 0 ? message.Substring(at) : message.Substring(at, end - at);
        return line.Trim();
    }

    private static string FirstLine(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "<no message>";
        }

        var end = message.IndexOf('\n');
        return end < 0 ? message : message.Substring(0, end);
    }

    private static string Shorten(string text)
    {
        if (text == null)
        {
            return "<no message>";
        }

        return text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) + "..." : text;
    }
}
