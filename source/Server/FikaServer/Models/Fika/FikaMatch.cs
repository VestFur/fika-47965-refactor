using FikaServer.Models.Enums;
using FikaServer.Models.Fika.Routes.Client;
using FikaServer.Models.Fika.Headless;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using System.Text.Json.Serialization;

namespace FikaServer.Models.Fika;

public record FikaMatch
{
    [JsonPropertyName("ips")]
    public string[] Ips { get; set; } = [];

    [JsonPropertyName("serverGuid")]
    public Guid ServerGuid { get; set; }

    [JsonPropertyName("port")]
    public ushort Port { get; set; }

    [JsonPropertyName("hostUsername")]
    public string HostUsername { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("crc32")]
    public uint CRC32 { get; set; }

    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = string.Empty;

    [JsonPropertyName("raidConfig")]
    public required GetRaidConfigurationRequestData RaidConfig { get; set; }

    [JsonPropertyName("locationData")]
    public required LocationBase LocationData { get; set; }

    [JsonPropertyName("status")]
    public EFikaMatchStatus Status { get; set; }

    [JsonPropertyName("timeout")]
    public int Timeout { get; set; }

    [JsonPropertyName("players")]
    public Dictionary<MongoId, FikaPlayer> Players { get; set; } = [];

    [JsonPropertyName("side")]
    public EFikaSide Side { get; set; }

    [JsonPropertyName("time")]
    public EFikaTime Time { get; set; }

    [JsonPropertyName("raidCode")]
    public string RaidCode { get; set; } = string.Empty;

    [JsonPropertyName("natPunch")]
    public bool NatPunch { get; set; }

    [JsonPropertyName("useFikaNatPunchServer")]
    public bool UseFikaNatPunchServer { get; set; }

    [JsonPropertyName("isHeadless")]
    public bool IsHeadless { get; set; }

    [JsonPropertyName("raids")]
    public int Raids { get; set; }

    [JsonPropertyName("customRaidSettings")]
    public FikaCustomRaidSettings? CustomRaidSettings { get; set; }

    [JsonIgnore]
    public bool IsEnding { get; set; }

    // Retain the reservation that created this match. A reconnect replaces
    // HeadlessClientInfo while the old match may still be settling insurance.
    [JsonIgnore]
    public HeadlessClientInfo? HeadlessReservationOwner { get; set; }

    [JsonIgnore]
    public long? HeadlessReservationVersion { get; set; }

    /// <summary>
    /// Per-match barrier for StartLocalRaid requests admitted before
    /// IsEnding. It is ignored by match JSON and survives until the exact
    /// match object is fully torn down.
    /// </summary>
    [JsonIgnore]
    public FikaServer.Services.LocalRaidStartBarrier LocalRaidStarts { get; } = new();
}
