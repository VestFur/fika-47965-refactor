using System;
using System.Collections.Generic;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.BaseClasses;

/// <summary>Associates native world controllers with their managed co-op state.</summary>
public static class FikaWorldControllers
{
    private static readonly Dictionary<IntPtr, Entry> Controllers = new();

    /// <summary>
    /// One registered controller plus the world generation it belongs to (analysis/transit-crash-unload).
    /// The epoch is what makes a lookup safe across a map change: the native address of a world controller
    /// can be handed out again by the allocator after the previous world was released, and a lookup keyed
    /// by the raw pointer alone would then return the *old* managed controller for the *new* native object -
    /// the shape in which a controller talks to a world that is gone.
    /// </summary>
    private readonly struct Entry(IDisposable controller, int epoch)
    {
        internal readonly IDisposable Controller = controller;
        internal readonly int Epoch = epoch;
    }

    private static int _staleReports;

    internal static void Add(Il2CppObjectBase native, IDisposable controller) =>
        Controllers[native.Pointer] = new Entry(controller, WorldLifecycle.Epoch);

    internal static void Remove(Il2CppObjectBase native) => Controllers.Remove(native.Pointer);

    public static T Find<T>(Il2CppObjectBase native) where T : class
    {
        if (native == null || !Controllers.TryGetValue(native.Pointer, out var entry))
        {
            return null;
        }

        if (!WorldLifecycle.IsCurrent(entry.Epoch))
        {
            // The controller belongs to a world that went down: drop it and report the first few cases, so a
            // run in which the release of a world controller is missed says so by name.
            Controllers.Remove(native.Pointer);
            if (_staleReports < 8)
            {
                _staleReports++;
                FikaGlobals.LogWarning("[WorldLifecycle] a " + typeof(T).Name + " of world epoch " + entry.Epoch
                    + " was looked up while epoch " + WorldLifecycle.Epoch + " is current; the entry was dropped"
                    + " instead of being used on the native object at " + native.Pointer.ToInt64().ToString("X"));
            }

            return null;
        }

        return entry.Controller as T;
    }

    /// <summary>
    /// Drops every entry at a world teardown and returns how many there were. The controllers are *not*
    /// disposed here: their native owners are already gone, and a Dispose would call into the released
    /// native object (the access violation this method exists to prevent). The managed wrappers are
    /// collected normally once nothing references them.
    /// </summary>
    internal static int ClearForWorldTeardown()
    {
        var count = Controllers.Count;
        if (count > 0)
        {
            Controllers.Clear();
        }

        return count;
    }
}

public abstract class FikaWorldController<TNative> : IDisposable where TNative : Il2CppObjectBase
{
    private bool _disposed;

    protected FikaWorldController(TNative native)
    {
        Native = native;
        FikaWorldControllers.Add(native, this);
    }

    public TNative Native { get; }

    public void Dispose()
    {
        if (_disposed) return;
        try { DisposeNative(); }
        finally { OnNativeDisposed(); }
    }

    internal void OnNativeDisposed()
    {
        if (_disposed) return;
        _disposed = true;
        FikaWorldControllers.Remove(Native);
        DisposeManaged();
    }

    protected abstract void DisposeNative();
    protected virtual void DisposeManaged() { }
}
