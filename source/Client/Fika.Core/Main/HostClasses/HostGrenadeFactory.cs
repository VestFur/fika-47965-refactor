using EFT;

namespace Fika.Core.Main.HostClasses;

public class HostGrenadeFactory : GrenadeFactory
{

    public HostGrenadeFactory(System.IntPtr pointer) : base(pointer) { }

    public HostGrenadeFactory() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<HostGrenadeFactory>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<GrenadeFactory>(this, []);
    }


    public override Grenade AddGrenade(GameObject gameObject)
    {
        return gameObject.AddComponent<FikaHostGrenade>();
    }

    public override SmokeGrenade AddSmokeGrenade(GameObject gameObject)
    {
        return gameObject.AddComponent<FikaHostSmokeGrenade>();
    }

    public override StunGrenade AddStunGrenade(GameObject gameObject)
    {
        return gameObject.AddComponent<FikaHostStunGrenade>();
    }

    public override TearGasGrenade AddTearGasGrenade(GameObject gameObject)
    {
        return gameObject.AddComponent<FikaHostTearGasGrenade>();
    }
}
