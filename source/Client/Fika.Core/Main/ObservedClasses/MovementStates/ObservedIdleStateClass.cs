using EFT;

namespace Fika.Core.Main.ObservedClasses.MovementStates;

public class ObservedIdleStateClass : IdlePlayerState
{
    public ObservedIdleStateClass(MovementContext movementContext)
        : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedIdleStateClass>(), movementContext) { }

    protected ObservedIdleStateClass(System.IntPtr pointer, MovementContext movementContext) : this(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<IdlePlayerState>(this, new object[] { movementContext });
    }


    public ObservedIdleStateClass(System.IntPtr pointer) : base(pointer) { }


    public override void Move(Vector2 direction)
    {
        // do nothing
    }
}
