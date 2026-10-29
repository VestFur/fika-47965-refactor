using EFT.GlobalEvents;
using EFT.Interactive.SecretExfiltrations;
using Fika.Core.Main.Players;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic.SubPackets;

public sealed class SecretExfilFound : IPoolSubPacket
{
    public string GroupId;
    public string TeamId;
    public string ExitName;

    private SecretExfilFound() { }

    public static SecretExfilFound CreateInstance()
    {
        return new SecretExfilFound();
    }

    public static SecretExfilFound FromValue(string groupId, string teamId, string exitName)
    {
        var packet = GenericSubPacketPoolManager.Instance.GetPacket<SecretExfilFound>(EGenericSubPacketType.SecretExfilFound);
        packet.GroupId = groupId;
        packet.TeamId = teamId;
        packet.ExitName = exitName;
        return packet;
    }

    // Keep the two-argument helper for source compatibility with extensions
    // compiled against the Mono client. SPT 5.0's native event distinguishes
    // team and group, so the old call falls back to the group value.
    public static SecretExfilFound FromValue(string groupId, string exitName) =>
        FromValue(groupId, groupId, exitName);

    public void Execute(FikaPlayer player = null)
    {
        GlobalEventsController.Instance
            .CreateCommonEvent<SecretExfiltrationPointFoundShareEvent>()
            .Invoke(GroupId, TeamId, ExitName);
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(GroupId);
        writer.Put(TeamId);
        writer.Put(ExitName);
    }

    public void Deserialize(NetDataReader reader)
    {
        GroupId = reader.GetString();
        TeamId = reader.GetString();
        ExitName = reader.GetString();
    }

    public void Dispose()
    {
        GroupId = null;
        TeamId = null;
        ExitName = null;
    }
}
