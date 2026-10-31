using System;
using System.Collections.Generic;
using Comfort.Common;
using Fika.Core.Main.Utils;
using UnityEngine;

namespace Fika.Core.Main.HostClasses;

/// <summary>
/// Host inventory operations are dispatched fire-and-forget. When an operation never reaches a
/// terminal result, the native EFT.PickUpState callback is never completed and the player stays in
/// the pickup state until CoopGame's 5 s interaction watchdog releases it. This watchdog fails and
/// releases the operation after 10 s, so a lost callback cannot lock pickup for the rest of the raid.
/// </summary>
internal static class HostOperationWatchdog
{
    private const float TimeoutSeconds = 10f;

    private sealed class Entry
    {
        internal HostInventoryOperationHandler Handler;
        internal float Since;
        internal string Label;
    }

    private static readonly List<Entry> Pending = new();

    internal static void Track(HostInventoryOperationHandler handler, string label)
    {
        try
        {
            if (handler == null)
            {
                return;
            }

            lock (Pending)
            {
                Pending.Add(new Entry { Handler = handler, Since = Time.unscaledTime, Label = label });
            }
        }
        catch
        {
        }
    }

    internal static void Tick()
    {
        try
        {
            List<Entry> snapshot;
            lock (Pending)
            {
                if (Pending.Count == 0)
                {
                    return;
                }

                snapshot = new List<Entry>(Pending);
            }

            var now = Time.unscaledTime;
            foreach (var entry in snapshot)
            {
                var handler = entry.Handler;
                if (handler == null || handler.Completed || handler.Operation == null)
                {
                    lock (Pending)
                    {
                        Pending.Remove(entry);
                    }

                    continue;
                }

                if (now - entry.Since < TimeoutSeconds)
                {
                    continue;
                }

                lock (Pending)
                {
                    Pending.Remove(entry);
                }

                FikaGlobals.LogWarning($"[LootFix] host-op-timeout label={entry.Label} " +
                    $"age={now - entry.Since:0.0}s", "HostOperationWatchdog");

                var controller = handler.InventoryController;
                try
                {
                    handler.Callback?.Fail("host operation timed out", 1);
                }
                catch
                {
                }

                try
                {
                    handler.CancelDispatch();
                }
                catch
                {
                }

                try
                {
                    NativeInteractionRelease.Release(controller?.FikaPlayer, "host-op-timeout");
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
