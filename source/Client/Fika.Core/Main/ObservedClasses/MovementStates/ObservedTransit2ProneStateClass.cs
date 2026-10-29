using EFT;

namespace Fika.Core.Main.ObservedClasses.MovementStates;

internal class ObservedTransit2ProneStateClass : ObservedIdleStateClass
{
    public ObservedTransit2ProneStateClass(MovementContext movementContext)
        : base(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedTransit2ProneStateClass>(), movementContext) { }

    public ObservedTransit2ProneStateClass(System.IntPtr pointer) : base(pointer) { }


    public override void ManualAnimatorMoveUpdate(float deltaTime)
    {
        // do nothing
    }

    public override void ProcessAnimatorMovement(float deltaTime)
    {
        // do nothing
    }
}
