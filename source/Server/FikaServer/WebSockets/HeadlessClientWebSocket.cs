using FikaServer.Helpers;
using FikaServer.Models.Enums;
using FikaServer.Models.Fika.Headless;
using FikaServer.Models.Fika.WebSocket.Notifications;
using FikaServer.Services;
using FikaServer.Services.Headless;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Servers.Ws;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FikaServer.WebSockets;

[Injectable(InjectionType.Singleton)]
public class HeadlessClientWebSocket(HeadlessHelper headlessHelper, HeadlessService headlessService,
    MatchService matchService, ISptLogger<HeadlessClientWebSocket> logger, WebhookService webhookService,
    NotificationWebSocket notificationWebSocket) : IWebSocketConnectionHandler
{
    private readonly ConcurrentDictionary<string, WebSocket> _headlessWebSockets = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionGates = [];

    public string GetHookUrl()
    {
        return "/fika/headless/client";
    }

    public string GetSocketId()
    {
        return "Fika Headless Client";
    }

    public async Task OnConnectionAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        if (!WebSocketAuthHelper.TryGetSessionId(context, out var userSessionID))
        {
            await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid authorization", CancellationToken.None);
            return;
        }

        logger.Debug($"[{GetSocketId()}] User is {userSessionID}");

        if (!headlessHelper.IsHeadlessClient(userSessionID))
        {
            logger.Error($"[{GetSocketId()}] Invalid headless client {userSessionID} tried to authenticate!");
            return;
        }

        WebSocket? oldSocket = null;
        var isNewClient = false;
        var connectionGate = _connectionGates.GetOrAdd(userSessionID, _ => new SemaphoreSlim(1, 1));
        await connectionGate.WaitAsync();
        HeadlessClientInfo? currentClient = null;
        try
        {
            if (headlessService.HeadlessClients.TryGetValue(userSessionID, out currentClient))
            {
                await currentClient.LifecycleGate.WaitAsync();
                try
                {
                    lock (currentClient) currentClient.IsReconnecting = true;
                }
                finally
                {
                    currentClient.LifecycleGate.Release();
                }
            }

            // A room may be ending or may not have registered its host yet.
            // Profile/player lookup omits both cases, while the headless room
            // is always keyed directly by this profile ID.
            if (matchService.GetMatch(userSessionID) != null)
            {
                // A restarted headless process cannot resume the previous raid.
                // End it through the normal path so reservation and insurance
                // state are cleared before this socket becomes available again.
                await matchService.EndMatch(userSessionID, EFikaMatchEndSessionMessage.HostShutdown);
            }

            // The peer can disconnect while the previous raid is being cleaned.
            if (ws.State != WebSocketState.Open)
            {
                return;
            }

            _headlessWebSockets[userSessionID] = ws;
            headlessService.HeadlessClients.AddOrUpdate(userSessionID,
            _ =>
            {
                isNewClient = true;
                return new HeadlessClientInfo(ws, EHeadlessStatus.READY);
            },
            (_, existing) =>
            {
                oldSocket = existing.WebSocket;
                return new HeadlessClientInfo(ws, EHeadlessStatus.READY);
            });
        }
        finally
        {
            connectionGate.Release();
        }

        if (oldSocket != null)
        {
            try
            {
                await oldSocket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation,
                    "Replaced by new connection", CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.Error($"{ex.Message}", ex);
            }
        }

        var name = headlessHelper.GetHeadlessNickname(userSessionID);

        await webhookService.SendWebhookMessage($"Headless client {name} has connected");
        await notificationWebSocket.BroadcastAsync(new HeadlessConnectedNotification
        {
            Name = name
        });
        if (!isNewClient)
        {
            logger.Info($"Headless client {name} reconnected and replaced existing session");
        }
    }

    public async Task OnMessageAsync(byte[] rawData, WebSocketMessageType messageType, WebSocket ws, HttpContext context)
    {
        if (messageType != WebSocketMessageType.Text || rawData.Length > 1024) return;

        try
        {
            using var document = JsonDocument.Parse(rawData);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != "HeadlessReady") return;

            string? reservationToken = null;
            if (root.TryGetProperty("ReservationToken", out var token))
            {
                if (token.ValueKind == JsonValueKind.String)
                    reservationToken = token.GetString();
                else if (token.ValueKind != JsonValueKind.Null)
                    return;
            }

            // Resolve the profile from the authoritative socket index.  The
            // authentication context alone cannot prove that an old socket
            // still owns this headless after a replacement connection.
            var sessionId = _headlessWebSockets.FirstOrDefault(
                entry => ReferenceEquals(entry.Value, ws)).Key;
            if (string.IsNullOrEmpty(sessionId)) return;

            await headlessService.MarkClientReadyAsync(sessionId, ws, reservationToken);
        }
        catch (JsonException exception)
        {
            logger.Warning($"Ignoring malformed Headless readiness message: {exception.Message}");
        }
    }

    public async Task OnCloseAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var userSessionID = _headlessWebSockets.FirstOrDefault(x => ReferenceEquals(x.Value, ws)).Key;

        if (!string.IsNullOrEmpty(userSessionID))
        {
            var connectionGate = _connectionGates.GetOrAdd(userSessionID, _ => new SemaphoreSlim(1, 1));
            await connectionGate.WaitAsync();
            try
            {
                logger.Debug($"[{GetSocketId()}] Deleting client {userSessionID}");

                // The close callback for a replaced socket can arrive after a
                // new socket has already started another raid. Re-check
                // authority while holding the per-profile gate before ending
                // anything; an old callback must never stop the new match.
                if (!_headlessWebSockets.TryGetValue(userSessionID, out var currentSocket) ||
                    !ReferenceEquals(currentSocket, ws) ||
                    (headlessService.HeadlessClients.TryGetValue(userSessionID, out var currentInfo) &&
                     !ReferenceEquals(currentInfo.WebSocket, ws)))
                {
                    return;
                }

                if (headlessService.HeadlessClients.TryGetValue(userSessionID, out var authoritativeClient))
                {
                    await authoritativeClient.LifecycleGate.WaitAsync();
                    try
                    {
                        if (!ReferenceEquals(authoritativeClient.WebSocket, ws)) return;
                        lock (authoritativeClient) authoritativeClient.IsReconnecting = true;
                    }
                    finally
                    {
                        authoritativeClient.LifecycleGate.Release();
                    }

                    await EndAuthoritativeMatchAsync(userSessionID);

                    if (headlessService.HeadlessClients.TryGetValue(userSessionID, out var client) &&
                        ReferenceEquals(client, authoritativeClient) &&
                        ReferenceEquals(client.WebSocket, ws))
                        headlessService.HeadlessClients.TryRemove(new(userSessionID, client));
                    _headlessWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(userSessionID, ws));
                }
                else
                {
                    // A failed replacement can leave the socket index alive
                    // after the profile entry was removed. Still terminate a
                    // match keyed by this headless session before dropping
                    // the last connection index.
                    await EndAuthoritativeMatchAsync(userSessionID);
                }

                if (!headlessService.HeadlessClients.ContainsKey(userSessionID))
                    _headlessWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(userSessionID, ws));
            }
            finally
            {
                connectionGate.Release();
            }
        }
    }

    private async Task EndAuthoritativeMatchAsync(string userSessionID)
    {
        if (matchService.GetMatch(userSessionID) == null) return;
        try
        {
            await matchService.EndMatch(userSessionID, EFikaMatchEndSessionMessage.HostShutdown);
        }
        catch (Exception exception)
        {
            // MatchService uses finally blocks for insurance/match cleanup;
            // still remove the dead socket even if persistence reports an
            // error so it cannot be advertised as available.
            logger.Error($"Failed to end headless match {userSessionID} during websocket close", exception);
        }
    }
}
