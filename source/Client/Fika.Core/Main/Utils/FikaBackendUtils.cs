using Diz.Binding;
using EFT.Communications;
using JsonType;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using Fika.Core.Main.Components;
using Fika.Core.Main.Patches.VOIP;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using HarmonyLib;
using System.Text.Json.Serialization;

namespace Fika.Core.Main.Utils;

public enum EClientType
{
    None = 0,
    Client = 1,
    Host = 2
}

public static class FikaBackendUtils
{
    /// <summary>
    /// The local player PMC <see cref="EFT.Profile"/>
    /// </summary>
    public static Profile Profile
    {
        get
        {
            _profile ??= FikaGlobals.GetProfile(false);
            return _profile;
        }

        internal set
        {
            _profile = value;
        }
    }
    /// <summary>
    /// The name of the local player PMC
    /// </summary>
    public static string PMCName => Profile.Nickname;
    /// <summary>
    /// If the current raid is a scav raid
    /// </summary>
    public static bool IsScav { get; internal set; }
    public static EClientType ClientType { get; internal set; } = EClientType.None;
    /// <summary>
    /// If this client is a headless client
    /// </summary>
    /// <remarks>
    /// Headless clients are always raid hosts (<see cref="IsServer"/> is always <see langword="true"/>) <br/>
    /// This check should mainly be used to prevent GPU/animation logic, etc.
    /// </remarks>
    public static bool IsHeadless { get; set; }
    /// <summary>
    /// If this client is a player scav walking into a raid that already runs.
    /// Such a player is spawned as a scav and never joins the host's group.
    /// </summary>
    public static bool IsScavLateJoin { get; internal set; }
    /// <summary>
    /// If the raid host is a headless client
    /// </summary>
    public static bool IsHeadlessGame { get; set; }
    /// <summary>
    /// If this client requested the headless session
    /// </summary>
    public static bool IsHeadlessRequester { get; set; }
    /// <summary>
    /// If the current raid is a transit
    /// </summary>
    public static bool IsTransit { get; set; }
    /// <summary>
    /// If the current session is spectator only
    /// </summary>
    public static bool IsSpectator { get; internal set; }
    /// <summary>
    /// If the host is using NAT punching
    /// </summary>
    public static bool IsHostNatPunch { get; internal set; }
    public static IPEndPoint RemoteEndPoint { get; internal set; }
    public static ushort LocalPort { get; internal set; }
    public static string HostLocationId { get; internal set; }
    public static RaidSettings CachedRaidSettings { get; set; }
    public static BindableList<RaidPlayer> GroupPlayers { get; set; } = new();
    public static FikaCustomRaidSettings CustomRaidSettings { get; set; } = new();

    internal static bool RequestFikaWorld;

    internal static MatchMakerAcceptScreen MatchMakerAcceptScreenInstance { get; set; }
    internal static PlayersRaidReadyPanel PlayersRaidReadyPanel { get; set; }
    internal static MatchMakerGroupPreview MatchMakerGroupPreview { get; set; }

    private static Profile _profile;

    /// <summary>
    /// Why the current join / raid start failed, or <see langword="null"/> while it is healthy.
    /// The raid start reads this before it creates a game, so a failed preparation (no network
    /// manager, no host endpoint, a backend error) aborts with a reason instead of continuing
    /// into a half-initialised session.
    /// </summary>
    public static string JoinFailure { get; private set; }

    public static bool JoinFailed
    {
        get
        {
            return JoinFailure != null;
        }
    }

    /// <summary>Records the first failure of the current attempt and writes it to the stable log.</summary>
    public static void MarkJoinFailed(string reason, Exception exception = null)
    {
        if (JoinFailure != null)
        {
            return;
        }

        JoinFailure = reason;
        
        if (exception != null)
        {
            
        }
    }

    /// <summary>Clears the failure state so the next attempt starts clean.</summary>
    public static void ClearJoinFailure()
    {
        if (JoinFailure == null)
        {
            return;
        }

        JoinFailure = null;
        
    }

    /// <summary>
    /// Drops the failure an <em>earlier</em> attempt recorded, at the moment a new attempt starts.
    ///
    /// CreateFikaGameCore reads <see cref="JoinFailed"/> before it creates anything and aborts the raid
    /// with "The raid could not be started: " + <see cref="JoinFailure"/>. MarkJoinFailed keeps the first
    /// reason of an attempt and only CleanUpVariables cleared it, so one failed join made every later
    /// raid start fail instantly - with the previous attempt's reason, on a menu where nothing had gone
    /// wrong yet. A failure of the *current* attempt is still recorded and shown exactly as before:
    /// this is only called when a new attempt begins.
    /// </summary>
    public static void ClearLeftoverJoinFailure()
    {
        var previous = JoinFailure;
        if (previous == null)
        {
            return;
        }

        JoinFailure = null;
        FikaGlobals.LogInfo($"[Join] cleared the leftover join failure from a previous attempt: {previous}");
        
    }

    public static void CleanUpVariables()
    {
        ClearJoinFailure();

        if (!IsTransit)
        {
            IsSpectator = false;
            IsHeadlessRequester = false;
            IsHeadlessGame = false;
            // AP (analysis/nonfika-raid-inertness): the raid kind is a raid-scoped fact and used to survive
            // the raid, so after a single Fika raid every "ClientType == EClientType.None" gate of the next
            // native single-player raid answered "no, this is Fika" - and the port acted on that raid. The
            // 2026-10-12 23:52 crash was played in exactly that state (the raid's own line says side=single).
            // Cleared together with the other raid-scoped fields; a transit keeps it because the reload
            // belongs to the same raid.
            ClientType = EClientType.None;
        }

        MatchMakerAcceptScreenInstance = null;
        PlayersRaidReadyPanel = null;
        MatchMakerGroupPreview = null;

        RequestFikaWorld = false;
        IsScavLateJoin = false;
        GroupPlayers?.Clear();
    }

    /// <summary>
    /// If this client is hosting the raid
    /// </summary>
    /// <remarks>A headless client is always server</remarks>
    public static bool IsServer
    {
        get
        {
            return ClientType == EClientType.Host;
        }
    }
    /// <summary>
    /// If this client joined a raid
    /// </summary>
    public static bool IsClient
    {
        get
        {
            return ClientType == EClientType.Client;
        }
    }
    /// <summary>
    /// If this client is hosting the raid and no clients are connectec
    /// </summary>
    public static bool IsSinglePlayer
    {
        get
        {
            return FikaSingleton<FikaServer>.Instantiated
                && FikaSingleton<FikaServer>.Instance.NetServer.ConnectedPeersCount == 0;
        }
    }
    public static string GroupId { get; internal set; }
    public static string RaidCode { get; internal set; }
    /// <summary>
    /// The <see cref="Guid"/> of the current raid session
    /// </summary>
    public static Guid ServerGuid { get; internal set; }
    public static TransitSettings TransitData
    {
        get
        {
            return _transitData ?? new()
            {
                transitionType = ELocationTransition.None,
                transitionCount = 0,
                transitionRaidId = FikaGlobals.DefaultTransitId,
                visitedLocations = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(0)
            };
        }
        set
        {
            _transitData = value;
        }
    }

    private static TransitSettings _transitData;

    public static void ResetTransitData()
    {
        TransitData = null;
    }

    public static bool JoinMatch(string profileId, string serverId, out CreateMatch result, out string errorMessage)
    {
        result = new CreateMatch();
        errorMessage = "No server matches the data provided or the server no longer exists";

        /*if (MatchMakerAcceptScreenInstance == null)
        {
            FikaGlobals.LogError("Could not find MatchMakerAcceptScreen");
            return false;
        }*/

        MatchJoinRequest body = new(serverId, profileId);
        
        result = FikaRequestHandler.RaidJoin(body);
        

        if (result.GameVersion != FikaPlugin.EFTVersionMajor)
        {
            errorMessage = string.Format(LocaleUtils.UI_ERROR_HOST_EFT_MISMATCH.Localized(), FikaPlugin.EFTVersionMajor, result.GameVersion);
            MarkJoinFailed(errorMessage);
            return false;
        }

        if (result.Crc32 != FikaPlugin.Crc32)
        {
            errorMessage = string.Format(LocaleUtils.UI_ERROR_HOST_FIKA_MISMATCH.Localized(), FikaPlugin.Crc32, result.Crc32);
            MarkJoinFailed(errorMessage);
            return false;
        }

        RaidCode = result.RaidCode;

        return true;
    }

    public static async Task CreateMatch(string profileId, string hostUsername, RaidSettings raidSettings,
        string reservationToken = null)
    {
        if (IsHeadless)
        {
            if (string.IsNullOrWhiteSpace(reservationToken))
                throw new InvalidOperationException("Headless raid creation has no requester reservation token.");
            FikaGlobals.LogInfo($"Creating headless room with reservation token={reservationToken[..Math.Min(6, reservationToken.Length)]}..., length={reservationToken.Length}");
        }
        NotificationManager.DisplayWarningNotification(LocaleUtils.STARTING_RAID.Localized());
        var timestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
        var raidCode = GenerateRaidCode(6);
        var serverGuid = Guid.NewGuid();
        CreateMatch body = new(raidCode, profileId, serverGuid, hostUsername, IsSpectator, timestamp, raidSettings, FikaPlugin.Crc32,
            raidSettings.Side, raidSettings.SelectedDateTime, CustomRaidSettings, reservationToken);

        
        await FikaRequestHandler.RaidCreate(body);
        

        GroupId = profileId;
        ClientType = EClientType.Host;

        RaidCode = raidCode;
        ServerGuid = serverGuid;
    }

    internal static string GenerateRaidCode(int length)
    {
        System.Random random = new();
        var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();
        var raidCode = "";
        for (var i = 0; i < length; i++)
        {
            var charIndex = random.Next(chars.Length);
            raidCode += chars[charIndex];
        }

        return raidCode;
    }

    internal static void AddPartyMembers(Dictionary<Profile, bool> profiles)
    {
        if (IsHeadless)
        {
            return;
        }

        if (Profile == null)
        {
            FikaGlobals.LogError("AddPartyMembers: Own profile was null!");
            return;
        }

        GroupPlayers.Clear();
        foreach ((var profile, var isLeader) in profiles)
        {
            var info = profile.Info;
            GroupPlayer infoSet = new()
            {
                AccountId = profile.AccountId,
                Id = profile.Id,
                IsLeader = isLeader,
                Info = new()
                {
                    Level = info.Level,
                    MemberCategory = info.MemberCategory,
                    SelectedMemberCategory = info.SelectedMemberCategory,
                    Nickname = profile.GetCorrectedNickname(),
                    Side = info.Side,
                    GameVersion = info.GameVersion,
                    HasCoopExtension = info.HasCoopExtension
                }
            };
            RaidPlayer visualProfile = new(infoSet)
            {
                PlayerVisualRepresentation = profile.GetVisualEquipmentState(false)
            };

            GroupPlayers.Add(visualProfile);
        }

        if (TarkovApplication.Exist(out var app))
        {
            var controller = app.Matchmaker;
            if (controller != null)
            {
                var menuUi = Singleton<MenuUI>.Instance;
                if (menuUi != null)
                {
                    var panel = menuUi.MatchmakerTimeHasCome._partyInfoPanel;
                    panel.Close();
                    panel.Show(GroupPlayers, Profile, false);
                    return;
                }
                FikaGlobals.LogWarning("AddPartyMembers: MenuUI was null!");
                return;
            }
            FikaGlobals.LogWarning("AddPartyMembers: MatchmakerPlayersController was null!");
        }
        FikaGlobals.LogWarning("AddPartyMembers: TarkovApplication was null!");
    }
}

public class FikaCustomRaidSettings
{
    [JsonPropertyName("useCustomWeather")]
    public bool UseCustomWeather { get; set; }

    [JsonPropertyName("disableOverload")]
    public bool DisableOverload { get; set; }

    [JsonPropertyName("disableLegStamina")]
    public bool DisableLegStamina { get; set; }

    [JsonPropertyName("disableArmStamina")]
    public bool DisableArmStamina { get; set; }

    public override string ToString()
    {
        return $"UseCustomWeather: {UseCustomWeather}, DisableOverload: {DisableOverload}, DisableLegStamina: {DisableLegStamina}, DisableArmStamina: {DisableArmStamina}";
    }

    public void Reset()
    {
        UseCustomWeather = default;
        DisableOverload = default;
        DisableLegStamina = default;
        DisableArmStamina = default;
    }
}
