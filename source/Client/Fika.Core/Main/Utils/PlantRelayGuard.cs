using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.Utils;

/// <summary>
/// (analysis/plant-client-host-crash) The host-side guard for the one relayed operation shape that
/// makes the host run native inventory work on a *remote* player's item graph:
/// <c>EFT.RemoveOperationDescriptor</c>.
///
/// What the host does with a client's descriptor today
/// (<c>FikaServer.Callbacks.OnInventoryPacketReceived</c> -> the sender's
/// <c>ObservedInventoryController.CreateOperationFromDescriptor</c>, ObservedInventoryController.cs:320-324)
/// is hand it to the native rebuild: <c>RemoveOperationDescriptor.ToInventoryOperation(IPlayer)</c> =
/// <c>Player.FindItemById</c> + <c>ItemManipulator.Discard(item, controller, simulate: true)</c> +
/// <c>new RemoveOperation(operationId, controller, discardResult)</c>, and the result is then executed
/// natively (4.0 decompile <c>eft40-decompiled/GClass1977.cs:24-37</c>). That chain had never run on a
/// remote player in this install before the first relayed plant (the whole of
/// logs-coop2-20261013-110333/LogOutput.log contains exactly two removal-shaped sends, both from the
/// plant, and one of them was answered with "Could not find item with id").
///
/// This guard is the defensive early exit the relay was missing: a removal the host can already tell it
/// cannot complete - a duplicate of one it just accepted (the plant gate fires twice per plant, see
/// PlantItemConsumptionPatch), a malformed descriptor, or anything the pre-flight cannot read - is
/// refused in managed code, before the native rebuild, with exactly one [PlantGuard] warning line and
/// the same answer every other rejection already gives (Failed + authoritative resync + id resync), so
/// the client is never left waiting and the native path never sees it.
///
/// Cost when nothing is refused: one null test, one il2cpp type test and (only for
/// RemoveOperationDescriptor) one dictionary lookup. Switch: Fika.cfg -> [Gameplay] ->
/// "Plant Relay Guard" (default on).
/// </summary>
internal static class PlantRelayGuard
{
    /// <summary>
    /// How long a removal this host accepted stays in the ledger. Long enough to catch the native
    /// double dispatch of one plant (same frame), short enough that the same charge can be removed
    /// again in a later raid without ever being refused.
    /// </summary>
    private const float LedgerTtlSeconds = 60f;

    /// <summary>Hard cap so a pathological peer cannot grow the ledger without bound.</summary>
    private const int LedgerLimit = 128;

    /// <summary>Removals this host has already committed to execute, keyed by netId + item id.</summary>
    private static readonly Dictionary<string, float> _accepted = new(StringComparer.Ordinal);

    /// <summary>
    /// The behaviour switch. On (default) the pre-flight above runs. Off: the descriptor goes to the
    /// native rebuild exactly as it did before this build (the one-flip rollback).
    /// </summary>
    internal static bool Enabled
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                return settings?.PlantRelayGuard == null || settings.PlantRelayGuard.Value;
            }
            catch
            {
                // Only a guard that can refuse work is being switched: keep it on when the settings
                // object cannot be read.
                return true;
            }
        }
    }

    /// <summary>
    /// Whether this operation must be refused instead of rebuilt. False means "hand it to the relay
    /// exactly as before"; true means the caller must answer Failed + resync and return.
    /// </summary>
    internal static bool TryRefuse(FikaPlayer sender, InventoryOperationDescriptor descriptor, out string reason)
    {
        reason = null;
        try
        {
            if (!Enabled)
            {
                return false;
            }

            if (descriptor == null)
            {
                reason = "no-descriptor";
                return true;
            }

            // Only the destroy-shaped descriptor is pre-flighted. Every other operation keeps the
            // pre-guard path, so ordinary traffic pays one type test and nothing else.
            if (descriptor.TryCast<RemoveOperationDescriptor>() is not { } removal)
            {
                return false;
            }

            if (sender == null)
            {
                reason = "no-sender";
                return true;
            }

            var itemKey = Key(sender, removal);
            if (itemKey == null)
            {
                // A removal without an item id cannot be rebuilt by the native side either; refusing
                // it here is the same answer with one line of evidence.
                reason = "empty-item-id";
                return true;
            }

            Prune();

            if (_accepted.ContainsKey(itemKey))
            {
                reason = "duplicate-removal";
                return true;
            }

            // The host is about to commit to this removal. Note it before the native rebuild so a
            // second copy of the same removal (the same plant, the same tick) is refused above.
            _accepted[itemKey] = Time.time;
            ReportAccepted(sender.NetId, removal, itemKey);
            return false;
        }
        catch (Exception exception)
        {
            // A guard that throws must not decide the outcome: refuse, so the native path stays closed.
            reason = "preflight-threw: " + exception.Message;
            return true;
        }
    }

    /// <summary>
    /// The one line the host writes when it refuses an operation it will not rebuild. Deliberately not
    /// behind the probe switch: a refusal is rare and it is the host-side half of the evidence
    /// ("the client sent something this host would not execute").
    /// </summary>
    internal static void ReportRefusal(int netId, ushort callbackId, InventoryOperationDescriptor descriptor, string reason)
    {
        try
        {
            FikaGlobals.LogWarning($"[PlantGuard] host-refuse-unsupported netId={netId} op={callbackId} " +
                $"descriptor={Describe(descriptor)} reason={reason} " +
                "-> answered Failed + authoritative inventory, the native rebuild was never entered");
        }
        catch
        {
            // Reporting must never take the packet handler down.
        }
    }

    /// <summary>
    /// The host-side counterpart of the client's "Sending inventory operation ... (RemoveOperation)"
    /// line: which removal this host accepted for which peer. Probe-gated (zero cost by default).
    /// </summary>
    private static void ReportAccepted(int netId, RemoveOperationDescriptor removal, string itemKey)
    {
        

        try
        {
            FikaGlobals.LogInfo($"[PlantGuard] host-accept-removal netId={netId} op={removal.OperationId} " +
                $"item={removal.ItemId} key={itemKey} side=host step=preflight");
        }
        catch
        {
            // Diagnostics must never change the outcome.
        }
    }

    internal static string Describe(InventoryOperationDescriptor descriptor)
    {
        try
        {
            return descriptor == null ? "none" : descriptor.GetIl2CppType()?.Name ?? descriptor.ToString();
        }
        catch
        {
            return "unreadable";
        }
    }

    private static string Key(FikaPlayer sender, RemoveOperationDescriptor removal)
    {
        try
        {
            var itemId = removal.ItemId.ToString();
            return string.IsNullOrEmpty(itemId) ? null : $"{sender.NetId}:{itemId}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Drops the ledger's stale entries, and the oldest ones when it is at its cap.</summary>
    private static void Prune()
    {
        var now = Time.time;
        List<string> stale = null;
        foreach (var entry in _accepted)
        {
            if (now - entry.Value > LedgerTtlSeconds)
            {
                (stale ??= []).Add(entry.Key);
            }
        }

        if (stale != null)
        {
            foreach (var key in stale)
            {
                _accepted.Remove(key);
            }
        }

        if (_accepted.Count <= LedgerLimit)
        {
            return;
        }

        // The cap can only be reached by a peer that removed more items than a raid can hold: keep the
        // newest entries and let the oldest go rather than growing without bound.
        List<KeyValuePair<string, float>> ordered = [.. _accepted];
        ordered.Sort((left, right) => left.Value.CompareTo(right.Value));
        var excess = _accepted.Count - LedgerLimit;
        for (var index = 0; index < excess; index++)
        {
            _accepted.Remove(ordered[index].Key);
        }
    }
}
