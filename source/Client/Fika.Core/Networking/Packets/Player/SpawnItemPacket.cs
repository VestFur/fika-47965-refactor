using System;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace Fika.Core.Networking.Packets.Player;

public struct SpawnItemInInventoryPacket : INetSerializable
{
    public int NetId;
    public MongoID ItemId;
    public MongoID TemplateId;
    public int Amount;
    public ItemAddress ItemAddress;

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(NetId);
        writer.PutMongoID(ItemId);
        writer.PutMongoID(TemplateId);
        writer.Put(Amount);
        var descriptor = ItemAddress.ToDescriptor();
        writer.PutPolymorph(descriptor);
    }

    public void Deserialize(NetDataReader reader)
    {
        NetId = reader.GetInt();
        ItemId = reader.GetMongoID();
        TemplateId = reader.GetMongoID();
        Amount = reader.GetInt();
        var descriptor = reader.GetPolymorph<ItemAddressDescriptor>();
        var option = Singleton<GameWorld>.Instance.ToItemAddress(descriptor);
        if (option.Failed)
        {
            throw new Exception($"[SpawnItemPacket] Failed to get ItemAddress: {option.Error}");
        }
        ItemAddress = option.Value;
    }
}
