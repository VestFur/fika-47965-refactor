using System;
using System.Collections.Generic;
using EFT.Quests;

namespace Fika.Core.Networking.Packets.World;

/// <summary>Only the sender's live final-mission quest statuses, never inventory/seat claims.</summary>
public struct FinalMissionQuestPacket : INetSerializable
{
    public int NetId;
    public Dictionary<string, EQuestStatus> Statuses;

    public void Deserialize(NetDataReader reader)
    {
        NetId = reader.GetInt();
        var count = reader.GetByte();
        if (count > 64) throw new InvalidOperationException("Too many final mission quests");
        Statuses = new(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var id = reader.GetString(128);
            var status = (EQuestStatus)reader.GetByte();
            if (string.IsNullOrEmpty(id) || status > EQuestStatus.AutoStart || !Statuses.TryAdd(id, status))
                throw new InvalidOperationException("Invalid final mission quest state");
        }
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        if (Statuses == null || Statuses.Count > 64) throw new InvalidOperationException("Invalid final mission quest count");
        writer.Put(NetId);
        writer.Put((byte)Statuses.Count);
        foreach (var quest in Statuses)
        {
            writer.Put(quest.Key);
            writer.Put((byte)quest.Value);
        }
    }
}
