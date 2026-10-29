namespace Fika.Core.Main.Utils;

/// <summary>Preserves native delegate equality, including duplicate subscriptions.</summary>
internal sealed class NativeActionEvent
{
    private System.Action _handlers;
    private Il2CppSystem.Action _nativeHandlers;

    public void Add(System.Action handler) =>
        _handlers = System.Delegate.Combine(_handlers, handler) as System.Action;

    public void Remove(System.Action handler) =>
        _handlers = System.Delegate.Remove(_handlers, handler) as System.Action;

    // Game events are raised with Il2CppSystem.Action. A native delegate has no
    // managed counterpart, so it is kept as-is and invoked through the interop.
    public void Add(Il2CppSystem.Action handler) =>
        _nativeHandlers = _nativeHandlers == null ? handler : _nativeHandlers + handler;

    public void Remove(Il2CppSystem.Action handler) =>
        _nativeHandlers = _nativeHandlers == null ? null : _nativeHandlers - handler;

    public void Invoke()
    {
        _handlers?.Invoke();
        InvokeNative();
    }

    /// <summary>
    /// (analysis/session-log-archive) The native half of the event, isolated from the caller.
    /// <para>
    /// A native subscriber throws inside its own frame here - the measured one is
    /// <c>BotGroupHostility.UpdateHostilityStatusForAllPlayers</c> ->
    /// <c>OwnedGroupCalcActionNextFrame</c> (NullReference) on the host's <c>UpdateByUnity</c> event, observed
    /// while the raid world was still being built. Without this catch the exception travelled out of the pump
    /// (<c>BotStateManager.Update</c> -> <c>HostGameController.InvokeUpdate</c>) into
    /// <c>NativeManagedInvoker.Shim</c>, which logged it as a shim failure - and the rest of that frame's work
    /// (the bot state batch) was skipped with it.
    /// </para>
    /// <para>
    /// The engine's own loop behaves the way this does: a MonoBehaviour Update that throws is logged and the
    /// next frame runs again. The subscribers after the failing one are still skipped for that one frame,
    /// which is also what a multicast delegate does natively; what is new is that the failure is named instead
    /// of being attributed to the pump, and that the pump keeps its remaining work.
    /// </para>
    /// </summary>
    private void InvokeNative()
    {
        var handlers = _nativeHandlers;
        if (handlers == null)
        {
            return;
        }

        try
        {
            handlers.Invoke();
        }
        catch (System.Exception exception)
        {
            NativeHandlerFault.Report(exception);
        }
    }

    public void Clear()
    {
        _handlers = null;
        _nativeHandlers = null;
    }
}
