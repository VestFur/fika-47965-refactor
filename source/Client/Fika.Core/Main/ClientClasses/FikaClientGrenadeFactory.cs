using EFT;

namespace Fika.Core.Main.ClientClasses;

public class FikaClientGrenadeFactory : GrenadeFactory
{

    public FikaClientGrenadeFactory(System.IntPtr pointer) : base(pointer) { }

    public FikaClientGrenadeFactory() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientGrenadeFactory>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<GrenadeFactory>(this, []);
    }


    public override Grenade AddGrenade(GameObject gameObject)
    {
        return gameObject.AddComponent<FikaClientGrenade>();
    }

    public override SmokeGrenade AddSmokeGrenade(GameObject gameObject)
    {
        return gameObject.AddComponent<FikaClientSmokeGrenade>();
    }

    public override StunGrenade AddStunGrenade(GameObject gameObject)
    {
        return gameObject.AddComponent<FikaClientStunGrenade>();
    }

    public override TearGasGrenade AddTearGasGrenade(GameObject gameObject)
    {
        return gameObject.AddComponent<FikaClientTearGasGrenade>();
    }
}

