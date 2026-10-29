// © 2026 Lacyway All Rights Reserved

using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using EFT.NetworkPackets;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.FirearmController.SubPackets;
using Fika.Core.Networking.Packets.World;
using KnifePacket = Fika.Core.Networking.Packets.FirearmController.SubPackets.KnifePacket;

namespace Fika.Core.Main.ClientClasses.HandsControllers;

public class FikaClientKnifeController : Player.KnifeController
{
    public FikaClientKnifeController(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<Player.KnifeController>(this);
    }


    public FikaClientKnifeController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientKnifeController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    protected FikaPlayer _fikaPlayer;
    private WeaponPacket _packet;

    public static FikaClientKnifeController Create(FikaPlayer player, KnifeComponent item)
    {
        var controller = CreateController<FikaClientKnifeController>(player, item);
        controller._fikaPlayer = player;
        controller._packet = new()
        {
            NetId = player.NetId
        };
        return controller;
    }

    public override void Destroy()
    {
        _packet = null;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call61(this);
    }

    public override void CompassStateHandler(bool isActive)
    {
        //SendCompassState(CompassChangePacket.FromValue(isActive));
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call8(this, isActive);
    }

    public void SendCompassState(CompassChangePacket packet)
    {
#if DEBUG
        FikaGlobals.LogInfo("Sending CompassPacket");
#endif
        _packet.Type = EFirearmSubPacketType.CompassChange;
        _packet.SubPacket = packet;
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override void ExamineWeapon()
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call62(this);

        _packet.Type = EFirearmSubPacketType.Knife;
        _packet.SubPacket = KnifePacket.FromValue(true, false, false, false);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
    }

    public override bool MakeKnifeKick()
    {
        var knifeKick = global::Fika.Core.Main.Utils.NativeBaseCalls.Call63(this);

        if (knifeKick)
        {
            _packet.Type = EFirearmSubPacketType.Knife;
            _packet.SubPacket = KnifePacket.FromValue(false, true, false, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }

        return knifeKick;
    }

    public override bool MakeAlternativeKick()
    {
        var alternateKnifeKick = global::Fika.Core.Main.Utils.NativeBaseCalls.Call64(this);

        if (alternateKnifeKick)
        {
            _packet.Type = EFirearmSubPacketType.Knife;
            _packet.SubPacket = KnifePacket.FromValue(false, false, true, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
        }

        return alternateKnifeKick;
    }

    public override void BrakeCombo()
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call65(this);

        _packet.Type = EFirearmSubPacketType.Knife;
        _packet.SubPacket = KnifePacket.FromValue(false, false, false, true);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _packet, DeliveryMethod.ReliableOrdered, true);
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
