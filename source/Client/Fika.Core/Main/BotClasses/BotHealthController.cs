// © 2026 Lacyway All Rights Reserved

using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;

namespace Fika.Core.Main.BotClasses;

public sealed class BotHealthController
    : PlayerHealthController
{
    private readonly Profile.HealthInfo healthInfo;
    private readonly Player player;
    private readonly InventoryController inventoryController;
    private readonly SkillManager skillManager;
    private readonly bool aiHealth;

    public BotHealthController(Profile.HealthInfo healthInfo, Player player, InventoryController inventoryController, SkillManager skillManager, bool aiHealth) : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<BotHealthController>())
    {
        this.healthInfo = healthInfo;
        this.player = player;
        this.inventoryController = inventoryController;
        this.skillManager = skillManager;
        this.aiHealth = aiHealth;
        _fikaBot = player.TryCast<FikaBot>()
            ?? throw new System.InvalidOperationException("Bot health controller received a non-Fika player");
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
        Fika.Core.Main.Utils.NativeConstructor.InvokeBase<PlayerHealthController>(this, healthInfo, player, inventoryController, skillManager, aiHealth);
    }


    public BotHealthController(System.IntPtr pointer) : base(pointer) { }


    private readonly FikaBot _fikaBot ;
    public override bool _sendNetworkSyncPackets
    {
        get
        {
            return true;
        }
    }

    private bool ShouldSend(SyncHealthPacket.ESyncType syncType)
    {
        switch (syncType)
        {
            case SyncHealthPacket.ESyncType.AddEffect:
            case SyncHealthPacket.ESyncType.RemoveEffect:
            case SyncHealthPacket.ESyncType.IsAlive:
            case SyncHealthPacket.ESyncType.BodyHealth:
            case SyncHealthPacket.ESyncType.DestroyedBodyPart:
            case SyncHealthPacket.ESyncType.EffectStrength:
            case SyncHealthPacket.ESyncType.EffectNextState:
            case SyncHealthPacket.ESyncType.EffectMedResource:
            case SyncHealthPacket.ESyncType.EffectStimulatorBuff:
                return true;
            default:
                return false;
        }
    }

    public override void SendNetworkSyncPacket(SyncHealthPacket packet)
    {
        if (packet.SyncType == SyncHealthPacket.ESyncType.IsAlive && !packet.Data.IsAlive.IsAlive)
        {
            _fikaBot.SetupCorpseSyncPacket(packet);
            return;
        }

        if (ShouldSend(packet.SyncType))
        {
            _fikaBot.CommonPacket.Type = ECommonSubPacketType.HealthSync;
            _fikaBot.CommonPacket.SubPacket = HealthSyncPacket.FromValue(packet);
            _fikaBot.PacketSender.NetworkManager.SendNetReusable(ref _fikaBot.CommonPacket, DeliveryMethod.ReliableOrdered, true);
        }
    }
}
