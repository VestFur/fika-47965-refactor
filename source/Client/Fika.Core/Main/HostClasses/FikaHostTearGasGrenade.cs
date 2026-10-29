using EFT;

namespace Fika.Core.Main.HostClasses;

/// <summary>Host-side tear-gas grenade that emits Fika snapshots.</summary>
public sealed class FikaHostTearGasGrenade : TearGasGrenade
{
    public FikaHostTearGasGrenade(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<TearGasGrenade>(this);
    }

    public FikaHostTearGasGrenade() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaHostTearGasGrenade>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public override bool HasNetData => true;
}
