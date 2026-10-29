using FikaServer.Helpers;
using FikaServer.Models.Fika.Routes.Location;
using FikaServer.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;

namespace FikaServer.Controllers;

[Injectable]
public class LocationController(MatchService matchService, HeadlessHelper headlessHelper)
{

    /// <summary>
    /// Handle /fika/location/raids
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public List<FikaRaidResponse> HandleGetRaids(GetRaidConfigurationRequestData request)
    {
        List<FikaRaidResponse> matches = [];

        foreach ((var serverId, var match) in matchService.Matches)
        {
            Dictionary<MongoId, bool> players = [];
            string hostUsername;
            bool isHeadless;
            FikaRaidResponse raid;
            lock (match)
            {
                if (match.IsEnding) continue;
                foreach ((var playerId, var player) in match.Players)
                    players[playerId] = player.IsDead;

                hostUsername = match.HostUsername;
                isHeadless = match.IsHeadless;
                raid = new FikaRaidResponse
                {
                    ServerId = serverId,
                    HostUsername = hostUsername,
                    PlayerCount = players.Count,
                    Status = match.Status,
                    Location = match.RaidConfig.Location,
                    Side = match.Side,
                    Time = match.Time,
                    Players = players,
                    IsHeadless = isHeadless,
                    HeadlessRequesterNickname = ""
                };
            }

            if (isHeadless)
            {
                raid.HostUsername = headlessHelper.GetHeadlessNickname(serverId);
            }
            raid.HeadlessRequesterNickname = headlessHelper.GetRequesterUsername(serverId) ?? "";
            matches.Add(raid);

        }

        return matches;
    }


}
