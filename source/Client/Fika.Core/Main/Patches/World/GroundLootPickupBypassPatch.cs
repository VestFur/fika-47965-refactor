using System;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using Diz.LanguageExtensions;
using Diz.Utils;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

/// <summary>
/// Client side ground loot pickup that skips the native interaction state machine.
/// The native take runs inside EFT.PickUpState and, when its completion callback is
/// lost, leaves the player locked in that state: the movement state machine refuses
/// view rotation, movement and crouch, and MovementContext.CanInteract answers
/// "Can't interact in Pickup", so no interaction prompt appears and ground loot can
/// never be taken again for the rest of the raid.
/// Intercepting InteractionContextHelper.PickupAction lets us run the same inventory
/// operation the verified startup probe uses (ItemManipulator placement +
/// ConvertOperationResultToOperation + Execute) and skip entering that state.
/// The host stays authoritative: the operation still travels through
/// ClientInventoryController, so loot cannot be duplicated locally.
/// </summary>
internal sealed class InteractionContextHelper_PickupAction_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(InteractionContextHelper)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name == "PickupAction"
                && x.GetParameters().Length == 5
                && x.GetParameters()[0].ParameterType == typeof(Player)
                && x.GetParameters()[2].ParameterType == typeof(Item));
        if (target == null)
        {
            throw new MissingMethodException(
                "InteractionContextHelper.PickupAction(Player, IItemOperationResult, Item, IPlayer, Action)");
        }
        return target;
    }

    [PatchPrefix]
    public static bool Prefix(Player __0, Item __2)
    {
        // 2026-10-12 (analysis/pickup-auto-fallback): this used to be client-only, which made
        // the switch below unreachable on a host - and the round-12 raid that got stuck in
        // EFT.PickUpState ran on the host (the log of that raid contains no [LootDiag]
        // pickup-request line at all, because this method returned before its first line).
        // The condition is now "the machine's own player in a Fika raid", host or client.
        if (__0 == null || __2 == null || !GroundLootPickup.IsFikaLocalPlayer(__0))
        {
            return true;
        }

        // 2026-10-12 (analysis/pickup-stuck-again + analysis/pickup-auto-fallback): the native
        // action relays a take through the inventory controller like every other inventory
        // action, but it does so inside EFT.PickUpState, and that state can stay entered for the
        // rest of the raid. Off by default; three modes (see GroundLootPickup):
        //   native       - the native action runs (default)
        //   bypass(cfg)  - Fika.cfg -> [Gameplay] -> "Ground Loot Pickup Bypass" = true
        //   bypass(auto) - a stuck pickup was detected in this raid, so the take now skips the
        //                  native pickup state (Fika.cfg -> [Gameplay] ->
        //                  "Ground Loot Pickup Auto Fallback" = false turns that switch off)
        if (!GroundLootPickup.BypassEnabled)
        {
            GroundLootPickup.DiagNativeTake(__0, __2);
            return true;
        }

        try
        {
            if (GroundLootPickup.TryTake(__0, __2))
            {
                return false;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[PickupBypass] failed for {__2.Id}: {exception}");
        }

        return true;
    }
}

/// <summary>
/// Puts ground loot into the matching equipment slot. Armour, helmets, rigs, backpacks and
/// headsets can only live in an equipment slot, which the generic container placement never
/// offers, so these items used to be left on the ground. Uses the verified native path:
/// InventoryEquipment.GetSlot + Slot.CreateItemAddress + ItemManipulator.Move (dry run) +
/// ConvertOperationResultToOperation + Execute, so the host stays authoritative.
/// </summary>
internal static class GroundLootEquip
{
    private static readonly EquipmentSlot[] CandidateSlots =
    {
        EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.ArmorVest,
        EquipmentSlot.TacticalVest, EquipmentSlot.Backpack
    };

    internal static bool TryEquipFromGround(Player player, Item item, out string error)
    {
        error = null;
        if (player == null || item == null)
        {
            error = "no player or item";
            return false;
        }

        var controller = player.InventoryController;
        var equipment = player.Equipment;
        if (controller == null || equipment == null)
        {
            error = "no inventory controller or equipment";
            return false;
        }

        var preferred = PreferredSlot(item);
        var ordered = new System.Collections.Generic.List<EquipmentSlot>();
        if (preferred != null)
        {
            ordered.Add(preferred.Value);
        }
        foreach (var candidate in CandidateSlots)
        {
            if (preferred == null || candidate != preferred.Value)
            {
                ordered.Add(candidate);
            }
        }

        var reasons = new System.Collections.Generic.List<string>();
        foreach (var slotName in ordered)
        {
            Slot slot;
            try
            {
                slot = equipment.GetSlot(slotName);
            }
            catch (Exception slotException)
            {
                reasons.Add(slotName + ": lookup failed (" + slotException.Message + ")");
                continue;
            }
            if (slot == null)
            {
                continue;
            }
            if (slot.ContainedItem != null)
            {
                continue;
            }
            if (slot.Locked)
            {
                reasons.Add(slotName + ": locked");
                continue;
            }
            bool compatible;
            try
            {
                compatible = slot.CheckCompatibility(item);
            }
            catch (Exception compatibilityException)
            {
                reasons.Add(slotName + ": compatibility check failed (" + compatibilityException.Message + ")");
                continue;
            }
            if (!compatible)
            {
                continue;
            }

            try
            {
                var placement = ItemManipulator.Move(item, slot.CreateItemAddress(), controller, true);
                if (placement.Failed)
                {
                    reasons.Add(slotName + ": " + (placement.Error == null ? "rejected" : placement.Error.ToString()));
                    continue;
                }

                var operation = controller.ConvertOperationResultToOperation(placement.Value);
                if (operation == null)
                {
                    reasons.Add(slotName + ": no operation from the placement result");
                    continue;
                }

                controller.Execute(operation, (Callback)(result =>
                {
                    try
                    {
                        if (result?.Failed == true)
                        {
                            FikaGlobals.LogWarning($"[PickupBypass] equipping {item.Id} into {slotName} failed: {result.Error}");
                        }
                        player.ForceInteractionsChanged();
                    }
                    catch (Exception callbackException)
                    {
                        FikaGlobals.LogError($"[PickupBypass] equipment pickup callback failed: {callbackException}");
                    }
                }));
                return true;
            }
            catch (Exception moveException)
            {
                reasons.Add(slotName + ": " + moveException.Message);
            }
        }

        error = reasons.Count == 0 ? "no equipment slot accepted the item" : string.Join("; ", reasons);
        return false;
    }

    private static EquipmentSlot? PreferredSlot(Item item)
    {
        try
        {
            if (item.TryCast<Armor>() != null) return EquipmentSlot.ArmorVest;
            if (item.TryCast<Headwear>() != null) return EquipmentSlot.Headwear;
            if (item.TryCast<Vest>() != null) return EquipmentSlot.TacticalVest;
            if (item.TryCast<Backpack>() != null) return EquipmentSlot.Backpack;
            if (item.TryCast<Headphones>() != null) return EquipmentSlot.Earpiece;
        }
        catch
        {
            // Type probing must never break the pickup.
        }
        return null;
    }
}

/// <summary>Direct ground loot take used by the bypass patch.</summary>
internal static class GroundLootPickup
{
    /// <summary>
    /// Whether Fika still bypasses the native ground loot take. Off by default since
    /// 2026-10-12 (analysis/pickup-stuck-again): the bypass skipped the native pickup state
    /// and built the inventory operation by hand, and every path where the item could not be
    /// placed returned true - the interaction was consumed, the item stayed on the ground and
    /// nothing ever told the player why. The native action reaches the same host-authoritative
    /// path through ClientInventoryController.
    /// <para>
    /// One flip back to the previous behaviour, no rebuild needed:
    /// Fika.cfg -> [Gameplay] -> "Ground Loot Pickup Bypass" = true.
    /// </para>
    /// </summary>
    internal static bool BypassEnabled
    {
        get
        {
            return CfgBypassEnabled || _autoSwitched;
        }
    }

    /// <summary>The manual switch. Fika.cfg -> [Gameplay] -> "Ground Loot Pickup Bypass".</summary>
    private static bool CfgBypassEnabled
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                return settings?.GroundLootPickupBypass != null && settings.GroundLootPickupBypass.Value;
            }
            catch
            {
                // A missing plugin or settings object must never decide a pickup: fall back
                // to the native path, which is the default anyway.
                return false;
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Pickup path switch (analysis/pickup-auto-fallback, Z4)
    // ---------------------------------------------------------------------------------------
    // native       : default. A ground loot take runs inside EFT.PickUpState (EPlayerState.Pickup).
    // bypass(cfg)  : "Ground Loot Pickup Bypass" = true. Every take uses the bypass.
    // bypass(auto) : a real stuck pickup was observed in this raid (NoteStuckPickup), so the take
    //                now skips EFT.PickUpState for the rest of the raid. No cfg write, no restart:
    //                BypassEnabled is read again on every take.
    internal const string ModeNative = "native";
    internal const string ModeBypassCfg = "bypass(cfg)";
    internal const string ModeBypassAuto = "bypass(auto)";

    private static bool _autoSwitched;
    private static int _switchCount;
    private static string _lastReleaseReason = "none";
    private static float _nextModeDiagTime;
    private static IntPtr _modePlayerPointer;

    /// <summary>
    /// Whether a detected stuck pickup may switch this machine to the bypass (default on).
    /// One cfg line turns the automatic switch off for good, without a rebuild:
    /// Fika.cfg -> [Gameplay] -> "Ground Loot Pickup Auto Fallback" = false.
    /// </summary>
    internal static bool AutoFallbackEnabled
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                return settings?.GroundLootPickupAutoFallback == null
                    || settings.GroundLootPickupAutoFallback.Value;
            }
            catch
            {
                // The switch is a safety net, not a gameplay feature: when the settings object
                // cannot be read, keep the automatic recovery available.
                return true;
            }
        }
    }

    /// <summary>The pickup path this machine uses right now: native, bypass(cfg) or bypass(auto).</summary>
    internal static string Mode
    {
        get
        {
            if (CfgBypassEnabled)
            {
                return ModeBypassCfg;
            }
            return _autoSwitched ? ModeBypassAuto : ModeNative;
        }
    }

    /// <summary>Which side of the raid this machine runs, for the diagnostic lines.</summary>
    internal static string Role
    {
        get
        {
            try
            {
                if (FikaBackendUtils.IsServer)
                {
                    return "host";
                }
                return FikaBackendUtils.IsClient ? "client" : "none";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    /// <summary>One description of the current pickup path, appended to the [LootDiag] lines.</summary>
    internal static string DescribeSwitch()
    {
        return $"mode={Mode} switches={_switchCount} lastRelease={_lastReleaseReason} " +
            $"autoFallback={(AutoFallbackEnabled ? "on" : "off")} role={Role}";
    }

    /// <summary>
    /// Records which release ran last, so "why was the interaction state dropped" is readable from
    /// any [LootDiag] line without cross-referencing the whole log.
    /// </summary>
    internal static void NoteRelease(string reason)
    {
        _lastReleaseReason = string.IsNullOrEmpty(reason) ? "unknown" : reason;
    }

    /// <summary>
    /// Whether this machine's own player is the one taking the item. Only then may Fika change the
    /// take: bots and remote players keep the native action on every machine.
    /// </summary>
    internal static bool IsFikaLocalPlayer(Player player)
    {
        try
        {
            if (player.IsYourPlayer != true)
            {
                return false;
            }
            if (!FikaBackendUtils.IsClient && !FikaBackendUtils.IsServer)
            {
                return false;
            }
            return player.TryCast<Fika.Core.Main.Players.FikaPlayer>() != null;
        }
        catch
        {
            // A throwing player accessor must never decide a pickup: keep the native action.
            return false;
        }
    }

    /// <summary>
    /// The one and only evidence that switches the pickup path: <c>CoopGame</c>'s watchdog saw the
    /// local player sit in <see cref="EPlayerState.Pickup"/> for its whole 5 s budget
    /// (<c>CoopGame.RepairLocalInteractionState</c>, <c>EPlayerState.Pickup =&gt; 5f</c>). A native
    /// take animates for well under two seconds, so five seconds in that state is not a normal
    /// pickup. No other state switches anything: Plant has its own 20 s budget and never reaches
    /// this method with <see cref="EPlayerState.Pickup"/>.
    /// <para>
    /// Idempotent: the switch happens at most once per raid, so repeated detections only refresh
    /// the diagnostic state. The before/after lines around it are the "this take really did flip
    /// the path" evidence for the next raid.
    /// </para>
    /// </summary>
    internal static void NoteStuckPickup(Player player, EPlayerState stuckState, float heldForSeconds)
    {
        if (stuckState != EPlayerState.Pickup)
        {
            return;
        }
        if (CfgBypassEnabled)
        {
            // Already on the bypass: nothing to switch, the player is simply in an odd state.
            return;
        }
        if (!AutoFallbackEnabled || _autoSwitched)
        {
            return;
        }

        _autoSwitched = true;
        _switchCount++;

                FikaGlobals.LogWarning("[PickupAutoFallback] 因为检测到卡手，已自动切到旁路 " +
            "(a stuck pickup was detected, so ground loot takes now skip the native pickup state; " +
            "no cfg change and no restart were needed)");
            }

    /// <summary>
    /// Read-only snapshot of the native pickup state machine, taken the moment the watchdog gives up
    /// on it (analysis/pickup-auto-fallback). It answers the two questions the next raid has to
    /// settle: did the take ever reach its inventory operation (<c>MovementContext.PickupAction</c>
    /// is the native continuation that runs when the take/its animation finished), and was the
    /// interaction gate closed by the pickup state itself.
    /// </summary>
    internal static void DiagnoseStuckState(MovementContext movementContext, EPlayerState stuckState,
        float heldForSeconds)
    {
        try
        {
            var gate = "unreadable";
            try
            {
                var canInteract = movementContext.CanInteract;
                gate = canInteract == null ? "none" : canInteract.ToString();
            }
            catch (Exception gateException)
            {
                gate = "read failed (" + gateException.Message + ")";
            }

            var pending = "unreadable";
            try
            {
                var action = movementContext.PickupAction;
                pending = action == null || action.Pointer == IntPtr.Zero ? "pending=False" : "pending=True";
            }
            catch (Exception actionException)
            {
                pending = "read failed (" + actionException.Message + ")";
            }

            var animationTime = "n/a";
            var stateAge = "n/a";
            try
            {
                if (movementContext.CurrentState is MovementState currentState
                    && currentState.TryCast<PickUpState>() is { } pickUpState)
                {
                    animationTime = pickUpState._timeForPikupAnimation.ToString("0.00");
                    stateAge = (Time.time - pickUpState._enterTime).ToString("0.0");
                }
            }
            catch
            {
                // The fields only exist on EFT.PickUpState; a state that is not one keeps "n/a".
            }

                    }
        catch (Exception exception)
        {
                    }
    }

    /// <summary>
    /// Driven once per frame by <c>CoopGame.RepairLocalInteractionState</c>: one [LootDiag] heartbeat
    /// every 60 s while a raid runs (so "which path is this machine on, how often did it switch"
    /// needs no other line), and the per-raid reset as soon as the game stopped - the next raid
    /// starts native again.
    /// </summary>
    internal static void TickPickupModeWatchdog(Player player, bool raidRunning)
    {
        try
        {
            if (!raidRunning)
            {
                ResetForNewRaid("the raid ended");
                return;
            }

            NoteLocalPlayer(player);

            // (analysis/perf-diagnostics-cleanup) The reset above and the player note are functional (they
            // clear the bypass of the raid that ended); only the heartbeat below is a probe: one [LootDiag]
            // pickup-mode line every 60 s while a raid runs. With the master switch off it is skipped before
            // the movement state is read, so a raid writes nothing from here.
            

            if (Time.time < _nextModeDiagTime)
            {
                return;
            }
            _nextModeDiagTime = Time.time + 60f;

            var state = "none";
            try
            {
                var currentState = player?.MovementContext?.CurrentState;
                if (currentState != null)
                {
                    state = currentState.Name.ToString();
                }
            }
            catch
            {
                state = "unreadable";
            }

                    }
        catch (Exception exception)
        {
            _nextModeDiagTime = Time.time + 60f;
                    }
    }

    /// <summary>
    /// The switch budget belongs to one local player of one raid: a reconnect or a respawn that
    /// hands the machine a new player object starts from native again.
    /// </summary>
    private static void NoteLocalPlayer(Player player)
    {
        if (player == null)
        {
            return;
        }

        var pointer = player.Pointer;
        if (_modePlayerPointer == pointer)
        {
            return;
        }
        if (_modePlayerPointer != IntPtr.Zero)
        {
            ResetForNewRaid("the local player changed");
        }
        _modePlayerPointer = pointer;
    }

    /// <summary>
    /// A switch only ever lives for one raid: once no raid is running any more the next one starts
    /// native, so a single bad raid cannot leave every later raid on the bypass.
    /// </summary>
    internal static void ResetForNewRaid(string why)
    {
        _modePlayerPointer = IntPtr.Zero;
        if (!_autoSwitched && _switchCount == 0 && _lastReleaseReason == "none")
        {
            return;
        }

        var previous = DescribeSwitch();
        _autoSwitched = false;
        _switchCount = 0;
        _lastReleaseReason = "none";
        _nextModeDiagTime = 0f;
            }

    /// <summary>
    /// Reports the interaction the native pickup action is about to handle, so the next raid
    /// says which path a take took and whether this client even sees the item as a network
    /// object (analysis/pickup-stuck-again). Throttled per item so holding the interaction key
    /// cannot flood the log.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, float> DiagSeen = new();

    internal static void DiagNativeTake(Player player, Item item)
    {
        try
        {
            var key = item.Id.ToString();
            lock (DiagSeen)
            {
                var now = Time.realtimeSinceStartup;
                if (DiagSeen.TryGetValue(key, out var last) && now - last < 1f)
                {
                    return;
                }
                if (DiagSeen.Count > 256)
                {
                    DiagSeen.Clear();
                }
                DiagSeen[key] = now;
            }

            var observed = item.TryCast<EFT.Interactive.ObservedLootItem>() != null;
                    }
        catch (Exception exception)
        {
                    }
    }

    // The native interaction action fires repeatedly while the key is held, and every
    // invocation used to dispatch its own operation. The second one is rejected by the
    // host ("Could not find item owner"), and that rejection triggers the authoritative
    // inventory replacement, which leaves the inventory screen empty for the rest of the
    // raid. Ignore repeats of the same item for a few seconds; the map is bounded and
    // self-expiring so a failed pickup can never block an item permanently.
    private static readonly System.Collections.Generic.Dictionary<string, float> RecentTakes = new();

    private static bool IsRepeatedTake(string itemId)
    {
        lock (RecentTakes)
        {
            var now = Time.realtimeSinceStartup;
            if (RecentTakes.TryGetValue(itemId, out var last) && now - last < 1.5f)
            {
                return true;
            }
            if (RecentTakes.Count > 256)
            {
                RecentTakes.Clear();
            }
            RecentTakes[itemId] = now;
            return false;
        }
    }

    internal static bool TryTake(Player player, Item item)
    {
        var controller = player.InventoryController;
        var localRoot = controller?.RootItem?.TryCast<CompoundItem>();
        if (controller == null || localRoot == null)
        {
            return false;
        }

        var itemKey = item.Id.ToString();
        if (IsRepeatedTake(itemKey))
        {
            FikaGlobals.LogInfo($"[PickupBypass] {itemKey} is already being taken; ignoring the repeated action");
            return true;
        }

        // Armour, helmets and rigs only fit into an equipment slot, and pockets, the
        // tactical rig, the backpack and the secure container are separate compound
        // items, so search every container the player carries instead of only the
        // inventory root. Otherwise a take can fail placement and the item is left
        // on the ground even though the player has room for it.
        var targets = new Il2CppSystem.Collections.Generic.List<CompoundItem>();
        var seen = new System.Collections.Generic.HashSet<IntPtr>();
        void AddTarget(CompoundItem candidate)
        {
            if (candidate != null && seen.Add(candidate.Pointer))
            {
                targets.Add(candidate);
            }
        }

        AddTarget(localRoot);
        if (player.Equipment != null && player.Equipment.TryCast<CompoundItem>() is { } equipment)
        {
            AddTarget(equipment);
        }

        try
        {
            var carried = controller.Inventory?.GetPlayerItems(EPlayerItems.NonQuestItems);
            if (carried != null)
            {
                foreach (var carriedItem in carried)
                {
                    if (carriedItem is CompoundItem compound)
                    {
                        AddTarget(compound);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[PickupBypass] could not enumerate the carried containers: {exception.Message}");
        }

        // Equipment (armour, helmet, rig, backpack, headset) only fits an equipment slot
        // and the generic container search never offers one, so try the dedicated slot
        // move first. Verified native path: Slot.CreateItemAddress + ItemManipulator.Move
        // (dry run) + ConvertOperationResultToOperation + Execute.
        if (GroundLootEquip.TryEquipFromGround(player, item, out var equipError))
        {
            FikaGlobals.LogInfo($"[PickupBypass] equip {item.Id} into its equipment slot " +
                "(native pickup state skipped)");
            return true;
        }

        var placement = ItemManipulator.QuickFindAppropriatePlace(item, controller, targets,
            ItemManipulator.EMoveItemOrder.IgnoreItemParent | ItemManipulator.EMoveItemOrder.TryEquip, true);
        if (placement.Failed)
        {
            // Never fall back to the native pickup action here: that path enters
            // EFT.PickUpState and can lock the player for the rest of the raid.
            // Log the item class and the localised error so the next report says why
            // the placement failed (equipment needs a dedicated slot operation).
            var reason = "unknown";
            try
            {
                reason = placement.Error == null
                    ? "no error object"
                    : placement.Error.GetIl2CppType().Name + " / " + placement.Error.ToString();
            }
            catch (Exception errorException)
            {
                reason = "unreadable (" + errorException.Message + ")";
            }
            FikaGlobals.LogWarning($"[PickupBypass] cannot place {item.Id} template={item.TemplateId.ToString()} " +
                $"class={item.GetIl2CppType().Name}: {reason}; slot attempt: {equipError ?? "not equipment"}; " +
                "keeping it on the ground instead of entering the native pickup state");
            player.ForceInteractionsChanged();
            return true;
        }

        var operation = controller.ConvertOperationResultToOperation(placement.Value);
        if (operation == null)
        {
            FikaGlobals.LogWarning($"[PickupBypass] no operation for {item.Id}");
            return false;
        }

        FikaGlobals.LogInfo($"[PickupBypass] taking {item.Id} through {operation.GetIl2CppType().Name} " +
            "(native pickup state skipped)");
        controller.Execute(operation, (Callback)(result =>
        {
            try
            {
                if (result?.Failed == true)
                {
                    FikaGlobals.LogWarning($"[PickupBypass] pickup of {item.Id} failed: {result.Error}");
                }
                player.ForceInteractionsChanged();
            }
            catch (Exception exception)
            {
                FikaGlobals.LogError($"[PickupBypass] callback failed: {exception}");
            }
        }));
        return true;
    }
}
