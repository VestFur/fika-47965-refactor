// © 2026 Lacyway All Rights Reserved

using EFT;
using Fika.Core.Main.Players;

namespace Fika.Core.Main.ObservedClasses.HandsControllers;

internal sealed class ObservedEmptyHandsController : Player.EmptyHandsController
{
    public ObservedEmptyHandsController(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.EmptyHandsController>(this);
    }


    public ObservedEmptyHandsController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedEmptyHandsController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private ObservedPlayer _observedPlayer;

    public static ObservedEmptyHandsController Create(ObservedPlayer observedPlayer)
    {
        var controller = CreateController<ObservedEmptyHandsController>(observedPlayer);
        controller._observedPlayer = observedPlayer;
        return controller;
    }

    public override bool CanChangeCompassState(bool newState)
    {
        return false;
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
}
