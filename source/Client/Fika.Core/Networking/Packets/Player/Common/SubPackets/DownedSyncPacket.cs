using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Player.Common.SubPackets;

public sealed class DownedSyncPacket : IPoolSubPacket
{
    private DownedSyncPacket() { }

    public static DownedSyncPacket CreateInstance()
    {
        return new();
    }

    public RevivalState State;

    public static DownedSyncPacket FromValue(RevivalState state)
    {
        var packet = CommonSubPacketPoolManager.Instance.GetPacket<DownedSyncPacket>(ECommonSubPacketType.DownedSync);
        packet.State = state;
        return packet;
    }

    public void Execute(FikaPlayer player = null)
    {
        if (player.TryCast<ObservedPlayer>() is { } observedPlayer)
        {
            if (observedPlayer.HasDied) return;
            observedPlayer.ToggleDowned(State.Downed);
            observedPlayer.RevivalClock.Restore(State);
            return;
        }

        FikaGlobals.LogError($"DownedSyncPacket: player {player?.NetId} was not observed");
    }

    public void Deserialize(NetDataReader reader)
    {
        State.Deserialize(reader);
    }

    public void Serialize(NetDataWriter writer)
    {
        State.Serialize(writer);
    }

    public void Dispose()
    {
        State = default;
    }
}
