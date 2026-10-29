using FikaServer.Models.Enums;
using FikaServer.Models.Fika;
using FikaServer.Models.Fika.Config;
using FikaServer.Models.Fika.Headless;
using FikaServer.Models.Fika.Routes.Headless;
using FikaServer.WebSockets;
using Spectre.Console;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace FikaServer.Services.Headless;

[Injectable(InjectionType.Singleton)]
public class HeadlessService(ISptLogger<HeadlessService> logger,
    HeadlessRequesterWebSocket headlessRequesterWebSocket, JsonUtil jsonUtil,
    FikaServerConfig fikaServerConfig, SaveServer saveServer)
{
    public ConcurrentDictionary<MongoId, HeadlessClientInfo> HeadlessClients { get; } = [];

    /// <summary>
    /// Begin setting up a raid for a headless client
    /// </summary>
    /// <returns>returns the SessionID of the headless client that is starting this raid, returns null if no client could be found or there was an error.</returns>
    public async Task<string?> StartHeadlessRaid(string headlessSessionID, string requesterSessionID, StartHeadlessRequest info)
    {
        if (!HeadlessClients.TryGetValue(headlessSessionID, out var headlessClientInfo))
        {
            logger.LogWithColor($"Could not find HeadlessSessionID '{headlessSessionID}'", Color.Red);
            return null;
        }

        await headlessClientInfo.LifecycleGate.WaitAsync();
        try
        {
            WebSocket webSocket;
            long reservationVersion;
            string reservationToken;
            lock (headlessClientInfo)
            {
                // The available-headless response is only a snapshot. Verify
                // that this object is still the registered connection after
                // acquiring the lifecycle gate, then reserve and send while
                // the gate prevents a reconnect/close from replacing it.
                if (!HeadlessClients.TryGetValue(headlessSessionID, out var current) ||
                    !ReferenceEquals(current, headlessClientInfo) ||
                    headlessClientInfo.State != EHeadlessStatus.READY ||
                    !headlessClientInfo.ClientReady ||
                    headlessClientInfo.IsReconnecting ||
                    headlessClientInfo.RequesterSessionID != null)
                    return null;

                webSocket = headlessClientInfo.WebSocket;
                if (webSocket == null || webSocket.State != WebSocketState.Open)
                    return null;

                headlessRequesterWebSocket.ClearPending(requesterSessionID);
                headlessClientInfo.StartRaid(requesterSessionID);
                reservationVersion = headlessClientInfo.ReservationVersion;
                reservationToken = headlessClientInfo.ReservationToken!;
            }

            try
            {
                // The caller cannot select this token. It belongs to the
                // reservation we just made for this exact connection.
                StartHeadlessRaid startHeadlessRequest = new(info with
                {
                    ReservationToken = reservationToken
                });
                var data = jsonUtil.Serialize(startHeadlessRequest)
                    ?? throw new NullReferenceException("StartHeadlessRaid:: Data was null after serializing");
                logger.Debug($"Starting Headless reservation for {headlessSessionID}: generation={reservationVersion}, token={TokenLabel(reservationToken)}");
                using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await webSocket.SendAsync(Encoding.UTF8.GetBytes(data),
                    WebSocketMessageType.Text, true, sendTimeout.Token);
            }
            catch
            {
                lock (headlessClientInfo)
                {
                    if (headlessClientInfo.ReservationVersion == reservationVersion &&
                        headlessClientInfo.State == EHeadlessStatus.READY &&
                        headlessClientInfo.RequesterSessionID == requesterSessionID)
                    {
                        // A failed SendAsync does not prove the remote peer
                        // missed the message. Quarantine this connection so
                        // a second requester cannot reserve it while the
                        // first peer is still able to issue /raid/create.
                        headlessClientInfo.IsReconnecting = true;
                    }
                }
                try { webSocket.Abort(); }
                catch (Exception exception)
                {
                    logger.Warning($"Failed to abort uncertain Headless start socket: {exception.Message}");
                }
                throw;
            }

            return headlessSessionID;
        }
        finally
        {
            headlessClientInfo.LifecycleGate.Release();
        }
    }

    /// <summary>
    /// Sends a join message to the requester of a headless client
    /// </summary>
    public async Task SendJoinMessageToRequester(MongoId headlessClientId, FikaMatch expectedMatch,
        Func<bool> isCurrentMatch)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo))
        {
            logger.LogWithColor($"Could not find HeadlessSessionID '{headlessClientId}'", Color.Red);
            return;
        }

        await headlessClientInfo.LifecycleGate.WaitAsync();
        try
        {
            // The status request can wait behind reconnect/close. Do not
            // notify a new reservation using the old match's COMPLETE event.
            string requesterSessionId;
            lock (expectedMatch)
            {
                // SetMatchStatus holds matchGate through this send, so an
                // EndMatch cannot remove this room or reset its reservation
                // before notification finishes. Verify the exact owner here.
                if (!HeadlessClients.TryGetValue(headlessClientId, out var currentClient) ||
                    !ReferenceEquals(currentClient, headlessClientInfo) ||
                    !ReferenceEquals(expectedMatch.HeadlessReservationOwner, headlessClientInfo) ||
                    expectedMatch.HeadlessReservationVersion != headlessClientInfo.ReservationVersion ||
                    !isCurrentMatch() || expectedMatch.IsEnding) return;

                lock (headlessClientInfo)
                {
                    // SetMatchStatus(COMPLETE) can be repeated. Only the first transition
                    // may send the requester into the raid.
                    if (headlessClientInfo.State != EHeadlessStatus.READY ||
                        headlessClientInfo.IsReconnecting ||
                        headlessClientInfo.HasNotifiedRequester != false ||
                        string.IsNullOrEmpty(headlessClientInfo.RequesterSessionID)) return;
                    requesterSessionId = headlessClientInfo.RequesterSessionID;
                    headlessClientInfo.State = EHeadlessStatus.IN_RAID;
                    headlessClientInfo.HasNotifiedRequester = true;
                }
            }

            await headlessRequesterWebSocket.SendAsync(requesterSessionId,
                new HeadlessRequesterJoinRaid(headlessClientId));
        }
        finally
        {
            headlessClientInfo.LifecycleGate.Release();
        }
    }

    /// <summary>
    /// Adds a player to the given headless client id
    /// </summary>
    /// <param name="headlessClientId">The id of the headless client</param>
    /// <param name="sessionID">The id of the player</param>
    /// <exception cref="NullReferenceException"></exception>
    public void AddPlayerToHeadlessMatch(MongoId headlessClientId, MongoId sessionID)
    {
        if (HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo))
        {
            if (headlessClientInfo == null)
            {
                throw new NullReferenceException($"AddPlayerToHeadlessMatch:: HeadlessClientInfo was null on {headlessClientId}");
            }

            string? requesterSessionId;
            lock (headlessClientInfo)
            {
                // A reconnect/transit can register the same profile more
                // than once. Keep the lobby roster a set so level averaging
                // and the web UI never count a player twice.
                if (!headlessClientInfo.Players.Contains(sessionID))
                    headlessClientInfo.Players.Add(sessionID);
                requesterSessionId = headlessClientInfo.RequesterSessionID;
            }

            if (!fikaServerConfig.Headless.SetLevelToAverageOfLobby)
            {
                // Doing this everytime is unecessary if we're not setting the average level so only set it once the original requester of the headless joins.
                if (requesterSessionId == sessionID)
                {
                    CalculateAndSetHeadlessLevel(headlessClientId);
                }
            }
            else
            {
                CalculateAndSetHeadlessLevel(headlessClientId);
            }
        }
    }

    /// <summary>
    /// Removes a non-headless player from the current host roster when the
    /// player leaves before the raid ends. The full roster is cleared by
    /// EndHeadlessRaid, but retaining departed players would corrupt lobby
    /// counts and average-level calculation during a long raid or transit.
    /// </summary>
    public void RemovePlayerFromHeadlessMatch(MongoId headlessClientId, MongoId sessionID)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo)) return;
        lock (headlessClientInfo)
        {
            headlessClientInfo.Players.Remove(sessionID);
        }
    }

    /// <summary>
    /// Sets the headless client to a specific level
    /// </summary>
    /// <remarks>Mainly used for transits</remarks>
    /// <param name="headlessClientId">The id of the client</param>
    /// <exception cref="NullReferenceException"></exception>
    public void SetHeadlessLevel(MongoId headlessClientId)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var clientInfo))
        {
            throw new NullReferenceException($"Could not find headlessClientId '{headlessClientId}'");
        }

        var headlessProfile = saveServer.GetProfile(headlessClientId)
                ?? throw new NullReferenceException($"Could not find headlessProfile {headlessClientId}");

        // A transit start can overlap socket replacement/room teardown. Do
        // not apply the old room's level to a fresh reservation that reused
        // the same headless profile id.
        lock (clientInfo)
        {
            if (!HeadlessClients.TryGetValue(headlessClientId, out var current) ||
                !ReferenceEquals(current, clientInfo) ||
                current.State is not EHeadlessStatus.IN_RAID)
                return;

            headlessProfile.CharacterData!.PmcData!.Info!.Level = clientInfo.Level;
        }
    }

    /// <summary>
    /// Calculates the level of the headless client for the raid
    /// </summary>
    /// <param name="headlessClientId">The id of the client</param>
    /// <exception cref="NullReferenceException"></exception>
    public void CalculateAndSetHeadlessLevel(MongoId headlessClientId)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo))
        {
            throw new NullReferenceException($"Could not find headlessClientId '{headlessClientId}'");
        }

        long reservationVersion;
        List<MongoId> players;
        lock (headlessClientInfo)
        {
            if (!HeadlessClients.TryGetValue(headlessClientId, out var current) ||
                !ReferenceEquals(current, headlessClientInfo) ||
                current.State is not EHeadlessStatus.IN_RAID)
                return;

            reservationVersion = headlessClientInfo.ReservationVersion;
            players = [.. headlessClientInfo.Players];
        }

        var headlessProfile = saveServer.GetProfile(headlessClientId)
            ?? throw new NullReferenceException($"Could not find headlessProfile {headlessClientId}");

        var baseHeadlessLevel = 0;
        var countedPlayers = 0; // count only valid, non-headless players

        foreach (var profileId in players)
        {
            var profile = saveServer.GetProfile(profileId);
            if (profile?.IsHeadlessProfile() != false)
            {
                continue;
            }

            baseHeadlessLevel += profile.CharacterData.PmcData.Info.Level ?? 1;
            countedPlayers++;
        }

        // avoid division by zero
        if (countedPlayers > 0)
        {
            baseHeadlessLevel = Math.Max(1, baseHeadlessLevel / countedPlayers);
        }
        else
        {
            baseHeadlessLevel = 1;
        }

        if (logger.IsLogEnabled(LogLevel.Debug))
        {
            logger.Log(LogLevel.Debug,
                $"[{headlessClientId}] Settings headless level to: {baseHeadlessLevel} | Players: {countedPlayers}");
        }

        // Profile lookup and level averaging can take long enough for the
        // room to end and the same profile to be reserved again. Verify the
        // exact client object and reservation generation before publishing.
        lock (headlessClientInfo)
        {
            if (!HeadlessClients.TryGetValue(headlessClientId, out var current) ||
                !ReferenceEquals(current, headlessClientInfo) ||
                current.ReservationVersion != reservationVersion ||
                current.State is not EHeadlessStatus.IN_RAID)
                return;

            headlessProfile.CharacterData.PmcData.Info.Level = baseHeadlessLevel;
            headlessClientInfo.Level = baseHeadlessLevel;
        }
    }

    public long? GetReservationVersion(string headlessClientId)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo)) return null;
        lock (headlessClientInfo) return headlessClientInfo.ReservationVersion;
    }

    /// <summary>
    /// Confirms that an in-flight room creation still belongs to the same
    /// headless connection and requester reservation after location generation.
    /// </summary>
    public bool IsCurrentReservation(string headlessClientId, HeadlessClientInfo owner,
        long reservationVersion, string? reservationToken)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var current) ||
            !ReferenceEquals(current, owner)) return false;

        lock (owner)
        {
            return HeadlessClients.TryGetValue(headlessClientId, out current) &&
                ReferenceEquals(current, owner) &&
                owner.ReservationVersion == reservationVersion &&
                HeadlessReservationToken.Matches(owner.ReservationToken, reservationToken) &&
                !owner.IsReconnecting &&
                !string.IsNullOrEmpty(owner.RequesterSessionID) &&
                owner.WebSocket.State == WebSocketState.Open;
        }
    }

    /// <summary>
    /// Reports why a rejected create failed without logging the full secret token.
    /// Call only after IsCurrentReservation rejects a request.
    /// </summary>
    public string DescribeReservationRejection(string headlessClientId, HeadlessClientInfo owner,
        long reservationVersion, string? reservationToken)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var current))
            return "host connection missing";
        if (!ReferenceEquals(current, owner))
            return "host connection replaced";

        lock (owner)
        {
            if (!HeadlessClients.TryGetValue(headlessClientId, out current) ||
                !ReferenceEquals(current, owner))
                return "host connection replaced";
            if (owner.ReservationVersion != reservationVersion)
                return $"generation changed (expected={reservationVersion}, current={owner.ReservationVersion})";
            if (!HeadlessReservationToken.Matches(owner.ReservationToken, reservationToken))
                return $"token mismatch (expected={TokenLabel(owner.ReservationToken)}, received={TokenLabel(reservationToken)})";
            if (owner.IsReconnecting)
                return "host reconnecting";
            if (string.IsNullOrEmpty(owner.RequesterSessionID))
                return "requester missing";
            if (owner.WebSocket.State != WebSocketState.Open)
                return $"host socket {owner.WebSocket.State}";
            return "reservation changed during validation";
        }
    }

    private static string TokenLabel(string? token) =>
        string.IsNullOrEmpty(token) ? "missing" : token[..Math.Min(token.Length, 6)];

    /// <summary>
    /// Accept readiness only from the currently registered socket and the
    /// just-ended reservation. A report sent while EndMatch is still
    /// finalizing is queued until Reset; it never makes an active room
    /// available to a second requester. The owner gate prevents reconnect
    /// replacement while the report is validated. This path never acquires
    /// MatchService's match gate, avoiding an inverse lock order with CreateMatch.
    /// </summary>
    public async Task MarkClientReadyAsync(string headlessClientId, WebSocket socket,
        string? reservationToken)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var client)) return;

        await client.LifecycleGate.WaitAsync();
        try
        {
            lock (client)
            {
                if (!HeadlessClients.TryGetValue(headlessClientId, out var current) ||
                    !ReferenceEquals(current, client) ||
                    !ReferenceEquals(client.WebSocket, socket) ||
                    client.IsReconnecting || socket.State != WebSocketState.Open)
                    return;

                client.TryMarkReady(reservationToken);
            }
        }
        finally
        {
            client.LifecycleGate.Release();
        }
    }

    /// <summary>
    /// End the raid for the specified headless client. The host remains
    /// unavailable until its matching post-raid readiness report arrives.
    /// </summary>
    public void EndHeadlessRaid(string headlessClientId, HeadlessClientInfo expectedClient,
        long? expectedReservationVersion)
    {
        if (!HeadlessClients.TryGetValue(headlessClientId, out var headlessClientInfo))
        {
            logger.LogWithColor($"EndHeadlessRaid:: Could not find '{headlessClientId}' to remove");
            return;
        }

        lock (headlessClientInfo)
        {
            // A new request can reserve this host while the previous match's
            // webhook is still being sent, or reconnect can replace the entire
            // connection with a fresh version counter. The older end must not
            // cancel either new reservation.
            if (!HeadlessClients.TryGetValue(headlessClientId, out var currentClient) ||
                !ReferenceEquals(currentClient, headlessClientInfo) ||
                !ReferenceEquals(expectedClient, headlessClientInfo) ||
                expectedReservationVersion != headlessClientInfo.ReservationVersion) return;
            headlessRequesterWebSocket.ClearPending(headlessClientInfo.RequesterSessionID);
            headlessClientInfo.Reset();
        }
    }
}
