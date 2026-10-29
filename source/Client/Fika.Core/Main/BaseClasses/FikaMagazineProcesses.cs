using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using EFT.InventoryLogic;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.BaseClasses;

internal interface IFikaMagazineProcess
{
    bool IsCancelled { get; }
    Task<IResult> Start();
    void Proceed(bool success);
    void TryProceedForItem(Item item);
}

/// <summary>Keeps the game's process handle so native stop/item-change paths reach Fika's batch process.</summary>
internal static class FikaMagazineProcesses
{
    private static readonly Dictionary<IntPtr, Entry> Processes = new();

    internal static void Add(Il2CppObjectBase native, IFikaMagazineProcess process) =>
        Processes[native.Pointer] = new Entry(process, WorldLifecycle.Epoch);

    internal static void Remove(Il2CppObjectBase native) => Processes.Remove(native.Pointer);

    internal static IFikaMagazineProcess Find(Il2CppObjectBase native)
    {
        // AP (analysis/nonfika-raid-inertness): this is the unload/load chain the 2026-10-12 23:52 crash
        // ended in ("Perform UNLOAD" as the last line before the fail fast). The registry is empty outside
        // a raid of ours anyway, so the entry below changes no behaviour - it makes the question explicit
        // and puts [Inert] skip=FikaMagazineProcesses.Find in the log when a native unload asks.
        if (FikaRaidGate.Skip("FikaMagazineProcesses.Find"))
        {
            return null;
        }

        if (native == null || !Processes.TryGetValue(native.Pointer, out var entry))
        {
            return null;
        }

        if (!WorldLifecycle.IsCurrent(entry.Epoch))
        {
            // A magazine process of a released world (analysis/transit-crash-unload): dropped, never entered.
            Processes.Remove(native.Pointer);
            return null;
        }

        return entry.Process;
    }

    /// <summary>Drops every process at a world teardown; returns how many there were.</summary>
    internal static int ClearForWorldTeardown()
    {
        var count = Processes.Count;
        if (count > 0)
        {
            Processes.Clear();
        }

        return count;
    }

    /// <summary>One process plus the world generation it belongs to.</summary>
    private readonly struct Entry(IFikaMagazineProcess process, int epoch)
    {
        internal readonly IFikaMagazineProcess Process = process;
        internal readonly int Epoch = epoch;
    }
}
