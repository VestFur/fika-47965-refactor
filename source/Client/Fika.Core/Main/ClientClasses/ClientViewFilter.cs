using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Components;

namespace Fika.Core.Main.ClientClasses;

public sealed class ClientViewFilter : ViewFilter
{

    public ClientViewFilter(System.IntPtr pointer) : base(pointer) { }

    public ClientViewFilter() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ClientViewFilter>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Il2CppSystem.Object>(this, []);
    }


    public override HashSet<EBodyModelPart> AllowedParts
    {
        get
        {
            return [EBodyModelPart.Body, EBodyModelPart.Feet, EBodyModelPart.Head, EBodyModelPart.Hands];
        }
    }
}

