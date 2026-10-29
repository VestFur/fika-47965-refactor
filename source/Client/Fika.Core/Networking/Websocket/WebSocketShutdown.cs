using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Fika.Core.Networking.Websocket;

/// <summary>
/// What a Fika web socket loop looks like when the application is going away.
///
/// Closing a client socket (<c>Close</c>/<c>Dispose</c>/<c>Abort</c>) does not silently end the reads that
/// are waiting on it: they come back as a cancellation, an aborted stream, or an object that is already
/// disposed - <c>OperationCanceledException</c>, <c>IOException</c> ("The read operation failed"),
/// <c>WebSocketException</c>, <c>ObjectDisposedException</c>. Every one of those used to travel through the
/// generic <c>catch</c> of the loops and come out as a warning line, while the cancellation of a
/// <c>Task.Delay(token)</c> escaped the loop entirely as a faulted fire-and-forget task
/// (2026-10-10, frame=21499: 13 exception blocks and 286 diagnostic lines in the last 90 ms of the run).
///
/// Two small helpers are all that is needed to tell that apart from a real socket problem: the condition
/// is "the stop token is already cancelled", and it is checked by the callers.
/// </summary>
internal static class WebSocketShutdown
{
    /// <summary>True for the exceptions a closed or aborted socket releases instead of a clean end.</summary>
    internal static bool IsTeardown(Exception exception) =>
        exception is OperationCanceledException or IOException or ObjectDisposedException or WebSocketException
            or SocketException;

    /// <summary>
    /// Waits before the next reconnect attempt and returns false when the stop token was cancelled while
    /// waiting. <c>Task.Delay(delay, token)</c> raises <c>TaskCanceledException</c> instead - it is awaited
    /// outside the loop's try block, so at shutdown it faulted the loop's own task and the first-chance
    /// probe recorded it as a teardown exception.
    /// </summary>
    internal static async Task<bool> WaitBeforeReconnectAsync(TimeSpan delay, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return false;
        }

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetResult(true)))
        {
            var completed = await Task.WhenAny(cancelled.Task, Task.Delay(delay)).ConfigureAwait(false);
            return completed != cancelled.Task && !token.IsCancellationRequested;
        }
    }
}
