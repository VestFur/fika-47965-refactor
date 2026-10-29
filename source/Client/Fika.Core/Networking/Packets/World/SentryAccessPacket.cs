namespace Fika.Core.Networking.Packets.World;

/// <summary>
/// BJ (analysis/terminal-rauf-card): one line of the RAUF access decision that the native chain keeps
/// per machine - 「this player has passed the card check of the terminal」.
///
/// Native: the card swipe ends in <c>GlobalEventDispatcher.OnTerminalTransitAccessGained</c>, whose only
/// consumer is <c>SentryBossLogic.OnTerminalAccessGained(Player)</c>, which writes the player id into the
/// sentry's private <c>_accessedPlayerIds</c>. The hostility of the sentry is recomputed on every machine
/// (<c>BotGroupHostility.UpdateHostilityStatusForAllPlayers</c> -> <c>BotHostilityService</c> ->
/// <c>BossSentryHostilityRule</c> -> <c>IAccessBossLogic.HaveAccess</c>), so a machine whose sentry never
/// saw the event keeps attacking. This packet carries the same fact to every machine instead.
/// </summary>
public struct SentryAccessPacket() : INetSerializable
{
    /// <summary>Profile id of the player the check passed for (stable across machines).</summary>
    public string ProfileId;
    /// <summary>Raid id of the player on the sending machine, -1 when unknown.</summary>
    public int RaidId = -1;
    /// <summary>True = the check passed, false = it was lost (left the zone / died).</summary>
    public bool Granted = true;
    /// <summary>The machine the line came from: 0 = host, 1 = client.</summary>
    public byte FromClient;

    /// <summary>
    /// BT (analysis/terminal-hostility-graph): the verdict the AUTHORITY read back from its own native state
    /// (<c>IAccessBossLogic.HaveAccess</c> answered yes for how many sentries, no for how many) and how many
    /// of its sentry groups took the fact through <c>AlliesBySentryAcces</c>. A client sends zeros; the
    /// authority fills them, so the echo carries the answer of the machine that owns the bots.
    /// </summary>
    public int HostAccessYes;
    public int HostAccessNo;
    public int HostAllies;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(ProfileId);
        writer.Put(RaidId);
        writer.Put(Granted);
        writer.Put(FromClient);
        writer.Put(HostAccessYes);
        writer.Put(HostAccessNo);
        writer.Put(HostAllies);
    }

    public void Deserialize(NetDataReader reader)
    {
        ProfileId = reader.GetString();
        RaidId = reader.GetInt();
        Granted = reader.GetBool();
        FromClient = reader.GetByte();
        HostAccessYes = reader.GetInt();
        HostAccessNo = reader.GetInt();
        HostAllies = reader.GetInt();
    }
}
