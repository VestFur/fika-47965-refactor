using Fika.Core.Main.Components;
using Fika.Core.Networking.LiteNetLib;
using System;

namespace Fika.Core.Networking.Packets.Communication;

/// <summary>
/// Per-player activation of a green flare exfiltration point ("signal flare" exits).
/// <para>
/// Native model (verified against the 47888 interop member dump, see
/// analysis/flare-exfil-verification): a green flare corresponds to
/// <c>EFT.InventoryLogic.FlareEventType.ExitActivate</c>. The flare's success raises
/// <c>EFT.GlobalEvents.FlareSuccessEvent</c> locally, which every
/// <c>EFT.Interactive.FlareShootDetectorZone</c> on that machine consumes: it adds
/// <b>the firing player's profile id</b> to <c>_shotPlayerProfiles</c> and fires its
/// output triggers, and the point's per-player requirement
/// (<c>ExfiltrationRequirement.Met(Player, ExfiltrationPoint)</c>, the manual activation
/// one) then answers for that player only. Because the flare success event is never
/// replayed on the host for a client's flare, a client's flare could not activate
/// anything, while the point-level status Fika did replicate made the point usable for
/// the whole team.
/// </para>
/// <para>
/// This packet restores the per-player binding over the wire:
/// <list type="bullet">
/// <item><see cref="EKind.Report"/>: client -&gt; host, "I fired an ExitActivate flare
/// inside detector zone ZoneId, at FlarePosition, and the point there is PointName".</item>
/// <item><see cref="EKind.Grant"/>: host -&gt; all peers, "ProfileId is allowed at
/// PointName". A receiver that is not ProfileId never applies it (and logs the ignored
/// line), so the point is never opened for a player that did not fire.</item>
/// </list>
/// </para>
/// <para>
/// Wire compatibility: this is a new packet type. Fika's LiteNetLib dispatch is keyed by
/// a CRC16 of the packet type name (<c>NetPacketProcessor.GetShortHash</c>) and an unknown
/// key throws during deserialization, so a peer that does not know this type cannot ignore
/// it. The compatibility gate is the handshake: <see cref="FikaProtocol.Revision"/> is
/// raised together with this packet, which rejects an older peer at the connection request
/// instead of mid-raid.
/// </para>
/// </summary>
public sealed class FlareExfilActivationPacket : INetSerializable
{
    /// <summary>Report (client to host) or Grant (host to every peer).</summary>
    public EKind Kind;

    /// <summary>Profile id of the player that fired the flare. Cross-checked against the sending peer on the host.</summary>
    public string ProfileId = string.Empty;

    /// <summary>Name of the exfiltration point (<c>ExfiltrationPoint.Settings.Name</c>) that the flare activates.</summary>
    public string PointName = string.Empty;

    /// <summary><c>FlareShootDetectorZone.zoneID</c> of the detector zone the flare was fired in.</summary>
    public string ZoneId = string.Empty;

    /// <summary>
    /// Group id of the shooter when the squad shares the activation (the vanilla party channel,
    /// <c>EZoneEventType.PlayerByPartyAddedInShotList</c>). Empty when the activation is bound to
    /// the shooter alone: a receiver then applies it only when <see cref="ProfileId"/> is its own.
    /// </summary>
    public string SquadId = string.Empty;

    /// <summary>Native flare bookkeeping uses the numeric raid id (<c>FlareShootZoneEvent.PlayerRaidID</c>).</summary>
    public int RaidId;

    /// <summary>World position the flare was fired from, used by the host to re-derive and validate the point.</summary>
    public Vector3 FlarePosition;

    /// <summary>
    /// Where the flare burned for the shooter, which is the point the exit chain was started at. Set on grants
    /// only; <see cref="Vector3.zero"/> means "no flare position was carried" and the receiver then re-applies
    /// nothing. The whole squad needs it, because the native scene chain of a flare exit
    /// (<c>EFT.GameTriggers.TriggerFlareSuccess</c> -&gt; <c>TriggersEmitter</c> -&gt; the map's handlers) plays its
    /// animation on the machine that raises the flare event, and that machine is the shooter's alone.
    /// </summary>
    public Vector3 FlareDirection;

    public void Serialize(NetDataWriter writer)
    {
        // Write the fields in the exact order they are read in Deserialize.
        writer.Put((byte)Kind);
        writer.Put(ProfileId ?? string.Empty);
        writer.Put(PointName ?? string.Empty);
        writer.Put(ZoneId ?? string.Empty);
        writer.Put(SquadId ?? string.Empty);
        writer.Put(RaidId);
        writer.PutUnmanaged(FlarePosition);
        writer.PutUnmanaged(FlareDirection);
    }

    public void Deserialize(NetDataReader reader)
    {
        Kind = (EKind)reader.GetByte();
        ProfileId = reader.GetString();
        PointName = reader.GetString();
        ZoneId = reader.GetString();
        SquadId = reader.GetString();
        RaidId = reader.GetInt();
        FlarePosition = reader.GetUnmanaged<Vector3>();
        FlareDirection = reader.GetUnmanaged<Vector3>();
    }

    public enum EKind : byte
    {
        /// <summary>Client to host: the local player fired an ExitActivate flare inside a detector zone.</summary>
        Report = 1,

        /// <summary>Host to peers: the named profile is allowed at the named point.</summary>
        Grant = 2
    }

    public static void RegisterOnServer(FikaServer server)
    {
        server.RegisterPacket<FlareExfilActivationPacket, NetPeer>(HostHandler);
    }

    public static void RegisterOnClient(FikaClient client)
    {
        client.RegisterPacket<FlareExfilActivationPacket>(PeerHandler);
    }

    private static void HostHandler(FlareExfilActivationPacket packet, NetPeer peer)
    {
        if (packet == null)
        {
            return;
        }

        FikaFlareExfil.HostHandleReport(packet, peer);
    }

    private static void PeerHandler(FlareExfilActivationPacket packet)
    {
        if (packet == null)
        {
            return;
        }

        FikaFlareExfil.ClientHandlePacket(packet);
    }
}