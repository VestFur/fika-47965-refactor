using EFT;

namespace Fika.Core.Networking.Packets.Communication;

public struct QuestConditionPacket : INetSerializable
{
    public int NetId;
    public string Nickname;
    public MongoID Id;
    public string SourceId;

    public void Deserialize(NetDataReader reader)
    {
        NetId = reader.GetInt();
        Nickname = reader.GetString();
        Id = reader.GetMongoID();
        SourceId = reader.GetString();
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(NetId);
        writer.Put(Nickname);
        writer.PutMongoID(Id);
        writer.Put(SourceId);
    }
}
