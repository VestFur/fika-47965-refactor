using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Player.Common.SubPackets;

public sealed class RevivingPlayerPacket : IPoolSubPacket
{
    private RevivingPlayerPacket() { }

    public static RevivingPlayerPacket CreateInstance()
    {
        return new();
    }

    public static RevivingPlayerPacket FromValue(bool reviving, string nickname)
    {
        var packet = CommonSubPacketPoolManager.Instance.GetPacket<RevivingPlayerPacket>(ECommonSubPacketType.RevivingPlayer);
        packet._reviving = reviving;
        packet._nickname = nickname;
        return packet;
    }

    private bool _reviving;
    private string _nickname;
    internal bool Reviving => _reviving;
    internal string Nickname => _nickname;

    public void Execute(FikaPlayer player = null)
    {
        if (player != null)
        {
            player.ToggleRevive(_reviving, _nickname);
            return;
        }

        FikaGlobals.LogError("RevivingPlayerPacket received without a player");
    }

    public void Deserialize(NetDataReader reader)
    {
        _reviving = reader.GetBool();
        _nickname = reader.GetString();
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(_reviving);
        writer.Put(_nickname);
    }

    public void Dispose()
    {
        _reviving = default;
        _nickname = null;
    }
}
