using EFT;

namespace Fika.Core.Main.ObservedClasses.MovementStates;

public class ObservedProneMoveStateClass : ObservedRunState
{
    public ObservedProneMoveStateClass(MovementContext movementContext)
        : base(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedProneMoveStateClass>(), movementContext) { }

    public ObservedProneMoveStateClass(System.IntPtr pointer) : base(pointer) { }


    public override void Rotate(Vector2 deltaRotation, bool ignoreClamp = false)
    {
        if (!ignoreClamp)
        {
            deltaRotation = ClampRotation(deltaRotation);
        }
        MovementContext.Rotation += deltaRotation;
    }

    public override void ManualAnimatorMoveUpdate(float deltaTime)
    {
        base.ManualAnimatorMoveUpdate(deltaTime);
        MovementContext.AlignToSurface(deltaTime, new Il2CppSystem.Nullable<Vector3>());
    }
}
