using EFT.GlobalEvents;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic.SubPackets;

public sealed class BtrSpawn : IPoolSubPacket
{
    public Vector3 Position;
    public Quaternion Rotation;
    public int PlayerRaidId;
    public uint Generation;

    private BtrSpawn() { }

    public static BtrSpawn CreateInstance()
    {
        return new BtrSpawn();
    }

    public static BtrSpawn FromValue(Vector3 position, Quaternion rotation, int playerRaidId, uint generation)
    {
        var packet = GenericSubPacketPoolManager.Instance.GetPacket<BtrSpawn>(EGenericSubPacketType.SpawnBTR);
        packet.Position = position;
        packet.Rotation = rotation;
        packet.PlayerRaidId = playerRaidId;
        packet.Generation = generation;
        return packet;
    }

    public void Execute(FikaPlayer player = null)
    {
        FikaSingleton<FikaClient>.Instance?.QueueBtrSpawn(this);
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.PutUnmanaged(Position);
        writer.PutUnmanaged(Rotation);
        writer.Put(PlayerRaidId);
        writer.Put(Generation);
    }

    public void Deserialize(NetDataReader reader)
    {
        Position = reader.GetUnmanaged<Vector3>();
        Rotation = reader.GetUnmanaged<Quaternion>();
        PlayerRaidId = reader.GetInt();
        Generation = reader.GetUInt();
    }

    public void Dispose()
    {
        Position = default;
        Rotation = default;
        PlayerRaidId = default;
        Generation = 0;
    }
}
