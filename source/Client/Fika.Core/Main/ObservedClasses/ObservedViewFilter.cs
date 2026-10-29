using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Components;

namespace Fika.Core.Main.ObservedClasses;

public sealed class ObservedViewFilter : ViewFilter
{

    public ObservedViewFilter(System.IntPtr pointer) : base(pointer) { }

    public ObservedViewFilter() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedViewFilter>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Il2CppSystem.Object>(this, []);
    }


    private static readonly System.Lazy<ObservedViewFilter> DefaultInstance = new(() => new ObservedViewFilter());
    public static ObservedViewFilter Default => DefaultInstance.Value;

    public override HashSet<EBodyModelPart> AllowedParts
    {
        get
        {
            return [EBodyModelPart.Body, EBodyModelPart.Feet, EBodyModelPart.Head];
        }
    }
}
