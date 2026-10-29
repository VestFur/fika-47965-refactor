using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;

namespace Fika.Core.Main.ClientClasses.HandsControllers;

public class FikaClientPortableRangeFinderController : PortableRangeFinderController
{
    public FikaClientPortableRangeFinderController(System.IntPtr pointer) : base(pointer)
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.InvokeBaseConstructor<PortableRangeFinderController>(this);
    }


    public FikaClientPortableRangeFinderController() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaClientPortableRangeFinderController>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    protected FikaPlayer _fikaPlayer;

    public static FikaClientPortableRangeFinderController Create(FikaPlayer player, Item item)
    {
        var controller = CreateController<FikaClientPortableRangeFinderController>(player, item);
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
