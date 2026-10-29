// © 2026 Lacyway All Rights Reserved

using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.ObservedClasses.HandsControllers;

internal sealed class ObservedGrenadeController : Player.GrenadeHandsController
{
    public ObservedGrenadeController(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.GrenadeHandsController>(this);
    }


    public ObservedGrenadeController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedGrenadeController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private ObservedPlayer _observedPlayer;

    public static ObservedGrenadeController Create(ObservedPlayer observedPlayer, ThrowWeap item)
    {
        var controller = CreateController<ObservedGrenadeController>(observedPlayer, item);
        controller._observedPlayer = observedPlayer;
        return controller;
    }

    public override bool CanChangeCompassState(bool newState)
    {
        return false;
    }

    public override bool CanRemove()
    {
        return true;
    }

    public override void OnCanUsePropChanged(bool canUse)
    {
        // Do nothing
    }

    public override void SetCompassState(bool active)
    {
        // Do nothing
    }

    public override void CompassStateHandler(bool isActive)
    {
        /*_observedPlayer.CreateObservedCompass();
        _objectInHandsAnimator.ShowCompass(isActive);
        _observedPlayer.SetPropVisibility(isActive);*/
    }

    /// <summary>
    /// Original method to spawn a grenade, we use <see cref="SpawnGrenade(float, Vector3, Quaternion, Vector3, bool)"/> instead
    /// </summary>
    /// <param name="timeSinceSafetyLevelRemoved"></param>
    /// <param name="position"></param>
    /// <param name="rotation"></param>
    /// <param name="force"></param>
    /// <param name="lowThrow"></param>
    public override void ThrowGrenade(float timeSinceSafetyLevelRemoved, Vector3 position, Quaternion rotation, Vector3 force, bool lowThrow)
    {
        // Do nothing, we use our own method
    }

    /// <summary>
    /// Spawns a grenade, uses data from <see cref="SubPackets.GrenadePacket"/>
    /// </summary>
    /// <param name="timeSinceSafetyLevelRemoved">The time since the safety was removed, use 0f</param>
    /// <param name="position">The <see cref="Vector3"/> position to start from</param>
    /// <param name="rotation">The <see cref="Quaternion"/> rotation of the grenade</param>
    /// <param name="force">The <see cref="Vector3"/> force of the grenade</param>
    /// <param name="lowThrow">If it's a low throw or not</param>
    public void SpawnGrenade(float timeSinceSafetyLevelRemoved, Vector3 position, Quaternion rotation, Vector3 force, bool lowThrow)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call57(this, timeSinceSafetyLevelRemoved, position, rotation, force, lowThrow);
    }
}

/// <summary>
/// Remote-only behavior for the native, sealed tripwire operation. Hooks retain
/// its native type so the game's operation factory and hide/reset paths still work.
/// </summary>
internal sealed class ObservedTripwireState(Player.GrenadeHandsController.PlantTripwireOperation operation)
{
    private Player.GrenadeHandsController Controller => operation.Controller;

    private void Finish()
    {
        // The remote client never owns the inventory mutation or the native
        // tripwire spawn. It still has to finish the native operation so the
        // hands controller can be replaced cleanly after the replicated
        // animation reaches its end.
        operation.State = Player.EOperationState.Finished;
    }

    public void Start()
    {
        Controller.FirearmsAnimator.SetFireMode(Weapon.EFireMode.greanadePlanting, false);
        operation.PlantOperationState = Player.GrenadeHandsController.PlantTripwireOperation.EPlantOperationState.StateIn;
        operation.State = Player.EOperationState.Executing;
        operation.SetLeftStanceAnimOnStartOperation();
    }

    public void OnIdleStartAction()
    {
        operation.PlantOperationState = Player.GrenadeHandsController.PlantTripwireOperation.EPlantOperationState.Idling;
    }

    public void Execute(Callback callback)
    {
        callback.Succeed();
    }

    public void End() => Finish();

    public void DropFinished() => Finish();

    public void FastForward() => Finish();

    public void PlantTripwire()
    {
        operation.PlantOperationState = Player.GrenadeHandsController.PlantTripwireOperation.EPlantOperationState.Planting;
        Controller.FirearmsAnimator.SetGrenadeFire(FirearmsAnimator.EGrenadeFire.Throw);
    }
}
