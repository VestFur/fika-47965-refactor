using EFT;
using Fika.Core.Main.Utils;
namespace Fika.Core.Main.ClientClasses;

public class FikaClientSmokeGrenade : ObservedSmokeGrenade
{
    public FikaClientSmokeGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<ObservedSmokeGrenade>(this);
    }


    public FikaClientSmokeGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientSmokeGrenade>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private const float _smoothSpeed = 10f;

    public override void ApplyNetPacket(GrenadeSyncPacket packet, float remoteTime)
    {
        ObservedGrenadeSync.Apply(this, packet, _smoothSpeed);
    }
}
