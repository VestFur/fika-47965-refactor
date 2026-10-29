using EFT;

namespace Fika.Core.Main.HostClasses;

public class FikaHostGrenade : Grenade
{
    public FikaHostGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Grenade>(this);
    }


    public FikaHostGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaHostGrenade>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public override bool HasNetData
    {
        get
        {
            return true;
        }
    }
}
