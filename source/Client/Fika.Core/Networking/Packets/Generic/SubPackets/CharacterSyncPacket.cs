using System.Collections.Generic;
using Comfort.Common;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic.SubPackets;

public sealed class CharacterSyncPacket : IPoolSubPacket
{
    private CharacterSyncPacket()
    {

    }

    public static CharacterSyncPacket CreateInstance()
    {
        return new();
    }

    public static CharacterSyncPacket FromValue(Dictionary<int, FikaPlayer> players)
    {
        var packet = GenericSubPacketPoolManager.Instance.GetPacket<CharacterSyncPacket>(EGenericSubPacketType.CharacterSync);
        foreach ((var netid, _) in players)
        {
            packet.PlayerIds.Add(netid);
        }
        return packet;
    }

    public List<int> PlayerIds = new(32);

    public void Deserialize(NetDataReader reader)
    {
        var amount = reader.GetUShort();
        if (amount > 0)
        {
            for (var i = 0; i < amount; i++)
            {
                PlayerIds.Add(reader.GetInt());
            }
        }
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((ushort)PlayerIds.Count);
        foreach (var netId in PlayerIds)
        {
            writer.Put(netId);
        }
    }

    /// <summary>
    /// Applies the host's character list on the receiving client.
    ///
    /// The direction of this packet is fixed by the sender, not by a flag on the receiver: only the
    /// host produces it (CoopHandler.SyncPlayersWithClients -> SendGenericPacket(CharacterSync, true)),
    /// the host's own receive path refuses to relay it (FikaServer.IsHostOnlyGenericPacket lists
    /// CharacterSync), so a machine that received it and owns a FikaClient is the addressed client.
    ///
    /// This used to be gated on FikaBackendUtils.IsClient (i.e. ClientType == EClientType.Client) and
    /// logged an error otherwise. ClientType is a *session* flag that NetManagerUtils.AbortFailedJoin
    /// clears when a raid start fails: after the 2026-10-11 18:58 client run gave up on the transit
    /// raid, that flag was already None while the still-connected client kept receiving the host's
    /// periodic sync, and every packet produced the error line
    /// "Received CharacterSyncPacket as server" (20 of them, LogOutput.log:4401-4420) even though the
    /// packet was addressed to that client and was simply ignored. The real question is whether a
    /// live client exists, which is what the check below asks; the flag is reported, not obeyed.
    /// </summary>
    public void Execute(FikaPlayer player = null)
    {
        var client = FikaSingleton<FikaClient>.Instantiated ? FikaSingleton<FikaClient>.Instance : null;
        if (client == null)
        {
            
            return;
        }

        if (!FikaBackendUtils.IsClient)
        {
            
        }

        client.OnCharacterSyncPacketReceived(this);
    }

    public void Dispose()
    {
        PlayerIds.Clear();
    }
}
