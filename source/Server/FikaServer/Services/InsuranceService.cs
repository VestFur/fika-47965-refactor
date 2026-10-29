using FikaServer.Models.Fika.Insurance;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;
using System.Collections.Concurrent;

namespace FikaServer.Services;

[Injectable(InjectionType.Singleton)]
public class InsuranceService(SaveServer saveServer, ItemHelper itemHelper, ISptLogger<InsuranceService> logger)
{
    private sealed class MatchInsuranceState
    {
        public Dictionary<MongoId, FikaInsurancePlayer> Players { get; } = [];
        public bool IsEnding { get; set; }
        public int PendingRaidEnds { get; set; }
        public TaskCompletionSource<bool>? RaidEndsCompleted { get; set; }
    }

    private readonly ConcurrentDictionary<MongoId, MatchInsuranceState> _matchInsuranceInfo = [];

    /// <summary>
    /// Gets the match the player is part of
    /// </summary>
    /// <param name="sessionID">The profile id to search for</param>
    /// <returns>The match id or <see langword="null"/> if not found</returns>
    public MongoId? GetMatchId(MongoId sessionID)
        => GetMatchId(sessionID, null);

    /// <summary>
    /// Gets the insurance snapshot that owns a native raid-end callback.
    ///
    /// A profile can be present in an ending match and in a newly joined
    /// match at the same time.  When SPT sends the local raid's ServerId,
    /// matching that token is the only reliable way to avoid assigning the
    /// old callback to the new raid.
    /// </summary>
    /// <param name="sessionID">The profile id to search for.</param>
    /// <param name="raidToken">The ServerId returned by StartLocalRaid.</param>
    /// <returns>The match id or <see langword="null"/> if not found.</returns>
    public MongoId? GetMatchId(MongoId sessionID, string? raidToken)
    {
        var hasToken = !string.IsNullOrWhiteSpace(raidToken);
        MongoId? endingMatch = null;
        foreach ((var matchId, var state) in _matchInsuranceInfo)
        {
            lock (state)
            {
                // EndMatchCore closes admission before native
                // EndLocalRaidAsync necessarily arrives.  Keep returning the
                // retained match while it is settling so the late request can
                // still record the final inventory.
                if (!state.Players.ContainsKey(sessionID))
                    continue;

                if (hasToken)
                {
                    if (!state.Players[sessionID].RaidTokens.Contains(raidToken!))
                        continue;

                    // A token is generated uniquely by StartLocalRaid.  Do
                    // not continue searching once an exact owner is found.
                    return matchId;
                }

                // A profile may already have joined its next raid while an
                // old state is still draining. Prefer the live state, while
                // retaining the ending state as a fallback for a late native
                // EndLocalRaid request from the previous raid.
                if (!state.IsEnding)
                    return matchId;

                endingMatch ??= matchId;
            }
        }

        return endingMatch;
    }

    /// <summary>
    /// Reads the retained raid participant metadata after the live Fika roster
    /// has removed a player. This is needed by the native raid-end request,
    /// which can arrive after /fika/raid/leave.
    /// </summary>
    public bool IsSpectator(MongoId matchId, MongoId sessionID)
    {
        if (!_matchInsuranceInfo.TryGetValue(matchId, out var state)) return false;

        lock (state)
        {
            return state.Players.TryGetValue(sessionID, out var player) && player.IsSpectator;
        }
    }

    /// <summary>
    /// Adds a player to the a match
    /// </summary>
    /// <param name="matchId">The id of the match</param>
    /// <param name="sessionID">The profile id to add</param>
    public bool AddPlayerToMatchId(MongoId matchId, MongoId sessionID, bool isSpectator = false)
    {
        // A reconnect can arrive after the roster entry was removed while
        // the insurance snapshot is intentionally retained for a late
        // EndLocalRaid request. Treat that registration as an idempotent
        // success before reading the profile again; the profile may already
        // be unloading while the raid is being torn down.
        if (_matchInsuranceInfo.TryGetValue(matchId, out var existingState))
        {
            lock (existingState)
            {
                if (existingState.IsEnding) return false;
                if (existingState.Players.ContainsKey(sessionID)) return true;
            }
        }

        var profile = saveServer.GetProfile(sessionID)
            ?? throw new NullReferenceException("[Fika Insurance] Profile was null");

        var pmcData = profile.CharacterData?.PmcData;
        var equipmentId = pmcData!.Inventory?.Equipment!;
        var equipment = pmcData!.Inventory!.Items!
            .Where(i => i.ParentId! == equipmentId || pmcData.DoesItemHaveRootId(i, equipmentId.Value))
            .Select(i => i.Id)
            .ToArray();
        var insuredItems = pmcData.InsuredItems!
            .Where(i => i.ItemId.HasValue && equipment.Contains(i.ItemId.Value))
            .Select(i => i.ItemId!.Value)
            .ToArray();

        logger.Debug($"[Fika Insurance] {sessionID} brought {insuredItems.Length} items into the raid");

        FikaInsurancePlayer player = new()
        {
            SessionID = sessionID,
            IsSpectator = isSpectator,
            EndedRaid = false,
            InsuredItemsBroughtToRaid = insuredItems
        };

        var state = _matchInsuranceInfo.GetOrAdd(matchId, _ => new MatchInsuranceState());
        lock (state)
        {
            // Reconnects can register the same profile again. Preserve the
            // first raid-entry inventory and any post-raid result already
            // recorded for that player.
            if (state.IsEnding) return false;
            // The second check closes the race between the fast path above
            // and profile snapshot construction.
            if (state.Players.ContainsKey(sessionID)) return true;
            state.Players.Add(sessionID, player);
            return true;
        }
    }

    /// <summary>
    /// Associates one StartLocalRaid response with the participant's retained
    /// insurance snapshot. EndLocalRaidRequestData.ServerId is the only raid
    /// identity sent back by the native SPT endpoint, so this association must
    /// be made before the client can finish the raid.
    /// </summary>
    public bool AddRaidToken(MongoId matchId, MongoId sessionID, string? raidToken)
    {
        if (string.IsNullOrWhiteSpace(raidToken) ||
            !_matchInsuranceInfo.TryGetValue(matchId, out var state))
            return false;

        lock (state)
        {
            // EndMatchCore closes admission for new participants before it
            // waits for native raid-end callbacks. A StartLocalRaid request
            // that was already in flight may finish after that gate closes;
            // its existing participant still needs the token registered so
            // the later EndLocalRaid callback can be matched to this snapshot.
            // AddPlayerToMatchId remains the admission gate for new players.
            // If every currently-known raid end was already settled, there is
            // no waiter left that can observe a newly-added token. Reject the
            // late response instead of allowing it to be copied after the
            // final insurance snapshot or attached to a removed state.
            if (state.IsEnding &&
                (state.RaidEndsCompleted is null || state.RaidEndsCompleted.Task.IsCompleted))
            {
                return false;
            }

            if (!state.Players.TryGetValue(sessionID, out var player))
                return false;

            if (player.RaidTokens.Add(raidToken))
            {
                player.RaidTokenOrder[raidToken] = player.NextRaidTokenSequence++;
            }
            return true;
        }
    }

    public void RemovePlayerFromMatchId(MongoId matchId, MongoId sessionID)
    {
        if (!_matchInsuranceInfo.TryGetValue(matchId, out var state)) return;
        lock (state)
        {
            if (!state.IsEnding) state.Players.Remove(sessionID);
        }
    }

    /// <summary>
    /// Executed when a clients requests to end a raid
    /// </summary>
    /// <param name="sessionID">The profile id that requested the end</param>
    /// <param name="matchId">The id of the match</param>
    /// <param name="endLocalRaidRequest">The request data</param>
    public void OnEndLocalRaidRequest(MongoId sessionID, MongoId? matchId, EndLocalRaidRequestData endLocalRaidRequest)
    {
        if (!matchId.HasValue)
        {
            logger.Error($"{sessionID} ended their raid but the match could not be found");
            return;
        }

        if (!_matchInsuranceInfo.TryGetValue(matchId.Value, out var state))
        {
            logger.Error("[Fika Insurance] onEndLocalRaidRequest: matchId not found!");
            return;
        }

        CompleteRaidEnd(state, sessionID, endLocalRaidRequest.ServerId, endLocalRaidRequest);
    }

    public bool BeginRaidEnd(MongoId sessionID, MongoId? matchId)
        => TryBeginRaidEnd(sessionID, matchId, null, out _);

    /// <summary>
    /// Reserves the native end-raid callback for a particular local raid.
    /// Transit can create several local raids under one Fika match, therefore
    /// the attempted/pending state is keyed by the StartLocalRaid ServerId.
    /// </summary>
    public bool TryBeginRaidEnd(MongoId sessionID, MongoId? matchId, string? raidToken,
        out string? reservationToken)
    {
        reservationToken = string.IsNullOrWhiteSpace(raidToken) ? null : raidToken;
        if (!matchId.HasValue || !_matchInsuranceInfo.TryGetValue(matchId.Value, out var state))
            return false;

        lock (state)
        {
            if (!state.Players.TryGetValue(sessionID, out var player) || player.IsSpectator)
                return false;

            if (reservationToken is not null)
            {
                // A token must have been issued by this service's matching
                // StartLocalRaid response. Never fall back to a profile-only
                // reservation when an explicit token is present.
                if (!player.RaidTokens.Contains(reservationToken) ||
                    !player.RaidEndAttemptedTokens.Add(reservationToken))
                    return false;

                player.RaidEndPendingTokens.Add(reservationToken);
            }
            else
            {
                // Compatibility path for native clients that did not send a
                // ServerId. This is deliberately one-shot, as there is no
                // request-side identity to distinguish transit raids.
                if (player.RaidEndAttempted)
                    return false;

                player.RaidEndAttempted = true;
                player.RaidEndPending = true;
            }

            state.PendingRaidEnds++;
            return true;
        }
    }

    public void CompleteRaidEnd(MongoId sessionID, MongoId? matchId, EndLocalRaidRequestData request)
        => CompleteRaidEnd(sessionID, matchId, null, request);

    public void CompleteRaidEnd(MongoId sessionID, MongoId? matchId, string? raidToken,
        EndLocalRaidRequestData request)
    {
        if (!matchId.HasValue || !_matchInsuranceInfo.TryGetValue(matchId.Value, out var state))
            return;
        CompleteRaidEnd(state, sessionID, raidToken, request);
    }

    /// <summary>
    /// Releases a pending raid-end slot when the native SPT request is
    /// cancelled or faults before producing a result.
    /// </summary>
    public void AbortRaidEnd(MongoId sessionID, MongoId? matchId)
        => AbortRaidEnd(sessionID, matchId, null);

    public void AbortRaidEnd(MongoId sessionID, MongoId? matchId, string? raidToken)
    {
        if (!matchId.HasValue || !_matchInsuranceInfo.TryGetValue(matchId.Value, out var state))
            return;

        lock (state)
        {
            // The leave callback may already have removed the player from the
            // insurance roster while the native task is faulting. The pending
            // counter is the authoritative ownership marker for this slot.
            if (state.Players.TryGetValue(sessionID, out var player))
            {
                var pendingRemoved = false;
                if (!string.IsNullOrWhiteSpace(raidToken))
                {
                    if (!player.RaidTokens.Contains(raidToken))
                    {
                        logger.Warning($"[Fika Insurance] Ignoring raid-end callback for unknown raid token {raidToken} ({sessionID})");
                        return;
                    }

                    player.RaidEndAttemptedTokens.Add(raidToken);
                    pendingRemoved = player.RaidEndPendingTokens.Remove(raidToken);
                }
                else
                {
                    player.RaidEndAttempted = true;
                    pendingRemoved = player.RaidEndPending;
                    player.RaidEndPending = false;
                }

                if (pendingRemoved && state.PendingRaidEnds > 0)
                {
                    state.PendingRaidEnds--;
                }
            }
            SignalIfAllRaidEndsAttempted(state);
        }

        logger.Warning($"[Fika Insurance] Raid-end request for {sessionID} was cancelled or faulted; released pending settlement slot");
    }

    private void CompleteRaidEnd(MatchInsuranceState state, MongoId sessionID, string? raidToken,
        EndLocalRaidRequestData request)
    {
        var inventoryAfterRaid = request.Results?.Profile?.Inventory?.Items?
            .Select(i => i.Id)
            .ToArray();
        lock (state)
        {
            if (state.Players.TryGetValue(sessionID, out var player))
            {
                var pendingRemoved = false;
                if (!string.IsNullOrWhiteSpace(raidToken))
                {
                    if (!player.RaidTokens.Contains(raidToken))
                    {
                        logger.Warning($"[Fika Insurance] Ignoring raid-end completion for unknown raid token {raidToken} ({sessionID})");
                        return;
                    }

                    player.RaidEndAttemptedTokens.Add(raidToken);
                    pendingRemoved = player.RaidEndPendingTokens.Remove(raidToken);
                }
                else
                {
                    player.RaidEndAttempted = true;
                    pendingRemoved = player.RaidEndPending;
                    player.RaidEndPending = false;
                }

                if (pendingRemoved && state.PendingRaidEnds > 0)
                {
                    state.PendingRaidEnds--;
                }

                // Keep the inventory and completion flag in the same critical
                // section so match-end cannot observe a half-written result.
                // Transit callbacks can complete out of order; the token
                // sequence prevents an older map from replacing the newest
                // post-raid inventory.
                // A legacy callback without ServerId is useful when no token
                // was registered, but must not overwrite a transit inventory
                // once a tokenized callback has been received.
                var sequence = player.RaidTokens.Count > 0 ? -1 : long.MaxValue;
                if (!string.IsNullOrWhiteSpace(raidToken) &&
                    player.RaidTokenOrder.TryGetValue(raidToken, out var tokenSequence))
                {
                    sequence = tokenSequence;
                }

                if (inventoryAfterRaid != null && sequence >= player.LastCompletedRaidSequence)
                {
                    player.InventoryAfterRaid = inventoryAfterRaid;
                    player.LastCompletedRaidSequence = sequence;
                    player.EndedRaid = true;
                }
            }

            if (inventoryAfterRaid == null)
                logger.Warning($"[Fika Insurance] Ignoring incomplete raid-end inventory for {sessionID}");

            SignalIfAllRaidEndsAttempted(state);
        }

        if (inventoryAfterRaid != null)
            logger.Debug($"[Fika Insurance] {sessionID} brought {inventoryAfterRaid.Length} items out from the raid");
    }

    private static bool PlayerRaidEndsSettled(FikaInsurancePlayer player)
    {
        if (player.IsSpectator)
            return true;

        if (player.RaidTokens.Count > 0)
        {
            return player.RaidEndPendingTokens.Count == 0 &&
                player.RaidEndAttemptedTokens.IsSupersetOf(player.RaidTokens);
        }

        return player.RaidEndPending == false && player.RaidEndAttempted;
    }

    private static bool AllRaidEndsSettled(MatchInsuranceState state) =>
        state.PendingRaidEnds == 0 && state.Players.Values.All(PlayerRaidEndsSettled);

    private static void SignalIfAllRaidEndsAttempted(MatchInsuranceState state)
    {
        if (AllRaidEndsSettled(state))
            state.RaidEndsCompleted?.TrySetResult(true);
    }

    /// <summary>
    /// Executed when a match ends
    /// </summary>
    /// <param name="matchId">The id of the match</param>
    public async Task OnMatchEndAsync(MongoId matchId)
    {
        if (!_matchInsuranceInfo.TryGetValue(matchId, out var state))
        {
            logger.Error($"[Fika Insurance] Could not find match with ID {matchId}");
            return;
        }

        FikaInsurancePlayer[] players;
        Task? pendingRaidEnds = null;
        lock (state)
        {
            if (state.IsEnding) return;
            state.IsEnding = true;
            // EndLocalRaidAsync can arrive after /fika/raid/leave and after
            // the host has started ending the match. Wait for all retained
            // non-spectator participants to reach the native request, whether
            // the request succeeds or faults, rather than only waiting for
            // requests that happened to be in-flight at this exact instant.
            if (!AllRaidEndsSettled(state))
            {
                state.RaidEndsCompleted ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingRaidEnds = state.RaidEndsCompleted.Task;
            }
        }

        if (pendingRaidEnds != null)
        {
            var completed = await Task.WhenAny(pendingRaidEnds, Task.Delay(TimeSpan.FromSeconds(30)));
            if (completed != pendingRaidEnds)
                logger.Warning($"[Fika Insurance] Timed out waiting for raid-end profile saves for {matchId}");
        }

        lock (state)
        {
            players = state.Players.Values
                .Select(player => new FikaInsurancePlayer
                {
                    SessionID = player.SessionID,
                    IsSpectator = player.IsSpectator,
                    RaidTokens = new HashSet<string>(player.RaidTokens, StringComparer.Ordinal),
                    RaidTokenOrder = new Dictionary<string, long>(player.RaidTokenOrder, StringComparer.Ordinal),
                    RaidEndAttemptedTokens = new HashSet<string>(player.RaidEndAttemptedTokens, StringComparer.Ordinal),
                    RaidEndPendingTokens = new HashSet<string>(player.RaidEndPendingTokens, StringComparer.Ordinal),
                    NextRaidTokenSequence = player.NextRaidTokenSequence,
                    LastCompletedRaidSequence = player.LastCompletedRaidSequence,
                    RaidEndAttempted = player.RaidEndAttempted,
                    RaidEndPending = player.RaidEndPending,
                    EndedRaid = player.EndedRaid,
                    InsuredItemsBroughtToRaid = player.InsuredItemsBroughtToRaid,
                    InventoryAfterRaid = player.InventoryAfterRaid
                })
                .ToArray();
        }

        try
        {
            logger.Debug($"[Fika Insurance] Iterating over {players.Length} players");

            var postRaidInventoryMap = new Dictionary<MongoId, MongoId[]>();
            var profilesWithChangedInsurance = new HashSet<MongoId>();
            foreach (var player in players)
            {
                if (!player.EndedRaid || player.InventoryAfterRaid == null)
                    continue;

                if (!postRaidInventoryMap.TryAdd(player.SessionID, player.InventoryAfterRaid))
                    logger.Error($"OnMatchEnd: Duplicate SessionID detected: {player.SessionID}.");
            }

            foreach (var player in players)
            {
                if (!player.EndedRaid || player.InsuredItemsBroughtToRaid == null)
                    continue;

                var otherPlayersPostRaidInventories = new HashSet<MongoId>(postRaidInventoryMap
                    .Where(kvp => kvp.Key != player.SessionID)
                    .SelectMany(kvp => kvp.Value));

                var lootedItemsForThisPlayer = player.InsuredItemsBroughtToRaid
                    .Where(otherPlayersPostRaidInventories.Contains)
                    .ToArray();

                if (lootedItemsForThisPlayer.Length > 0)
                {
                    try
                    {
                        logger.Debug($"{player.SessionID} will lose {lootedItemsForThisPlayer.Length} items in insurance");
                        if (RemoveItemsFromInsurance(player.SessionID, lootedItemsForThisPlayer))
                            profilesWithChangedInsurance.Add(player.SessionID);
                    }
                    catch (Exception exception)
                    {
                        logger.Error($"[Fika Insurance] Failed to settle insurance for {player.SessionID}: {exception.Message}", exception);
                    }
                }
            }

            // SPT saves the raid profile before Fika closes the match. Persist
            // insurance changes made here so they survive a server restart.
            foreach (var sessionID in profilesWithChangedInsurance)
            {
                try
                {
                    await saveServer.SaveProfileAsync(sessionID);
                }
                catch (Exception exception)
                {
                    logger.Error($"[Fika Insurance] Failed to persist insurance changes for {sessionID}: {exception.Message}", exception);
                }
            }
        }
        finally
        {
            _matchInsuranceInfo.TryRemove(new KeyValuePair<MongoId, MatchInsuranceState>(matchId, state));
        }
    }

    /// <summary>
    /// Removes all provided itemIds from a profile's <see cref="Insurance"/>
    /// </summary>
    /// <param name="sessionID">The profile to remove from</param>
    /// <param name="ids">The item ids to search for and remove</param>
    /// <exception cref="NullReferenceException"></exception>
    private bool RemoveItemsFromInsurance(MongoId sessionID, MongoId[] ids)
    {
        var profile = saveServer.GetProfile(sessionID)
            ?? throw new NullReferenceException("[Fika Insurance] Profile was null");

        var changed = false;
        List<Item> toRemove = [];
        for (var i = 0; i < profile.InsuranceList?.Count; i++)
        {
            var insurance = profile.InsuranceList[i];
            if (insurance.Items == null)
            {
                continue;
            }

            foreach (var itemId in ids)
            {
                var item = insurance.Items!
                    .FirstOrDefault(x => x.Id == itemId);

                if (item is null)
                {
                    continue;
                }

                // Remove soft inserts out of armor and helmets
                if (itemHelper.IsOfBaseclasses(item.Template, [BaseClasses.ARMOR, BaseClasses.HEADWEAR]))
                {
                    logger.Debug($"[Fika Insurance] {itemId} is armor or helmet");
                    var children = insurance.Items!
                        .Where(x => x.ParentId! == itemId && itemHelper.IsOfBaseclass(x.Template, BaseClasses.BUILT_IN_INSERTS));

                    foreach (var childItem in children)
                    {
                        // Soft inserts are not insured
                        if (!ids.Contains(childItem.Id))
                        {
                            toRemove.Add(childItem);
                        }
                    }
                }

                // Remove children of the item
                foreach (var itemToRemove in toRemove)
                {
                    if (!insurance.Items!.Remove(itemToRemove))
                    {
                        logger.Debug($"[Fika Insurance] Unable to remove the item {itemToRemove.Id}");
                    }
                    else
                    {
                        changed = true;
                    }
                }

                // Clear list for next iteration
                toRemove.Clear();

                // Remove the original item (parent)
                changed |= insurance.Items!.Remove(item);
            }

            // If all insured items are gone, remove the entry
            if (insurance.Items?.Count == 0)
            {
                profile.InsuranceList.RemoveAt(i--);
                changed = true;
            }
        }

        return changed;
    }
}
