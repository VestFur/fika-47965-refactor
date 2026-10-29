using EFT;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.ClientClasses;

/// <summary>
/// IL2CPP observed implementation for the tear-gas grenade.  SPT 5 adds a
/// separate native type, so it must be registered with the same interpolation
/// path as the other observed throwables.
/// </summary>
public sealed class FikaClientTearGasGrenade : ObservedTearGasGrenade
{
    public FikaClientTearGasGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<ObservedTearGasGrenade>(this);
    }

    public FikaClientTearGasGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientTearGasGrenade>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public override void ApplyNetPacket(GrenadeSyncPacket packet, float remoteTime)
    {
        ObservedGrenadeSync.Apply(this, packet, 10f);
    }
}
