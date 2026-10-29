using EFT;
using Fika.Core.Main.Utils;
namespace Fika.Core.Main.ClientClasses;

public class FikaClientStunGrenade : ObservedStunGrenade
{
    public FikaClientStunGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<ObservedStunGrenade>(this);
    }


    public FikaClientStunGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientStunGrenade>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private const float _smoothSpeed = 10f;

    public override void ApplyNetPacket(GrenadeSyncPacket packet, float remoteTime)
    {
        ObservedGrenadeSync.Apply(this, packet, _smoothSpeed);
    }
}
