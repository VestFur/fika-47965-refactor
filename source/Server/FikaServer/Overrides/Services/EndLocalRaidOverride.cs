using FikaServer.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Services.InRaid;
using System.Reflection;
using InsuranceService = FikaServer.Services.InsuranceService;

namespace FikaServer.Overrides.Services;

[Injectable]
public sealed class EndLocalRaidOverride : AbstractPatch
{
    private sealed class EndRaidState
    {
        public MongoId MatchId { get; init; }
        public bool Track { get; init; }
        public string? RaidToken { get; init; }
    }

    private static MatchService _matchService = default!;
    private static InsuranceService _insuranceService = default!;

    public EndLocalRaidOverride(MatchService matchService, InsuranceService insuranceService)
    {
        _matchService = matchService;
        _insuranceService = insuranceService;
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService)
            .GetMethod(nameof(LocationLifecycleService.EndLocalRaidAsync))!;
    }

    [PatchPrefix]
    public static bool Prefix(MongoId sessionId, EndLocalRaidRequestData request, ref Task __result,
        out object? __state)
    {
        __state = null;

        // The original SPT handler must finish first: it applies insured-item
        // loss to InsuranceList and persists the raid profile. Recording the
        // inventory in a prefix can make Fika settle insurance before SPT has
        // added those lost items.
        // /fika/raid/leave removes the player from the live match roster before
        // SPT receives the native raid-end request.  InsuranceService keeps the
        // entry deliberately so the request can still carry the final
        // inventory snapshot.  Use that retained state when the roster lookup
        // has already gone empty.
        // EndLocalRaid carries the ServerId returned by StartLocalRaid. A
        // profile can already be present in a new match while the previous
        // match is still draining, so a profile-only lookup can attach the
        // late callback to the wrong insurance snapshot. Prefer the exact
        // raid token and only use the legacy profile lookup when the native
        // request did not provide one.
        var raidToken = request?.ServerId;
        var matchId = _insuranceService.GetMatchId(sessionId, raidToken);
        if (!matchId.HasValue && string.IsNullOrWhiteSpace(raidToken))
        {
            matchId = _matchService.GetMatchIdByProfile(sessionId)
                ?? _insuranceService.GetMatchId(sessionId);
        }
        if (matchId == null)
        {
            return true;
        }

        // Find player that exited the raid
        var player = _matchService.GetPlayerInMatch(matchId.Value, sessionId);

        // A player can leave the Fika match before SPT processes the raid-end
        // request. Only a confirmed spectator should bypass profile saving.
        // When the live roster is already gone, the insurance match is the
        // authoritative non-spectator marker and must still reserve a pending
        // settlement slot.
        if (player == null)
        {
            if (_insuranceService.IsSpectator(matchId.Value, sessionId))
            {
                // Preserve the original Fika spectator bypass even when the
                // roster entry was removed before the native callback arrived.
                __result = Task.CompletedTask;
                return false;
            }

            var track = _insuranceService.TryBeginRaidEnd(sessionId, matchId, raidToken, out var reservationToken);
            __state = new EndRaidState
            {
                MatchId = matchId.Value,
                Track = track,
                RaidToken = reservationToken
            };
            return true;
        }

        if (!player.IsSpectator)
        {
            var track = _insuranceService.TryBeginRaidEnd(sessionId, matchId, raidToken, out var reservationToken);
            __state = new EndRaidState
            {
                MatchId = matchId.Value,
                Track = track,
                RaidToken = reservationToken
            };
            return true;
        }

        // Spectators do not have a raid profile to save.
        __result = Task.CompletedTask;

        return false;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, EndLocalRaidRequestData request, Task __result,
        object? __state)
    {
        if (__state is not EndRaidState state || !state.Track) return;
        if (__result == null)
        {
            _insuranceService.AbortRaidEnd(sessionId, state.MatchId, state.RaidToken);
            return;
        }

        _ = __result.ContinueWith(task =>
        {
            if (task.IsCanceled || task.IsFaulted)
            {
                _insuranceService.AbortRaidEnd(sessionId, state.MatchId, state.RaidToken);
                return;
            }

            // A leave callback may have removed the roster entry while the
            // SPT request was running. Use the match captured before the
            // asynchronous request started; lookups by profile can already
            // be empty at this point and would lose the inventory snapshot.
            _insuranceService.CompleteRaidEnd(sessionId, state.MatchId, state.RaidToken, request);
        }, TaskScheduler.Default);
    }
}
