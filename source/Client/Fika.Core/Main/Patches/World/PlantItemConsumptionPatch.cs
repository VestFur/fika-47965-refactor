using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

/// <summary>
/// AU (analysis/plant-item-consumption): the "the charge is still in the backpack after a successful
/// plant" repair.
///
/// Where the native build takes a planted item out of the inventory was pinned down from the 4.0
/// decompile:
///
///   EFT.Player.PlantItemNetwork(string itemId, string zoneId, bool successful)   // the plant
///     -> EFT.Player.PlantItem(itemId, zoneId, successful)
///          -> Profile.ItemDroppedAtPlace(itemId, zoneId)                          // a note only
///          -> EFT.Player.vmethod_1(operation, callback)                           // the item action
///               -> plant operation(id, controller, discardResult, zone, ...)       // GClass3498
///                    -> InteractionsHandlerClass.Discard(item, controller, true)
///                         => DiscardResult                                 // the cached removal
///
/// (Player.cs:26159-26175 for the entry point, Player.cs:1396-1412 for the operation, and
/// Player.cs:23049-23055 for the shape the same Discard-plus-RemoveOperation pair has on the amulet
/// path: ItemManipulator.Discard, then a RemoveOperation built from its DiscardResult.)
///
/// AY correction (analysis/plant-client-host-crash), from the same decompile: there is no plant
/// operation to run in this build. EFT.Player.PlantItem is exactly one line -
/// Profile.ItemDroppedAtPlace(itemId, zoneId) (eft40-decompiled/EFT/Player.cs:26164-26170) - and
/// Player.PlantItemLocalOnly (Player.cs:26172-26175) only forwards to it; the vmethod_1/plant-operation
/// chain above exists in GClass1979/Player.cs around the amulet path, not behind PlantItem. So the
/// charge really is never removed by the game here, the Discard-plus-RemoveOperation pair below is the
/// only removal that will happen, and on a client it can only travel through the ordinary
/// inventory-operation relay. That relay works: the host answered the client's first relayed
/// RemoveOperation with Succeeded (logs-coop2-20261013-110333/LogOutput.log:2933).
///
/// The hook is the gate that answers "is the charge there" -
/// EFT.GameTriggers.ItemTriggerRequirement.TryRedeem(InventoryController, SkillManager) - because that
/// is the moment the plant is committed to (the map's HandlerRequirementsWithFailedTrigger fires the
/// plant trigger right after it) and because the live r16/cgok logs prove the item is still in the
/// inventory after it answered true (logs-cgok-20261013-004053/LogOutput.log:3322-3323, from the same
/// build that runs this patch). Whatever the plant's own removal is called, one thing is invariant:
/// the requirement has been redeemed, so the item it was redeemed with must leave the inventory.
///
/// This patch invents nothing: it searches the local player's own inventory for the template the
/// requirement asked for, and when exactly one item matches, it runs exactly the two native steps
/// above - ItemManipulator.Discard plus the RemoveOperation built from that DiscardResult - through
/// the player's own controller, i.e. host player locally and client player through the port's
/// inventory operation relay like every other inventory action. When the item is already gone (native
/// consumed it, or a second handler fires for the same requirement) nothing is executed, so no plant
/// ever consumes twice, and when the template matches more than one item nothing is executed either -
/// the repair never guesses.
///
/// AY: "nothing is executed twice" was still wrong in one place - the gate itself is answered twice by
/// the map handler for a single plant, and the removal is dispatched asynchronously, so presence in the
/// inventory is not a "was it already consumed" test. One plant therefore sent two identical removals
/// (LogOutput.log:2931-2932); the host applied the first and refused the second, and that refusal
/// resynced the client's item graph and aborted the plant (LogOutput.log:2934-2938). The ledger below
/// makes the dispatch exactly-once per item, which is what the native code does.
///
/// Switches: Fika.cfg -> [Gameplay] -> "Plant Consumes Item" (default on) and "Plant Consume On Client"
/// (default on; off keeps the removal strictly on the host, so a client's plant consumes nothing and
/// sends nothing). Evidence lines: [PlantDiag] consume: ...
/// </summary>
internal sealed class ItemTriggerRequirement_TryRedeem_Consume_Patch : ModulePatch
{
    /// <summary>
    /// AY (analysis/plant-client-host-crash): the removals this machine has already dispatched, so one
    /// plant never dispatches two.
    ///
    /// The native map handler answers this gate twice for a single plant - proven in the host run
    /// (analysis/plant-item-consumption quotes logs-cgok-20261013-004053/LogOutput.log:3319-3324,
    /// "op=TryRedeem -> result=True (x2)") and again by the two identical sends in
    /// logs-coop2-20261013-110333/LogOutput.log:2931-2932. The old guard ("the item is already gone,
    /// so a second call does nothing") cannot catch that: controller.Execute is asynchronous
    /// (ClientInventoryController.cs:119-131 queues RunClientOperation and yields), so the second call
    /// still found the charge in the inventory and sent a second removal for it. On a client that
    /// second removal was refused by the host ("Could not find item with id", LogOutput.log:2934), and
    /// the refusal drove an authoritative inventory resync that released the plant interaction
    /// mid-animation (LogOutput.log:2936-2938) - the whole reported crash chain starts here.
    ///
    /// The ledger is keyed by the item id and expires after
    /// <see cref="DispatchLedgerTtlSeconds"/>, so a later raid (or the same item removed for real
    /// later) is never blocked by an old entry.
    /// </summary>
    private const float DispatchLedgerTtlSeconds = 60f;

    private const int DispatchLedgerLimit = 64;

    private static readonly Dictionary<string, float> _dispatched =
        new(StringComparer.Ordinal);

    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(EFT.GameTriggers.ItemTriggerRequirement)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "TryRedeem" && method.GetParameters().Length == 2);
        if (target == null)
        {
            throw new MissingMethodException("EFT.GameTriggers.ItemTriggerRequirement.TryRedeem(InventoryController, SkillManager)");
        }
        return target;
    }

    [PatchPostfix]
    public static void Postfix(EFT.GameTriggers.ItemTriggerRequirement __instance, InventoryController __0, ref bool __result)
    {
        try
        {
            if (!__result)
            {
                return;
            }

            ConsumeRedeemedItem(__instance, __0);
        }
        catch (Exception exception)
        {
                    }
    }

    private static void ConsumeRedeemedItem(EFT.GameTriggers.ItemTriggerRequirement requirement, InventoryController controller)
    {
        if (FikaRaidGate.Skip("ItemTriggerRequirement.TryRedeem consume"))
        {
            return;
        }

        if (!PlantDiag.ConsumesItem)
        {
            
            return;
        }

        // AY: the side policy. The repair belongs to the inventory that owns the item, and on a
        // client that inventory is authoritative on the host, so the removal has to be relayed
        // (ClientInventoryController.RunClientOperation) - which the host applies, proven by
        // logs-coop2-20261013-110333/LogOutput.log:2933. "Plant Consume On Client" = false keeps the
        // removal strictly on the host: a client's plant then consumes nothing and sends nothing.
        if (!PlantDiag.ConsumeOnClient && !PlantDiag.IsHostMachine)
        {
            
            return;
        }

        // Only the inventory this machine owns may be changed: the controller the requirement was
        // handed has to be the local player's own, exactly like the requirement diagnostics.
        if (!PlantDiag.IsLocalController(controller))
        {
            
            return;
        }

        var player = PlantDiag.LocalPlayer;
        if (player == null || controller == null)
        {
            
            return;
        }

        var item = FindRedeemedItem(player, requirement);
        if (item == null)
        {
            // The native chain already removed it, or the template matches no single item. Never
            // invent an item; 
            return;
        }

        var reference = PlantDiag.ItemRef(item);
        if (AlreadyDispatched(item))
        {
            
            return;
        }

        

        // Step 1 - the same simulation the native plant operation is built from.
        OperationResult<DiscardResult> discard;
        try
        {
            discard = ItemManipulator.Discard(item, controller, true);
        }
        catch (Exception exception)
        {
            
            return;
        }

        if (discard == null || discard.Failed || discard.Value == null)
        {
            var error = discard == null ? "null result" : DescribeDiscard(discard);
            
            return;
        }

        // Step 2 - the native step that executes that discard: a RemoveOperation over the plant
        // controller, executed through the controller so a client plant is relayed to the host like
        // every other inventory operation. The callback carries the answer of that execution.
        try
        {
            var operation = new RemoveOperation(controller.GetAndIncrementNextOperationId(), controller, discard.Value);
            var callback = (Comfort.Common.Callback)(result => ReportExecution(player, reference));
            controller.Execute(operation, callback);
            // AY: the removal is in flight now, so the second answer of the same plant must not send
            // a second one. Only a dispatch that returned counts; a throw keeps the door open.
            NoteDispatched(item);
        }
        catch (Exception exception)
        {
            
            return;
        }

        // The host executes an operation asynchronously, so this first read is only the immediate
        // state; the step=callback line above is the one that judges the execution.
        var presentAfter = FindRedeemedItem(player, requirement) != null;
        
    }

    /// <summary>
    /// The answer of the dispatched removal, so a run distinguishes "dispatched but refused later"
    /// from "dispatched and finished".
    /// </summary>
    private static void ReportExecution(FikaPlayer player, string reference)
    {
        try
        {
            var present = player != null && PlantDiag.FindLocalByTemplate(PlantDiag.LastRequirementFilter) != null;
            
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// The item the requirement was redeemed with: the template it asked for, but only when that
    /// template is present exactly once in the local inventory - the repair never picks between
    /// candidates.
    /// </summary>
    private static Item FindRedeemedItem(FikaPlayer player, EFT.GameTriggers.ItemTriggerRequirement requirement)
    {
        var filter = Filter(requirement);
        if (string.IsNullOrEmpty(filter))
        {
            return null;
        }

        try
        {
            var inventory = player.Profile?.Inventory;
            if (inventory == null)
            {
                return null;
            }

            var matches = inventory.GetAllItemByTemplate(filter);
            if (matches == null)
            {
                return null;
            }

            Item found = null;
            var count = 0;
            foreach (var candidate in matches)
            {
                if (candidate == null)
                {
                    continue;
                }

                count++;
                found = candidate;
            }

            if (count == 1)
            {
                return found;
            }

            
            return null;
        }
        catch (Exception exception)
        {
            
            return null;
        }
    }

    private static string Filter(EFT.GameTriggers.ItemTriggerRequirement requirement)
    {
        try
        {
            return requirement?._itemRequirementId ?? "none";
        }
        catch
        {
            return "none";
        }
    }

    /// <summary>Whether this machine already dispatched the removal of this item.</summary>
    private static bool AlreadyDispatched(Item item)
    {
        var key = ItemKey(item);
        if (key == null)
        {
            // An unreadable id must not block a removal the native side would have made.
            return false;
        }

        try
        {
            PruneDispatched();
            return _dispatched.ContainsKey(key);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Records a dispatched removal. Called only after the controller took it.</summary>
    private static void NoteDispatched(Item item)
    {
        var key = ItemKey(item);
        if (key == null)
        {
            return;
        }

        try
        {
            PruneDispatched();
            _dispatched[key] = Time.time;
        }
        catch
        {
            // The ledger only suppresses a duplicate; failing to write it must not stop the repair.
        }
    }

    private static string ItemKey(Item item)
    {
        try
        {
            var id = item?.Id;
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch
        {
            return null;
        }
    }

    private static void PruneDispatched()
    {
        var now = Time.time;
        List<string> stale = null;
        foreach (var entry in _dispatched)
        {
            if (now - entry.Value > DispatchLedgerTtlSeconds)
            {
                (stale ??= []).Add(entry.Key);
            }
        }

        if (stale != null)
        {
            foreach (var key in stale)
            {
                _dispatched.Remove(key);
            }
        }

        if (_dispatched.Count <= DispatchLedgerLimit)
        {
            return;
        }

        List<KeyValuePair<string, float>> ordered = [.. _dispatched];
        ordered.Sort((left, right) => left.Value.CompareTo(right.Value));
        var excess = _dispatched.Count - DispatchLedgerLimit;
        for (var index = 0; index < excess; index++)
        {
            _dispatched.Remove(ordered[index].Key);
        }
    }

    private static string DescribeDiscard(OperationResult<DiscardResult> result)
    {
        try
        {
            return result.Error?.ToString() ?? "unknown";
        }
        catch
        {
            return "unreadable";
        }
    }
}
