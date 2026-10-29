namespace Fika.Core.Main.HostClasses;

public class FikaHostStunGrenade : StunGrenade
{
    public FikaHostStunGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<StunGrenade>(this);
    }


    public FikaHostStunGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaHostStunGrenade>())
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
