using System;
using Fika.Core.Networking.LiteNetLib.Utils;

namespace Fika.Core.Networking;

/// <summary>Rejects incompatible wire layouts before any player or world packets are accepted.</summary>
internal static class FikaProtocol
{
    // Increment whenever a packet layout or native descriptor schema changes.
    // v18 adds Kolotun state synchronization for the SPT 5 Christmas cold event.
    // Keep this in the connection key so peers with the previous wire layout are
    // rejected before they can consume a packet using the new fields.
    // v19 lets a player scav walk into a raid that is already running.
    // v20 adds TransitZonePresencePacket, which a client uses to tell the host that
    // its player entered or left a transit point. Fika's packet dispatch is keyed by
    // a CRC16 of the type name and throws on an unknown key, so a peer that does not
    // know the type cannot decode it; the handshake has to reject that peer instead.
    // v21 adds FlareExfilActivationPacket, which a client uses to report a green
    // ("ExitActivate") flare inside a FlareShootDetectorZone so the host can grant the
    // flare exfiltration point to the shooter (and, by default, its squad) instead of
    // leaving the point open to everyone.
    // v22 adds FlareExfilActivationPacket.FlareDirection (the shooter's position) and the
    // scene-trigger resolution for maps without a detector zone (Icebreaker's Heli exit).
    // That packet gained a field, so peers with the v21 layout must be rejected before they
    // decode it.
    // v23 removes the disconnect-reconnect feature (analysis/remove-reconnect): the reconnect
    // connection key is gone and the knock handshake no longer carries its reconnect flag, so
    // peers with a v22 layout must be rejected before they read the shorter handshake.
    internal const int Revision = 23;
    internal const string JoinKey = "fika.spt50.il2cpp.v23.join";
    internal const string ScavJoinKey = "fika.spt50.il2cpp.v23.scavjoin";

    internal static string ConnectionKey(bool scavLateJoin = false) =>
        scavLateJoin ? ScavJoinKey : JoinKey;

    internal static bool TryReadConnection(NetDataReader data, bool raidStarted, out string rejection) =>
        TryReadConnection(data, raidStarted, out rejection, out _);

    internal static bool TryReadConnection(NetDataReader data, bool raidStarted, out string rejection, out bool scavLateJoin)
    {
        rejection = "Incompatible Fika protocol. All peers must use the same SPT 5 IL2CPP build.";
        scavLateJoin = false;
        if (!data.TryGetString(out var key) || data.AvailableBytes != 0) return false;
        if (!string.Equals(key, JoinKey, StringComparison.Ordinal) &&
            !string.Equals(key, ScavJoinKey, StringComparison.Ordinal)) return false;

        scavLateJoin = string.Equals(key, ScavJoinKey, StringComparison.Ordinal);

        // A player scav may enter a raid that is already running; a regular join
        // still has to wait for the host to start it.
        if (raidStarted && string.Equals(key, JoinKey, StringComparison.Ordinal))
        {
            rejection = "Raid already started";
            return false;
        }
        rejection = null;
        return true;
    }
}