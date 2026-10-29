using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.BaseClasses;

/// <summary>
/// Owns the game's sealed local quest controller. Native callers keep the actual
/// controller, including its local-game connectors, auto-start quests and rewards.
/// Fika state is resolved by native pointer rather than managed wrapper identity.
/// </summary>
public abstract class FikaQuestController : IDisposable
{
    private static readonly Dictionary<IntPtr, Entry> Controllers = new();
    private bool _disposed;
    protected bool IsDisposed => _disposed;

    /// <summary>
    /// One registered controller plus the world generation it belongs to (analysis/transit-crash-unload).
    /// A quest controller is created per raid with its local game; without the epoch a lookup keyed only by
    /// the native pointer can hand the controller of a released raid to the native object of the next one.
    /// </summary>
    private readonly struct Entry(FikaQuestController controller, int epoch)
    {
        internal readonly FikaQuestController Controller = controller;
        internal readonly int Epoch = epoch;
    }

    private static int _staleReports;

    protected FikaQuestController(Profile profile, InventoryController inventoryController,
        IPlayerSearchController searchController, IQuestSession session)
    {
        Native = new QuestControllerClientLocalGame(profile, inventoryController, searchController, session);
        Controllers[Native.Pointer] = new Entry(this, WorldLifecycle.Epoch);
    }

    public QuestControllerClientLocalGame Native { get; }
    public Profile Profile => Native.Profile;
    public InventoryController InventoryController => Native.InventoryController;
    public ConditionalBook<Quest> ConditionalBook => Native.ConditionalBook;
    public QuestBook Quests => Native.Quests;

    public static T Find<T>(ConditionalController<Quest> native) where T : FikaQuestController
    {
        if (native == null || !Controllers.TryGetValue(native.Pointer, out var entry))
        {
            return null;
        }

        if (!WorldLifecycle.IsCurrent(entry.Epoch))
        {
            Controllers.Remove(native.Pointer);
            if (_staleReports < 8)
            {
                _staleReports++;
                FikaGlobals.LogWarning("[WorldLifecycle] a " + typeof(T).Name + " of world epoch " + entry.Epoch
                    + " was looked up while epoch " + WorldLifecycle.Epoch + " is current; the entry was dropped");
            }

            return null;
        }

        return entry.Controller as T;
    }

    /// <summary>
    /// Drops every controller at a world teardown (analysis/transit-crash-unload). Like
    /// <see cref="FikaWorldControllers.ClearForWorldTeardown"/> this deliberately does not call Dispose:
    /// the native quest controller of that world is already gone, and disposing would enter it.
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

    public virtual void Init() => Native.Init();
    public virtual void Run() => Native.Run();

    public void Dispose()
    {
        if (_disposed) return;
        DisposeNative();
        OnNativeDisposed();
    }

    protected virtual void DisposeNative() => Native.Dispose();

    internal void OnNativeDisposed()
    {
        if (_disposed) return;
        _disposed = true;
        Controllers.Remove(Native.Pointer);
        DisposeManaged();
    }

    protected virtual void DisposeManaged() { }
}
