using System;
using EFT;
using EFT.Interactive;

namespace Fika.Core.Main.ObservedClasses.MovementStates;

public class ObservedBreachDoorState : BreachDoorState
{
    private readonly MovementContext movementContext;

    public ObservedBreachDoorState(MovementContext movementContext) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedBreachDoorState>())
    {
        this.movementContext = movementContext;
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<BreachDoorState>(this, new object[] { movementContext });
    }


    public ObservedBreachDoorState(System.IntPtr pointer) : base(pointer) { }


    public override void ManualAnimatorMoveUpdate(float deltaTime)
    {
        if (NormalizedTime > 0.15f)
        {
            ProcessAnimatorMovement(deltaTime);
        }
        if (NormalizedTime < KickTime)
        {
            return;
        }
        if (_hit)
        {
            return;
        }
        _hit = true;
        if (MovementContext.NextBreachResult)
        {
            _door.KickOpen(MovementContext.TransformPosition, false);
        }
        else
        {
            _door.FailBreach(MovementContext.TransformPosition);
        }
        MovementContext.OnBreach();
    }

    public override void ExecuteDoorInteraction(WorldInteractiveObject interactive, InteractionResult interactionResult, Il2CppSystem.Action callback, Player user)
    {
        // Do nothing
    }
}
