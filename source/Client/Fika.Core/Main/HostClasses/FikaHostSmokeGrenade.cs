namespace Fika.Core.Main.HostClasses;

public class FikaHostSmokeGrenade : SmokeGrenade
{
    public FikaHostSmokeGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<SmokeGrenade>(this);
    }


    public FikaHostSmokeGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaHostSmokeGrenade>())
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
