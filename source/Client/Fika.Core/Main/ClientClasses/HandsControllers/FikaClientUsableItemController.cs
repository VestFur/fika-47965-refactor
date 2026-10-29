using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using static EFT.Player;

namespace Fika.Core.Main.ClientClasses.HandsControllers;

public class FikaClientUsableItemController : UsableItemController
{
    public FikaClientUsableItemController(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<UsableItemController>(this);
    }


    public FikaClientUsableItemController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientUsableItemController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    protected FikaPlayer _fikaPlayer;

    public static FikaClientUsableItemController Create(FikaPlayer player, Item item)
    {
        var controller = CreateController<FikaClientUsableItemController>(player, item);
        controller._fikaPlayer = player;
        return controller;
    }

    public override void CompassStateHandler(bool isActive)
    {
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call8(this, isActive);
        _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.UsableItem;
        _fikaPlayer.CommonPacket.SubPacket = UsableItemPacket.FromValue(true, isActive, false, false, false);
        _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
    }

    public override bool ExamineWeapon()
    {
        var flag = global::Fika.Core.Main.Utils.NativeBaseCalls.Call67(this);
        if (flag)
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.UsableItem;
            _fikaPlayer.CommonPacket.SubPacket = UsableItemPacket.FromValue(false, false, true, false, false);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }
        return flag;
    }

    public override void SetAim(bool value)
    {
        var isAiming = IsAiming;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call68(this, value);
        if (IsAiming != isAiming)
        {
            _fikaPlayer.CommonPacket.Type = ECommonSubPacketType.UsableItem;
            _fikaPlayer.CommonPacket.SubPacket = UsableItemPacket.FromValue(false, false, false, true, isAiming);
            _fikaPlayer.PacketSender.NetworkManager.SendNetReusable(ref _fikaPlayer.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }
    }
}
