using System;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Patches.World;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Releases the native presentation state a ground loot pickup can leave behind.
/// The pickup runs inside EFT.PickUpState (EPlayerState.Pickup) plus the
/// animator's interaction queue. When the driving inventory operation never
/// reports a terminal status that state stays entered, and the player keeps its
/// restrictions: no view rotation, no movement, no crouch and no interaction
/// prompts, while the inventory and firing keep working. Prone temporarily
/// clears it because it forces a state change, and the queued TakeItem
/// interaction then denies every later pickup for the rest of the raid.
/// Every step is guarded: this only restores presentation state and never
/// touches items, addresses or operations, so loot cannot be duplicated or
/// rolled back by it.
/// </summary>
internal static class NativeInteractionRelease
{
    internal static void Release(Player player, string reason)
    {
        if (player == null)
        {
            return;
        }

        try
        {
            var movementContext = player.MovementContext;
            if (movementContext == null)
            {
                return;
            }

            // Only touch anything when the player really is stuck in a pickup or
            // interaction state. A healthy operation failure must not cancel a
            // legitimate reload, door, examine or throw animation.
            var stuckState = movementContext.CurrentState is MovementState currentState
                && currentState.Name is EPlayerState.Pickup or EPlayerState.Loot or EPlayerState.DoorInteraction
                    or EPlayerState.BreachDoor or EPlayerState.Plant;
            var blockedInteractions = false;
            var blockReason = string.Empty;
            try
            {
                var gate = movementContext.CanInteract;
                blockedInteractions = gate != null;
                if (blockedInteractions)
                {
                    blockReason = gate.ToString();
                }
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: CanInteract read failed: {exception.Message}");
            }
            var animatorStuck = false;
            try
            {
                var animatedInteractions = movementContext.PlayerAnimator?.AnimatedInteractions;
                animatorStuck = animatedInteractions != null
                    && (!animatedInteractions.CanInteract || animatedInteractions.IsScheduleDenied);
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: animator state read failed: {exception.Message}");
            }

            if (!stuckState && !blockedInteractions && !animatorStuck)
            {
                return;
            }

            // [LootDiag] (analysis/pickup-stuck-again): this is the "a stuck pickup was
            // detected and recovered" line. Read together with the pickup-request and the
            // operation result lines it says whether the recovery released a take that was
            // still in flight, which is exactly the reported "it recovered but I still
            // cannot take the item".
            // analysis/pickup-auto-fallback (Z4): it now also names the pickup path this machine
            // is on (native / bypass(cfg) / bypass(auto)) and how often it switched, so one grep
            // answers "did the automatic switch happen, and which take did it release".
            GroundLootPickup.NoteRelease(reason);
                        FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: releasing the interaction state " +
                $"(state={movementContext.CurrentState?.Name}, blocked={blockedInteractions}, animatorStuck={animatorStuck}, " +
                $"gate={(string.IsNullOrEmpty(blockReason) ? "none" : blockReason)})");

            if (movementContext != null)
            {
                Try(reason, "ForceStopInteractions", () =>
                    movementContext.PlayerAnimator?.AnimatedInteractions?.ForceStopInteractions());
                Try(reason, "StopAnyInteractions", () => movementContext.StopAnyInteractions());
                Try(reason, "state cancel", () =>
                {
                    if (movementContext.CurrentState is MovementState state
                        && state.Name is EPlayerState.Pickup or EPlayerState.Loot or EPlayerState.DoorInteraction
                            or EPlayerState.BreachDoor or EPlayerState.Plant)
                    {
                        state.Cancel();

                        // Cancel() only leaves the state once its own callback finishes;
                        // when that callback was lost the player stays locked forever (no
                        // rotation, no movement, no interaction prompts). Force the state
                        // machine back to Idle, exactly like a pose change does.
                        var states = movementContext.States;
                        if (states != null && movementContext.CurrentState?.Name != EPlayerState.Idle
                            && states.TryGetValue(EPlayerState.Idle, out var idleState) && idleState != null)
                        {
                            var stuckName = movementContext.CurrentState?.Name;
                            Try(reason, "state exit", () => state.Exit(false));
                            movementContext.ProcessStateEnter(idleState);
                            FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: forced the movement state " +
                                $"from {stuckName} to Idle");
                        }
                    }
                });
                Try(reason, "SetInteractInHands", () => movementContext.SetInteractInHands(EInteraction.None));
                Try(reason, "PlayerAnimatorSetInteraction", () => movementContext.PlayerAnimatorSetInteraction(false));
                Try(reason, "ToggleBlockInputPlayerRotation", () => movementContext.ToggleBlockInputPlayerRotation(false));
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: movement context release failed: {exception.Message}");
        }

        Try(reason, "interaction refresh", () =>
        {
            player.InteractableObject = null;
            player.ForceInteractionsChanged();
        });

        try
        {
            if (FikaSingleton<IFikaGame>.Instance is CoopGame coopGame)
            {
                coopGame.DiagnosticPlayerOwner?.ClearInteractionState();
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: owner release failed: {exception.Message}");
        }
    }

    /// <summary>
    /// [InvDiag] (2026-10-27, T3) Polls the two interaction-lock signals that nothing else polls.
    /// <para>
    /// <see cref="Release(Player, string)"/> treats three signals as "the player is stuck": a pickup/loot/door/plant
    /// movement state, <c>MovementContext.CanInteract</c> held, and the animator's interaction schedule denied
    /// (<c>AnimatedInteractions.CanInteract</c> false or <c>IsScheduleDenied</c>). Only the first one is ever polled on
    /// a timer (<c>CoopGame.RepairLocalInteractionState</c>, and only for Pickup/Plant), and
    /// <c>CoopGame.RepairLocalInventoryState</c> defines "blocked" as <c>ItemController.Locked</c> /
    /// <c>IsChangingWeapon</c> - neither of the other two. So a ground loot take whose inventory operation was never
    /// answered can leave the animator queue denied while the player is otherwise idle: no interaction prompt appears,
    /// every later take is refused by the native interaction gate, and no watchdog ever called Release. That is the
    /// reported "the client locks up after taking an item".
    /// </para>
    /// <para>
    /// Only runs while the movement state is Idle: every legitimate holder of either signal is a movement state of its
    /// own (door, approach, breach, stationary, pickup, plant), so an idle player with one of them still set has no
    /// owner for it. The budget is the same reasoning the pickup budget uses - a native take finishes its animation
    /// well inside two seconds, so eight seconds of an idle denied schedule is not a running interaction.
    /// </para>
    /// </summary>
    internal static void WatchLostInteractionSchedule(Player player, int pendingOperations, float now)
    {
        if (player == null)
        {
            ResetLostScheduleWatch();
            return;
        }

        if (_lostSchedulePlayer != player.Pointer)
        {
            _lostSchedulePlayer = player.Pointer;
            _lostScheduleSince = 0f;
        }

        var movementContext = player.MovementContext;
        var currentState = movementContext?.CurrentState;
        if (currentState is not MovementState state)
        {
            _lostScheduleSince = 0f;
            return;
        }

        // Every legitimate holder of either signal is a movement state of its own, so an Idle player with one of
        // them still set has no owner for it.
        if (state.Name != EPlayerState.Idle)
        {
            _lostScheduleSince = 0f;
            return;
        }

        // A usable item (meds, food, surgery, a range finder) runs its own animation through the hands controller while
        // the movement state stays Idle and legitimately denies the interaction schedule for many seconds. Release()
        // would cancel it, so those hands are never touched by this watch - the same exclusion the inventory gate
        // watchdog makes. A hands switch in progress is the hands watchdog's business, not this one's.
        try
        {
            var hands = player.HandsController;
            var usableInHands = hands != null
                && (hands.TryCast<Player.UsableItemController>() != null
                    || hands.TryCast<Player.MedsController>() != null);
            if (usableInHands || player.ProcessStatus != Player.EProcessStatus.None)
            {
                _lostScheduleSince = 0f;
                return;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InvDiag] hands state read failed, not watching the interaction schedule: {exception.Message}");
            _lostScheduleSince = 0f;
            return;
        }

        var gateHeld = false;
        try
        {
            gateHeld = movementContext.CanInteract != null;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InvDiag] CanInteract read failed, not watching the interaction schedule: {exception.Message}");
            _lostScheduleSince = 0f;
            return;
        }

        var scheduleDenied = false;
        try
        {
            var animatedInteractions = movementContext.PlayerAnimator?.AnimatedInteractions;
            scheduleDenied = animatedInteractions != null
                && (!animatedInteractions.CanInteract || animatedInteractions.IsScheduleDenied);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InvDiag] animator state read failed, not watching the interaction schedule: {exception.Message}");
            _lostScheduleSince = 0f;
            return;
        }

        if (!gateHeld && !scheduleDenied)
        {
            _lostScheduleSince = 0f;
            return;
        }

        if (_lostScheduleSince == 0f)
        {
            _lostScheduleSince = now;
            return;
        }

        var heldFor = now - _lostScheduleSince;
        if (heldFor < LostScheduleBudgetSeconds)
        {
            return;
        }

        // Keep the timestamp so a release that did not help runs again after the same budget instead of every frame.
        _lostScheduleSince = now;
        FikaGlobals.LogWarning($"[InvDiag] lost-interaction-schedule: idle for {heldFor:0.0}s with " +
            $"gate={(gateHeld ? "held" : "open")}, animatorDenied={scheduleDenied}, " +
            $"pendingOperations={pendingOperations} - releasing the presentation state");
        Release(player, "lost-interaction-schedule");
    }

    /// <summary>[InvDiag] A schedule/budget only ever belongs to one local player of one raid.</summary>
    internal static void ResetLostScheduleWatch()
    {
        _lostSchedulePlayer = IntPtr.Zero;
        _lostScheduleSince = 0f;
    }

    private const float LostScheduleBudgetSeconds = 8f;
    private static IntPtr _lostSchedulePlayer;
    private static float _lostScheduleSince;

    private static void Try(string reason, string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"NativeInteractionRelease[{reason}]: {step} failed: {exception.Message}");
        }
    }
}
