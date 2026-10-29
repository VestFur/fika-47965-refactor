using EFT;

namespace Fika.Core.Main.ObservedClasses;

public sealed class FikaObservedGrenade : Grenade
{
    public FikaObservedGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Grenade>(this);
    }

    public FikaObservedGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaObservedGrenade>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }
}
