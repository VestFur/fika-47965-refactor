using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Player.Common.SubPackets;

public sealed class RevivedPlayerPacket : IPoolSubPacket
{
    private RevivedPlayerPacket() { }

    public static RevivedPlayerPacket CreateInstance()
    {
        return new();
    }

    public static RevivedPlayerPacket FromValue()
    {
        return CommonSubPacketPoolManager.Instance.GetPacket<RevivedPlayerPacket>(ECommonSubPacketType.RevivedPlayer);
    }

    public void Execute(FikaPlayer player = null)
    {
        if (player != null)
        {
            player.AcceptRevival();

            return;
        }

        FikaGlobals.LogError("RevivedPlayerPacket received without a player");
    }

    public void Deserialize(NetDataReader reader)
    {

    }

    public void Serialize(NetDataWriter writer)
    {

    }

    public void Dispose()
    {

    }
}
