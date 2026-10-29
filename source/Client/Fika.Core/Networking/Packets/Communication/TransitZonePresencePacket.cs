using EFT;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.LiteNetLib;
using System;

namespace Fika.Core.Networking.Packets.Communication;

/// <summary>
/// Sent by a client to the host when its own player enters or leaves a transit
/// point trigger zone, plus one "the interact request itself proves presence"
/// variant that backs up the trigger hook.
/// <para>
/// Why this packet exists: the native <c>TransitController._playersInTransitZone</c>
/// map (and every check that reads it) is only written by
/// <c>ClientTransitController.PlayerEnter/PlayerExit</c>, which only ever runs for
/// the player that is local to that machine. On a host that means the host's own
/// player; a client's player is never registered, so
/// <c>FikaHostTransitController.CheckForPlayers</c> kept answering
/// <c>TransitMessagesEvent.EType.NonAllTeammates</c> and rejected every transit.
/// This packet mirrors the client's local zone state onto the host.
/// </para>
/// <para>
/// Trust: the host never takes a client's word for who it is - the packet is
/// applied only when <c>peer.Player.ProfileId</c> matches the claimed profile
/// (see <see cref="TransitZonePresence.HostHandler"/>) - and it never grants
/// access to a point. Presence only satisfies the "everyone is standing in the
/// zone" gate; access, entry requirements, the completed-quest requirement and
/// the group-size limit are still validated by the host per player.
/// </para>
/// <para>
/// Wire compatibility: this is a new packet type. Fika's LiteNetLib dispatch is
/// keyed by a CRC16 of the packet type name (<c>NetPacketProcessor.GetShortHash</c>)
/// and an unknown key throws a ParseException inside the peer's logic thread
/// (<c>NetPacketProcessor.cs:91</c>), which means a peer that does not know this
/// type cannot "safely ignore" it. The compatibility gate is therefore the
/// handshake: <see cref="FikaProtocol.Revision"/> was raised with this packet, so
/// two peers can only exchange packets when both understand it, and an older peer
/// is rejected at the connection request instead of mid-raid.
/// </para>
/// </summary>
public sealed class TransitZonePresencePacket : INetSerializable
{
    /// <summary>Transit point the player is in (<see cref="EFT.Interactive.TransitPoint.parameters"/>.id).</summary>
    public int PointId;

    /// <summary>Profile of the player the client is reporting. Cross-checked against the sending peer on the host.</summary>
    public string ProfileId;

    /// <summary>What the client observed.</summary>
    public ETransitZonePresenceKind Kind;

    public void Deserialize(NetDataReader reader)
    {
        // Read the fields in the exact order they are written below.
        PointId = reader.GetInt();
        ProfileId = reader.GetString();
        Kind = (ETransitZonePresenceKind)reader.GetByte();
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PointId);
        writer.Put(ProfileId ?? string.Empty);
        writer.Put((byte)Kind);
    }

    public enum ETransitZonePresenceKind : byte
    {
        /// <summary>The player left the zone.</summary>
        Left = 0,
        /// <summary>The player is inside the zone (native PlayerEnter).</summary>
        In = 1,
        /// <summary>The player may be inside a zone; used by the interact request as a fallback for a missed trigger.</summary>
        InUncertain = 2
    }
}

/// <summary>
/// Host-side plumbing for <see cref="TransitZonePresencePacket"/>: the packet and
/// its handlers live together so the shared network files need one registration
/// line each.
/// </summary>
public static class TransitZonePresence
{
    /// <summary>Low-frequency diagnostics: one line per player and point.</summary>
    private static readonly System.Collections.Generic.HashSet<string> LoggedPresence = [];

    /// <summary>Host: registers the packet on the server's dispatch table.</summary>
    public static void RegisterOnServer(FikaServer server)
    {
        server.RegisterPacket<TransitZonePresencePacket, NetPeer>(HostHandler);
    }

    /// <summary>
    /// Client: registers the packet on the dispatch table so a client that is
    /// promoted to host (or a loopback test) can decode it. The client only sends.
    /// </summary>
    public static void RegisterOnClient(FikaClient client)
    {
        client.RegisterPacket<TransitZonePresencePacket>(PeerHandler);
    }

    /// <summary>Client side receive: the host is authoritative about this map.</summary>
    private static void PeerHandler(TransitZonePresencePacket packet)
    {
        
    }

    /// <summary>
    /// Host side receive. The peer's own player decides which profile the packet
    /// may speak for; a mismatch is dropped, so a client cannot register or evict
    /// another player's presence.
    /// </summary>
    private static void HostHandler(TransitZonePresencePacket packet, NetPeer peer)
    {
        try
        {
            var server = FikaSingleton<IFikaNetworkManager>.Instance as FikaServer;
            if (server?.CoopHandler == null)
            {
                // Not in a raid yet; a zone packet has no meaning before the world exists.
                
                return;
            }

            var sender = peer?.Player?.ProfileId;
            if (string.IsNullOrEmpty(sender) || !string.Equals(sender, packet.ProfileId, StringComparison.Ordinal))
            {
                
                return;
            }

            var world = Comfort.Common.Singleton<GameWorld>.Instance;
            var controller = Fika.Core.Main.BaseClasses.FikaWorldControllers.Find<FikaHostTransitController>(world?.TransitController);
            if (controller == null)
            {
                
                return;
            }

            controller.OnRemotePlayerZonePresence(packet.ProfileId, packet.PointId, packet.Kind);
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// Records a player as being inside a point on the host and proves it in the
    /// log exactly once per player and point. Returns true when this call changed
    /// the host's view.
    /// </summary>
    public static bool RecordInZone(System.Collections.Generic.Dictionary<string, int> playersInTransitZone, string profileId,
        int pointId, string source)
    {
        var changed = !playersInTransitZone.TryGetValue(profileId, out var current) || current != pointId;
        playersInTransitZone[profileId] = pointId;
        if (changed && LoggedPresence.Add(profileId + "@" + pointId))
        {
            
        }
        return changed;
    }

    /// <summary>Removes a player from the host's zone map; returns true when an entry was removed.</summary>
    public static bool RemoveFromZone(System.Collections.Generic.Dictionary<string, int> playersInTransitZone, string profileId, string source)
    {
        if (!playersInTransitZone.Remove(profileId)) return false;
        
        return true;
    }

    /// <summary>Drops the whole log dedup table when the map/raid changes.</summary>
    public static void ResetLog()
    {
        LoggedPresence.Clear();
    }
}
