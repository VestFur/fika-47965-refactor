using FikaServer.Models.Enums;
using FikaServer.Models.Fika.Presence;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using System.Collections.Concurrent;

namespace FikaServer.Services;

[Injectable(InjectionType.Singleton)]
public class PresenceService(SaveServer saveServer, TimeUtil timeUtil, ISptLogger<PresenceService> logger)
{
    public List<FikaPlayerPresence> AllPlayersPresence
    {
        get
        {
            return [.. _onlinePlayers.Values.Select(CopyPresence)];
        }
    }

    public Dictionary<MongoId, FikaPlayerPresence> AllPlayersPresenceById =>
        _onlinePlayers.ToDictionary(entry => entry.Key, entry => CopyPresence(entry.Value));

    private readonly ConcurrentDictionary<MongoId, FikaPlayerPresence> _onlinePlayers = [];

    public FikaPlayerPresence? GetPlayerPresence(MongoId profileId)
    {
        if (_onlinePlayers.TryGetValue(profileId, out var playerPresence))
        {
            return CopyPresence(playerPresence);
        }

        return null;
    }

    private static FikaPlayerPresence CopyPresence(FikaPlayerPresence presence) => presence with
    {
        RaidInformation = presence.RaidInformation is { } raid ? raid with { } : null
    };

    public void AddPlayerPresence(MongoId sessionID)
    {
        if (!saveServer.GetProfiles().TryGetValue(sessionID, out var profile) ||
            profile.CharacterData?.PmcData?.Info is null)
        {
            // This used to return silently, which made "the client connected but never showed
            // up in the online list" indistinguishable from "the client never connected".
            logger.Warning($"[Fika Presence] {sessionID} connected but has no PMC profile; no presence entry was created");
            return;
        }

        FikaPlayerPresence data = new()
        {
            Nickname = profile.CharacterData.PmcData.Info.Nickname,
            Level = profile.CharacterData.PmcData.Info.Level ?? 0,
            Activity = EFikaPlayerPresences.IN_MENU,
            ActivityStartedTimestamp = timeUtil.GetTimeStamp()
        };

        logger.Debug($"[Fika Presence] Adding player: {data.Nickname}");

        _onlinePlayers.TryAdd(sessionID, data);
    }

    public void UpdatePlayerPresence(MongoId sessionID, FikaSetPresence NewPresence)
    {
        if (!saveServer.GetProfiles().TryGetValue(sessionID, out var profile) ||
            profile.CharacterData?.PmcData?.Info is null)
        {
            return;
        }

        while (_onlinePlayers.TryGetValue(sessionID, out var currentPresence))
        {
            var nextPresence = new FikaPlayerPresence
            {
                Nickname = profile.CharacterData.PmcData.Info.Nickname,
                Level = profile.CharacterData.PmcData.Info.Level ?? 0,
                Activity = NewPresence.Activity,
                ActivityStartedTimestamp = timeUtil.GetTimeStamp(),
                RaidInformation = NewPresence.RaidInformation is { } raid ? raid with { } : null
            };

            if (_onlinePlayers.TryUpdate(sessionID, nextPresence, currentPresence))
            {
                return;
            }
        }

        // Leaving the loop means this session has no entry. The dictionary used to be filled by
        // the notification websocket alone (NotificationWebSocket.OnConnectionAsync), so a client
        // whose /fika/notification/ socket never connected had every presence update dropped in
        // silence - and /fika/presence/get answered an empty array, which left the main menu
        // roster without a single row, not even the local player's.
        // A presence update is proof that the player is online, so it creates the missing entry.
        // The websocket close handler still removes it when the client leaves.
        FikaPlayerPresence created = new()
        {
            Nickname = profile.CharacterData.PmcData.Info.Nickname,
            Level = profile.CharacterData.PmcData.Info.Level ?? 0,
            Activity = NewPresence.Activity,
            ActivityStartedTimestamp = timeUtil.GetTimeStamp(),
            RaidInformation = NewPresence.RaidInformation is { } raidInfo ? raidInfo with { } : null
        };

        if (_onlinePlayers.TryAdd(sessionID, created))
        {
            logger.Info($"[Fika Presence] Registered {created.Nickname} from a presence update " +
                $"(no notification socket had registered them yet; activity={created.Activity})");
        }
    }

    public void RemovePlayerPresence(MongoId sessionID)
    {
        _onlinePlayers.TryRemove(sessionID, out _);
    }

    public void UpdateMatchStatus(MongoId matchId, EFikaMatchStatus status)
    {
        foreach (var profileId in _onlinePlayers.Keys)
        {
            while (_onlinePlayers.TryGetValue(profileId, out var currentPresence) &&
                currentPresence.RaidInformation is { } raid && raid.MatchId == matchId)
            {
                var nextPresence = currentPresence with
                {
                    RaidInformation = raid with { Started = status != EFikaMatchStatus.COMPLETE }
                };

                if (_onlinePlayers.TryUpdate(profileId, nextPresence, currentPresence))
                {
                    break;
                }
            }
        }
    }
}
