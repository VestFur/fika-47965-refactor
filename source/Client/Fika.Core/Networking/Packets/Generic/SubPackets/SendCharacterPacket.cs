using Comfort.Common;
using EFT;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic.SubPackets;

public sealed class SendCharacterPacket : IPoolSubPacket
{
    private SendCharacterPacket() { }

    public static SendCharacterPacket CreateInstance()
    {
        return new();
    }

    public static SendCharacterPacket FromValue(PlayerInfoPacket playerInfoPacket, bool isAlive, bool isAi, Vector3 position, int netId, RevivalState? revivalState = null)
    {
        var packet = GenericSubPacketPoolManager.Instance.GetPacket<SendCharacterPacket>(EGenericSubPacketType.SendCharacter);
        packet.PlayerInfoPacket = playerInfoPacket;
        packet.IsAlive = isAlive;
        packet.IsAI = isAi;
        packet.Position = position;
        packet.NetId = netId;
        packet.RevivalState = revivalState ?? new RevivalState { IsAlive = isAlive, BleedoutRemaining = -1f };
        return packet;
    }

    public PlayerInfoPacket PlayerInfoPacket;
    public bool IsAlive;
    public bool IsAI;
    public Vector3 Position;
    public int NetId;
    public RevivalState RevivalState;

    public void Execute(FikaPlayer player = null)
    {
        var handler = FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler;
        if (handler != null)
        {
            handler.QueueProfile(PlayerInfoPacket.Profile, PlayerInfoPacket.HealthByteArray, Position, NetId, IsAlive, IsAI,
                PlayerInfoPacket.ControllerId, PlayerInfoPacket.FirstOperationId, PlayerInfoPacket.IsZombie,
                PlayerInfoPacket.ItemId, PlayerInfoPacket.ControllerType, RevivalState, PlayerInfoPacket.IsStationary);
        }
    }

    public void Deserialize(NetDataReader reader)
    {
        PlayerInfoPacket = reader.GetPlayerInfoPacket();
        IsAlive = reader.GetBool();
        IsAI = reader.GetBool();
        Position = reader.GetUnmanaged<Vector3>();
        NetId = reader.GetInt();
        RevivalState.Deserialize(reader);
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.PutPlayerInfoPacket(PlayerInfoPacket);
        writer.Put(IsAlive);
        writer.Put(IsAI);
        writer.PutUnmanaged(Position);
        writer.Put(NetId);
        RevivalState.Serialize(writer);
    }

    public void Dispose()
    {
        PlayerInfoPacket = default;
        IsAlive = false;
        IsAI = false;
        Position = default;
        NetId = 0;
        RevivalState = default;
    }
}

public struct PlayerInfoPacket
{
    public Profile Profile;
    public MongoID ControllerId;
    public MongoID? ItemId;

    public byte[] HealthByteArray;

    public ushort FirstOperationId;
    public EHandsControllerType ControllerType;

    public bool IsStationary;
    public bool IsZombie;
}
