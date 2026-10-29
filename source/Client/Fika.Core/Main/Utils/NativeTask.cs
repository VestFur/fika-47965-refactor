using System;
using System.Threading.Tasks;
using Comfort.Common;

namespace Fika.Core.Main.Utils;

/// <summary>Completes native tasks only after their managed work has finished.</summary>
public static class NativeTask
{
    public static async Task AsManaged(this EFT.AssetsManager.LoadSceneOperation operation)
    {
        await operation;
    }

    // The generated IL2CPP awaiter forwards MoveNext directly to its native
    // callback. It cannot restore a CLR SynchronizationContext. Returning a
    // CLR Task lets the C# awaiter retain the caller's Unity dispatcher across
    // both native completion and any managed HTTP/delay await that follows.
    public static Task AsManaged(this Il2CppSystem.Threading.Tasks.Task task)
    {
        var source = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = task.GetAwaiter();
        void Finish()
        {
            try
            {
                if (task.IsCanceled) { source.TrySetCanceled(); return; }
                awaiter.GetResult();
                source.TrySetResult(null);
            }
            catch (Exception exception) { source.TrySetException(exception); }
        }
        if (awaiter.IsCompleted) Finish();
        else awaiter.OnCompleted((Action)Finish);
        return source.Task;
    }

    public static Task<T> AsManaged<T>(this Il2CppSystem.Threading.Tasks.Task<T> task)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = task.GetAwaiter();
        void Finish()
        {
            try
            {
                if (task.IsCanceled) { source.TrySetCanceled(); return; }
                source.TrySetResult(awaiter.GetResult());
            }
            catch (Exception exception) { source.TrySetException(exception); }
        }
        if (awaiter.IsCompleted) Finish();
        else awaiter.OnCompleted((Action)Finish);
        return source.Task;
    }

    public static Il2CppSystem.Threading.Tasks.Task FromManaged(Task task)
    {
        var source = new Il2CppSystem.Threading.Tasks.TaskCompletionSource<Il2CppSystem.Object>();
        Complete(task, source);
        return source.Task;
    }

    public static Il2CppSystem.Threading.Tasks.Task<T> FromManaged<T>(Task<T> task)
    {
        var source = new Il2CppSystem.Threading.Tasks.TaskCompletionSource<T>();
        Complete(task, source);
        return source.Task;
    }

    private static async void Complete(Task task, Il2CppSystem.Threading.Tasks.TaskCompletionSource<Il2CppSystem.Object> source)
    {
        try
        {
            await task;
            MainThreadDispatcher.Post(() => source.TrySetResult(null));
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
            MainThreadDispatcher.Post(() => source.TrySetCanceled());
        }
        catch (Exception exception)
        {
            MainThreadDispatcher.Post(() => source.TrySetException(new Il2CppSystem.Exception(exception.ToString())));
        }
    }

    private static async void Complete<T>(Task<T> task, Il2CppSystem.Threading.Tasks.TaskCompletionSource<T> source)
    {
        try
        {
            var result = await task;
            MainThreadDispatcher.Post(() => source.TrySetResult(result));
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
            MainThreadDispatcher.Post(() => source.TrySetCanceled());
        }
        catch (Exception exception)
        {
            MainThreadDispatcher.Post(() => source.TrySetException(new Il2CppSystem.Exception(exception.ToString())));
        }
    }
}
