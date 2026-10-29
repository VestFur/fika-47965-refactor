using Fika.Core.Main.Players;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Player.Common.SubPackets;

public sealed class FastForwardOutdatedPacket : IPoolSubPacket
{
    private FastForwardOutdatedPacket() { }
    public static FastForwardOutdatedPacket CreateInstance() => new();
    public static FastForwardOutdatedPacket FromValue() =>
        CommonSubPacketPoolManager.Instance.GetPacket<FastForwardOutdatedPacket>(ECommonSubPacketType.FastForwardOutdated);
    public void Execute(FikaPlayer player = null) => player?.HandsController?.FastForwardCurrentState();
    public void Serialize(NetDataWriter writer) { }
    public void Deserialize(NetDataReader reader) { }
    public void Dispose() { }
}
