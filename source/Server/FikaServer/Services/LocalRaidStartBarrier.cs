#nullable enable

namespace FikaServer.Services;

/// <summary>
/// Tracks StartLocalRaid requests admitted before a match begins ending.
/// The owner closes admission under the match lock, then waits for this
/// barrier before finalizing insurance. Keeping the counter separate from
/// the insurance state also lets a request finish token registration even
/// when native EndLocalRaid has already completed.
/// </summary>
public sealed class LocalRaidStartBarrier
{
    private readonly object _gate = new();
    private int _inFlight;
    private TaskCompletionSource<bool>? _completed;

    public int InFlight
    {
        get
        {
            lock (_gate) return _inFlight;
        }
    }

    /// <summary>
    /// Reserves one StartLocalRaid request when admission is still open.
    /// </summary>
    public bool TryReserve(bool admissionOpen)
    {
        if (!admissionOpen) return false;

        lock (_gate)
        {
            // A caller can reserve only while its FikaMatch lock is held.
            // Keep an existing waiter when another request is already in
            // flight; EndMatch closes admission before it waits.
            _inFlight++;
            return true;
        }
    }

    /// <summary>
    /// Releases one reservation and wakes a waiter when the last request is
    /// done. Returns false for an unmatched/double release.
    /// </summary>
    public bool Release()
    {
        TaskCompletionSource<bool>? completed = null;
        lock (_gate)
        {
            if (_inFlight <= 0) return false;

            _inFlight--;
            if (_inFlight == 0)
            {
                completed = _completed;
                _completed = null;
            }
        }

        completed?.TrySetResult(true);
        return true;
    }

    /// <summary>
    /// Waits for all reservations which existed when admission was closed.
    /// A timeout keeps a faulted request from blocking teardown forever.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout)
    {
        Task? pending;
        lock (_gate)
        {
            if (_inFlight == 0) return true;
            _completed ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pending = _completed.Task;
        }

        var completed = await Task.WhenAny(pending, Task.Delay(timeout))
            .ConfigureAwait(false);
        return completed == pending;
    }
}
