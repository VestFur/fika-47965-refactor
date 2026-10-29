using FikaServer.Models.Fika.WebSocket;
using FikaServer.Services;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers.Ws;
using SPTarkov.Server.Core.Utils;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace FikaServer.WebSockets;

[Injectable(InjectionType.Singleton)]
public class NotificationWebSocket(JsonUtil jsonUtil, ISptLogger<NotificationWebSocket> logger, PresenceService fikaPresenceService) : IWebSocketConnectionHandler
{
    private ConcurrentDictionary<string, WebSocket> clientWebSockets = [];

    public string GetHookUrl()
    {
        return "/fika/notification/";
    }

    public string GetSocketId()
    {
        return "Fika Notification Manager";
    }

    public async Task OnConnectionAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        if (!WebSocketAuthHelper.TryGetSessionId(context, out var userSessionID))
        {
            await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid authorization", CancellationToken.None);
            return;
        }

        logger.Debug($"[{GetSocketId()}] User is {userSessionID}");

        WebSocket? previousSocket = null;
        if (!clientWebSockets.TryAdd(userSessionID, ws))
        {
            // A reconnect can arrive before the previous socket's close
            // callback. Replace only that previous connection; its eventual
            // OnCloseAsync must not remove the new socket or presence.
            if (clientWebSockets.TryGetValue(userSessionID, out previousSocket) &&
                !clientWebSockets.TryUpdate(userSessionID, ws, previousSocket))
            {
                clientWebSockets[userSessionID] = ws;
            }
        }

        // Do not leave the superseded connection alive after a reconnect. Its
        // close callback is identity-guarded and therefore cannot remove the
        // newly installed socket or presence entry.
        if (previousSocket != null && !ReferenceEquals(previousSocket, ws))
        {
            try { previousSocket.Abort(); }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
        }

        fikaPresenceService.AddPlayerPresence(userSessionID);
    }

    public Task OnMessageAsync(byte[] rawData, WebSocketMessageType messageType, WebSocket ws, HttpContext context)
    {
        // Do nothing
        return Task.CompletedTask;
    }

    public Task OnCloseAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var client = clientWebSockets.Where(x => x.Value == ws).FirstOrDefault();

        if (client.Key != null)
        {
            logger.Debug($"[{GetSocketId()}] Deleting client {client.Key}");

            if (clientWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(client.Key, ws)))
            {
                fikaPresenceService.RemovePlayerPresence(client.Key);
            }
        }

        return Task.CompletedTask;
    }

    public async Task SendAsync<T>(MongoId sessionID, T message) where T : IFikaNotification
    {
        // Client is not online or not currently connected to the websocket.
        if (!clientWebSockets.TryGetValue(sessionID, out var ws))
        {
            return;
        }

        // Client was formerly connected to the websocket, but may have
        // connection issues as it didn't run OnCloseAsync.  SendAsync throws
        // for CloseReceived/CloseSent/Aborted as well, so only an open socket
        // is eligible for a notification.
        if (ws.State != WebSocketState.Open)
        {
            if (clientWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(sessionID, ws)))
            {
                fikaPresenceService.RemovePlayerPresence(sessionID);
            }
            return;
        }

        var serialized = jsonUtil.Serialize(message);
        if (string.IsNullOrEmpty(serialized))
        {
            logger.Warning($"[{GetSocketId()}] Could not serialize notification for {sessionID}");
            return;
        }

        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(serialized), WebSocketMessageType.Text, true,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or
            InvalidOperationException or OperationCanceledException)
        {
            // A stale socket must not abort a broadcast to every other
            // player. Remove only this exact connection; a reconnect may have
            // already installed a newer socket for the same session.
            if (clientWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(sessionID, ws)))
            {
                fikaPresenceService.RemovePlayerPresence(sessionID);
            }

            logger.Debug($"[{GetSocketId()}] Notification socket for {sessionID} was unavailable: {exception.Message}");
        }
    }

    public async Task BroadcastAsync<T>(T message) where T : IFikaNotification
    {
        foreach (var websocket in clientWebSockets)
        {
            await SendAsync(websocket.Key, message);
        }
    }
}
