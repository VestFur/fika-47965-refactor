// © 2026 Lacyway All Rights Reserved

using UnityEngine.UI;

namespace Fika.Core.Main.Components;

internal class FikaPing : MonoBehaviour
{
    public FikaPing(System.IntPtr pointer) : base(pointer) { }


    public FikaPing() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaPing>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    Image _image;

    private void Awake()
    {
        _image = GetComponent<Image>();
    }

    private void Update()
    {

    }
}
