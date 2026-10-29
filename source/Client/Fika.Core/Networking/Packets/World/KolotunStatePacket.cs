namespace Fika.Core.Networking.Packets.World;

public struct KolotunStatePacket : INetSerializable
{
    public bool IsActive;
    public int EventStage;

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(IsActive);
        writer.Put(EventStage);
    }

    public void Deserialize(NetDataReader reader)
    {
        IsActive = reader.GetBool();
        EventStage = reader.GetInt();
    }
}
