using EFT;
using EFT.ZombieMovementStates;

namespace Fika.Core.Main.ObservedClasses.MovementStates;

public class ObservedIdleZombieStateClass : IdleZombieState
{
    private readonly MovementContext movementContext;

    public ObservedIdleZombieStateClass(MovementContext movementContext) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedIdleZombieStateClass>())
    {
        this.movementContext = movementContext;
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<IdleZombieState>(this, new object[] { movementContext });
    }


    public ObservedIdleZombieStateClass(System.IntPtr pointer) : base(pointer) { }


    public override void Move(Vector2 direction)
    {
        // do nothing
    }
}
