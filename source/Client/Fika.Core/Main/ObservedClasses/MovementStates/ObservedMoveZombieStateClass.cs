using EFT;
using EFT.ZombieMovementStates;

namespace Fika.Core.Main.ObservedClasses.MovementStates;

public class ObservedMoveZombieStateClass : MoveZombieState
{
    private readonly MovementContext movementContext;

    public ObservedMoveZombieStateClass(MovementContext movementContext) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedMoveZombieStateClass>())
    {
        this.movementContext = movementContext;
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<MoveZombieState>(this, new object[] { movementContext });
    }


    public ObservedMoveZombieStateClass(System.IntPtr pointer) : base(pointer) { }


    public override void ManualAnimatorMoveUpdate(float deltaTime)
    {
        if (Direction != Vector2.zero)
        {
            Direction = Vector2.zero;
            _timeWithoutInput = 0f;
        }
        _timeWithoutInput += deltaTime;
    }

    public override void UpdateRotationAndPosition(float deltaTime)
    {
        // do nothing
    }
}
