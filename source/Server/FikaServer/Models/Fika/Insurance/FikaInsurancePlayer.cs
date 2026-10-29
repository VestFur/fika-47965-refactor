using SPTarkov.Server.Core.Models.Common;

namespace FikaServer.Models.Fika.Insurance;

public sealed record FikaInsurancePlayer
{
    public required MongoId SessionID { get; init; }
    public bool IsSpectator { get; init; }
    /// <summary>
    /// ServerId values returned by StartLocalRaid for this participant.
    /// EndLocalRaid is allowed to arrive after the live Fika roster has moved
    /// on to another match, so the value is the only request-side identity
    /// that lets us associate the callback with the correct raid snapshot.
    /// Keep all values for a match because transit raids can produce more than
    /// one local raid before the Fika match is closed.
    /// </summary>
    public HashSet<string> RaidTokens { get; init; } = [];
    /// <summary>
    /// Monotonic order assigned when a local raid token is registered. The
    /// order lets a late callback from an earlier transit raid avoid replacing
    /// the final inventory captured by a newer raid.
    /// </summary>
    public Dictionary<string, long> RaidTokenOrder { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> RaidEndAttemptedTokens { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> RaidEndPendingTokens { get; init; } = new(StringComparer.Ordinal);
    public long NextRaidTokenSequence { get; set; }
    public long LastCompletedRaidSequence { get; set; } = -1;
    /// <summary>
    /// True once the native raid-end request has reached Fika, either through
    /// a successful completion or a cancellation/fault.  Match teardown may
    /// begin before that request arrives; keeping this marker separate from
    /// <see cref="EndedRaid"/> lets the service wait for the late request
    /// without treating a failed save as a successful inventory snapshot.
    /// </summary>
    public bool RaidEndAttempted { get; set; }
    /// <summary>
    /// True while the native EndLocalRaid task owns a settlement slot. This
    /// participant-level marker makes completion and fault callbacks idempotent.
    /// </summary>
    public bool RaidEndPending { get; set; }
    public bool EndedRaid { get; set; }
    public MongoId[]? InsuredItemsBroughtToRaid { get; set; }
    public MongoId[]? InventoryAfterRaid { get; set; }
}
