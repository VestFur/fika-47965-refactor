// © 2026 Lacyway All Rights Reserved

using Comfort.Common;
using Fika.Core.Networking;

namespace Fika.Core.Main.PacketHandlers;

public sealed class ObservedPacketSender : MonoBehaviour, IPacketSender
{
    public ObservedPacketSender(System.IntPtr pointer) : base(pointer) { }


    public ObservedPacketSender() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ObservedPacketSender>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public bool SendState { get; set; }
    public IFikaNetworkManager NetworkManager { get; set; }

    private void Awake()
    {
        NetworkManager = FikaSingleton<IFikaNetworkManager>.Instance;
    }

    public void Init()
    {

    }

    public void DestroyThis()
    {
        NetworkManager = null;
        Destroy(this);
    }
}
