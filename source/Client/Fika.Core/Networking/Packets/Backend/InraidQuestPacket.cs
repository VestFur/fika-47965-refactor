using JsonType;
using System.Collections.Generic;
using EFT;
using Fika.Core.Networking.Serialization;
using System;

namespace Fika.Core.Networking.Packets.Backend;

public struct InRaidQuestPacket : INetSerializable
{
    public int NetId;
    public InraidQuestType Type;
    public List<FlatItem[]> Items;
    public List<ItemAddressDescriptor> RewardAddresses;
    public List<MongoID> RewardRootIds;
    public List<MongoID> ItemIdsToRemove;
    public List<int> ItemRemainingCounts;

    public void Deserialize(NetDataReader reader)
    {
        Items = null;
        RewardAddresses = null;
        RewardRootIds = null;
        ItemIdsToRemove = null;
        ItemRemainingCounts = null;
        NetId = reader.GetInt();
        Type = reader.GetEnum<InraidQuestType>();
        switch (Type)
        {
            case InraidQuestType.Finish:
                {
                    var length = reader.GetUShort();
                    Items = new(length);
                    RewardAddresses = new(length);
                    RewardRootIds = new(length);
                    for (var i = 0; i < length; i++)
                    {
                        RewardRootIds.Add(reader.GetMongoID());
                        Items.Add(FikaJson.Deserialize<FlatItem[]>(reader.GetString()));
                        RewardAddresses.Add(reader.GetPolymorph<ItemAddressDescriptor>());
                    }
                }
                break;
            case InraidQuestType.Handover:
                {
                    if (reader.GetBool())
                    {
                        var length = reader.GetUShort();
                        ItemIdsToRemove = new(length);
                        ItemRemainingCounts = new(length);
                        for (var i = 0; i < length; i++)
                        {
                            ItemIdsToRemove.Add(reader.GetMongoID());
                            var remaining = reader.GetInt();
                            if (remaining < 0) throw new InvalidOperationException("Negative quest handover remainder");
                            ItemRemainingCounts.Add(remaining);
                        }
                    }
                }
                break;
            default: throw new InvalidOperationException("Unknown quest inventory packet type");
        }
        Validate();
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        Validate();
        writer.Put(NetId);
        writer.PutEnum(Type);
        switch (Type)
        {
            case InraidQuestType.Finish:
                writer.Put((ushort)Items.Count);
                for (var i = 0; i < Items.Count; i++)
                {
                    writer.PutMongoID(RewardRootIds[i]);
                    writer.Put(FikaJson.Serialize(Items[i]));
                    writer.PutPolymorph(RewardAddresses[i]);
                }
                break;
            case InraidQuestType.Handover:
                writer.Put(ItemIdsToRemove != null);
                if (ItemIdsToRemove != null)
                {
                    writer.Put((ushort)ItemIdsToRemove.Count);
                    for (var i = 0; i < ItemIdsToRemove.Count; i++)
                    {
                        writer.PutMongoID(ItemIdsToRemove[i]);
                        writer.Put(ItemRemainingCounts[i]);
                    }
                }
                break;
        }
    }

    public readonly void Validate()
    {
        if (Type == InraidQuestType.Finish)
        {
            if (Items == null || RewardAddresses == null || RewardRootIds == null || Items.Count > ushort.MaxValue ||
                Items.Count != RewardAddresses.Count || Items.Count != RewardRootIds.Count)
                throw new InvalidOperationException("Invalid quest reward list lengths");
            for (var i = 0; i < Items.Count; i++)
                if (Items[i] == null || Items[i].Length == 0 || RewardAddresses[i] == null || RewardRootIds[i] is null)
                    throw new InvalidOperationException("Incomplete quest reward tree or destination");
        }
        else if (Type == InraidQuestType.Handover)
        {
            if (ItemIdsToRemove == null)
            {
                if (ItemRemainingCounts?.Count > 0) throw new InvalidOperationException("Missing quest handover IDs");
                return;
            }
            if (ItemRemainingCounts == null || ItemIdsToRemove.Count > ushort.MaxValue || ItemIdsToRemove.Count != ItemRemainingCounts.Count)
                throw new InvalidOperationException("Invalid quest handover list lengths");
            for (var i = 0; i < ItemIdsToRemove.Count; i++)
                if (ItemIdsToRemove[i] is null || ItemRemainingCounts[i] < 0)
                    throw new InvalidOperationException("Invalid quest handover item or remainder");
        }
        else throw new InvalidOperationException("Unknown quest inventory packet type");
    }
    public enum InraidQuestType : byte
    {
        Finish,
        Handover
    }
}
