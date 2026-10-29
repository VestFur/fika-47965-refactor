using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Airdrop;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.SynchronizableObjects;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Networking;

public sealed partial class FikaServer
{
    private static SpawnSyncObjectPacket CaptureAirdrop(AirdropSynchronizableObject obj, Item item = null)
    {
        var container = obj.GetComponentInChildren<LootableContainer>(true);
        item = container?.ItemOwner?.RootItem ?? item;
        if (container == null || item == null) return null;
        if (string.IsNullOrEmpty(container.Id)) container.Id = item.Id;
        Singleton<GameWorld>.Instance.RegisterWorldInteractionObject(container);
        if (container.NetId == 0) return null;
        return new SpawnSyncObjectPacket
        {
            ObjectType = SynchronizableObjectType.AirDrop,
            SubPacket = new SpawnSyncObjectSubPackets.SpawnAirdrop
            {
                ObjectId = obj.ObjectId,
                IsStatic = obj.IsStatic,
                Position = obj.transform.position,
                Rotation = obj.transform.rotation,
                AirdropType = obj.AirdropType,
                AirdropItem = item,
                ContainerId = new MongoID(container.Id),
                NetId = container.NetId
            }
        };
    }

    private async Task SendAirdropWhenReady(EAirdropType type, Item item, int id)
    {
        var world = Singleton<GameWorld>.Instance;
        for (var attempt = 0; attempt < 40 && world != null && Singleton<GameWorld>.Instance?.Pointer == world.Pointer; attempt++)
        {
            var obj = WorldSyncSpawnQueue.Find(world.SynchronizableObjectLogicProcessor, SynchronizableObjectType.AirDrop, id)
                ?.TryCast<AirdropSynchronizableObject>();
            var packet = obj == null ? null : CaptureAirdrop(obj, item);
            if (packet != null)
            {
                SendData(ref packet, DeliveryMethod.ReliableOrdered);
                return;
            }
            await Task.Delay(250);
        }
        if (world != null && Singleton<GameWorld>.Instance?.Pointer == world.Pointer)
            _logger.LogError($"Airdrop {id} ({type}) never acquired a registered loot container");
    }

}
