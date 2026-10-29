using System;
using System.Threading.Tasks;

namespace Fika.Core.Main.Utils;

/// <summary>Tracks the one early death save; normal raid-end saving remains the fallback.</summary>
internal sealed class RaidSaveState
{
    private TaskCompletionSource<object> _completion;

    internal Task Completion => _completion?.Task ?? Task.CompletedTask;
    internal bool HasSaved => _completion?.Task.Status == TaskStatus.RanToCompletion;

    // Called on the Unity dispatcher. Publish the pending task before invoking
    // the operation so reentrant death/exit callbacks observe the same save.
    internal Task SaveOnce(Func<Task> operation)
    {
        if (_completion != null) return _completion.Task;
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Complete(operation, _completion);
        return _completion.Task;
    }

    private static async Task Complete(Func<Task> operation, TaskCompletionSource<object> completion)
    {
        try
        {
            await operation();
            completion.TrySetResult(null);
        }
        catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
}
