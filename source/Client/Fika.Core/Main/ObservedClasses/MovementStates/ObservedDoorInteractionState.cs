using System;
using EFT;
using EFT.Interactive;

namespace Fika.Core.Main.ObservedClasses.MovementStates;

public class ObservedDoorInteractionState : DoorInteractState
{
    private readonly MovementContext movementContext;

    public ObservedDoorInteractionState(MovementContext movementContext) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedDoorInteractionState>())
    {
        this.movementContext = movementContext;
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<DoorInteractState>(this, new object[] { movementContext });
    }


    public ObservedDoorInteractionState(System.IntPtr pointer) : base(pointer) { }


    public override void Enter(bool isFromSameState)
    {
        MovementContext.RestoreDefaultAlignment(1f);
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call83(this, isFromSameState);
    }

    public override void ExecuteInteraction()
    {
        Door.Interact(MovementContext.InteractionInfo.Result);
    }

    public override void ExecuteDoorInteraction(WorldInteractiveObject door, InteractionResult interactionResult, Il2CppSystem.Action callback, Player user)
    {
        // Do nothing
    }
}
