using EFT;
using JsonType;
namespace Fika.Core.Networking.Packets.World;

public struct CorpsePositionPacket : INetSerializable
{
    public string ProfileId;
    public CorpseSyncPacket Data;

    public void Deserialize(NetDataReader reader)
    {
        ProfileId = reader.GetString();
        Data = reader.GetRagdollStruct();
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(ProfileId ?? string.Empty);
        writer.PutRagdollStruct(Data);
    }
}
