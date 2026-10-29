// © 2026 Lacyway All Rights Reserved

using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using EFT.NetworkPackets;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.ClientClasses.HandsControllers;

public class FikaClientQuickKnifeController : Player.QuickKnifeKickController
{
    public FikaClientQuickKnifeController(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.QuickKnifeKickController>(this);
    }


    public FikaClientQuickKnifeController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientQuickKnifeController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    protected FikaPlayer _fikaPlayer;

    public static FikaClientQuickKnifeController Create(FikaPlayer player, KnifeComponent item)
    {
        var controller = CreateController<FikaClientQuickKnifeController>(player, item);
        controller._fikaPlayer = player;
        return controller;
    }

    public override PlayerHitInfo ProcessHit(Player.KnifeRaycastHit hit, BallisticCollider ballisticCollider)
    {
        if (FikaBackendUtils.IsServer)
        {
            return global::Fika.Core.Main.Utils.NativeBaseCalls.Call66(this, hit, ballisticCollider);
        }

        var shotInfo = global::Fika.Core.Main.Utils.NativeBaseCalls.Call66(this, hit, ballisticCollider);
        if (ballisticCollider == null || ballisticCollider.HitType == EHitType.Default)
        {
            return shotInfo;
        }

        var packet = new KnifeHitPacket
        {
            NetId = _fikaPlayer.NetId,
            HitType = ballisticCollider.HitType,
            HitId = ballisticCollider.NetId,
            HitPoint = hit.point
        };
        FikaSingleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);

        return shotInfo;
    }
}
