using FikaServer.Models.Fika.Headless;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Ws;
using SPTarkov.Server.Core.Utils;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace FikaServer.WebSockets;

[Injectable(InjectionType.Singleton)]
public class HeadlessRequesterWebSocket(SaveServer saveServer, JsonUtil jsonUtil, ISptLogger<HeadlessRequesterWebSocket> logger) : IWebSocketConnectionHandler
{
    private readonly ConcurrentDictionary<string, WebSocket> requesterWebSockets = [];
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> pendingMessages = [];
    private readonly SemaphoreSlim sendGate = new(1, 1);

    public string GetHookUrl()
    {
        return "/fika/headless/requester";
    }

    public string GetSocketId()
    {
        return "Fika Headless Requester";
    }

    public async Task OnConnectionAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        if (!WebSocketAuthHelper.TryGetSessionId(context, out var userSessionID))
        {
            await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid authorization", CancellationToken.None);
            return;
        }

        logger.Debug($"[{GetSocketId()}] User is {userSessionID}");

        if (!saveServer.ProfileExists(userSessionID))
        {
            logger.Error($"[{GetSocketId()}] Invalid user {userSessionID} tried to authenticate!");
            return;
        }

        // Reconnection can arrive before the old socket's close callback.
        // Always make the newest connection authoritative.
        requesterWebSockets.TryGetValue(userSessionID, out var previous);
        requesterWebSockets[userSessionID] = ws;
        if (previous != null && !ReferenceEquals(previous, ws)) previous.Abort();
        await FlushPendingAsync(userSessionID);
    }

    public Task OnMessageAsync(byte[] rawData, WebSocketMessageType messageType, WebSocket ws, HttpContext context)
    {
        // Do nothing
        return Task.CompletedTask;
    }

    public Task OnCloseAsync(WebSocket ws, HttpContext context, string sessionIdContext)
    {
        var userSessionID = requesterWebSockets.FirstOrDefault(x => x.Value == ws).Key;

        if (!string.IsNullOrEmpty(userSessionID))
        {
            logger.Debug($"[{GetSocketId()}] Deleting requester {userSessionID}");

            requesterWebSockets.TryRemove(new KeyValuePair<string, WebSocket>(userSessionID, ws));
        }

        return Task.CompletedTask;
    }

    public async Task SendAsync<T>(string sessionID, T message) where T : IHeadlessWSMessage
    {
        var serialized = jsonUtil.Serialize(message)
            ?? throw new InvalidOperationException("Cannot serialize headless requester notification");

        // Serialize enqueue + flush as one operation.  If the assignment is
        // performed before acquiring sendGate, a second notification can
        // overwrite the first one while its flush is waiting, losing the
        // earlier join event during rapid COMPLETE/status callbacks.
        await sendGate.WaitAsync();
        try
        {
            pendingMessages.GetOrAdd(sessionID, static _ => new ConcurrentQueue<string>()).Enqueue(serialized);
            await FlushPendingUnsafeAsync(sessionID);
        }
        finally { sendGate.Release(); }
    }

    private async Task FlushPendingAsync(string sessionID)
    {
        await sendGate.WaitAsync();
        try { await FlushPendingUnsafeAsync(sessionID); }
        finally { sendGate.Release(); }
    }

    private async Task FlushPendingUnsafeAsync(string sessionID)
    {
        if (!pendingMessages.TryGetValue(sessionID, out var messages) || messages.IsEmpty) return;
        if (!requesterWebSockets.TryGetValue(sessionID, out var ws) || ws.State != WebSocketState.Open)
        {
            logger.Warning($"[{GetSocketId()}] Requester ({sessionID}) is not connected; retaining join notification");
            return;
        }

        try
        {
            while (messages.TryPeek(out var message))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, timeout.Token);
                messages.TryDequeue(out _);
            }

            if (messages.IsEmpty)
                pendingMessages.TryRemove(new KeyValuePair<string, ConcurrentQueue<string>>(sessionID, messages));
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            logger.Warning($"[{GetSocketId()}] Retaining pending join notification for {sessionID}: {exception.Message}");
        }
    }

    public void ClearPending(string sessionID)
    {
        if (!string.IsNullOrEmpty(sessionID)) pendingMessages.TryRemove(sessionID, out _);
    }
}
