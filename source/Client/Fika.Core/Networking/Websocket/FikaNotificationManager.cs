using BepInEx.Logging;
using Comfort.Common;
using EFT.Communications;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Serialization;
using Fika.Core.Networking.Websocket.Notifications;
using SPTushonka.Common.Http;
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fika.Core.Networking.Websocket;

internal sealed class FikaNotificationManager : IDisposable
{
    private static readonly ManualLogSource _logger = Logger.CreateLogSource("FikaNotificationManager");
    public static FikaNotificationManager Instance;
    public static bool Exists => Instance != null;
    public string Host { get; }
    public string Url { get; }
    public string SessionId { get; }
    public bool Connected => _webSocket?.State == WebSocketState.Open;

    /// <summary>
    /// <see cref="Connected"/> for callers that must not care whether the manager exists yet.
    /// The presence roster is empty until this socket is up (the server registers a player in
    /// the socket's connection handler), so the panel asks this before it decides that "nobody
    /// is online" is the truth.
    /// </summary>
    public static bool IsConnected => Instance is { } instance && instance.Connected;

    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private ClientWebSocket _webSocket;
    private TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public FikaNotificationManager()
    {
        Instance = this;
        Host = RequestHandler.Host.Replace("http", "ws");
        SessionId = RequestHandler.SessionId;
        Url = $"{Host}/fika/notification/";
        _ = ConnectLoopAsync();
    }

    /// <summary>
    /// Creates the single notification client and records which call site asked for it.
    ///
    /// This socket is the client's presence registration: the server adds the player to its
    /// online list in the connection handler of <c>/fika/notification/</c>
    /// (<c>FikaServer.WebSockets.NotificationWebSocket.OnConnectionAsync</c> ->
    /// <c>PresenceService.AddPlayerPresence</c>). If it is never opened, <c>/fika/presence/get</c>
    /// answers an empty array and the main menu roster shows no row at all - not even the local
    /// player, which is exactly what the 47888 run showed after the application-level patch went
    /// dead on the renamed game method.
    ///
    /// Creation is asked for from two hooks (the application notification initializer and, as a
    /// fallback, <c>MenuScreen.Awake</c>), so it has to stay idempotent: a second manager would
    /// open a second socket and the server treats a second connection of the same session as a
    /// reconnect, aborting the socket it already had.
    /// </summary>
    /// <param name="source">The call site that asked for the manager.</param>
    /// <returns>The manager that is now alive.</returns>
    internal static FikaNotificationManager EnsureCreated(string source)
    {
        var existing = Instance;
        if (existing != null)
        {
            return existing;
        }

        // Called from a patch postfix, so a failure here must not travel into the game method
        // that was patched: it is reported instead and the fallback hook gets another chance.
        try
        {
            var created = new FikaNotificationManager();
            if (FikaPlugin.Instance != null)
            {
                FikaPlugin.Instance.NotificationManager = created;
            }

            
            _logger.LogInfo($"Notification websocket client created by {source} ({created.Url})");
            return created;
        }
        catch (Exception exception)
        {
            
            _logger.LogError($"Notification websocket client could not be created by {source}: {exception}");
            return null;
        }
    }

    public void Connect()
    {
        // Do not leak a faulted fire-and-forget task into Unity's synchronization
        // context.  The reconnect loop owns retries; this call only nudges it.
        _ = EnsureConnectedAsync(TimeSpan.Zero).ContinueWith(
            task =>
            {
                if (task.IsFaulted && task.Exception != null)
                {
                    _logger.LogDebug($"Notification websocket is not connected yet: {task.Exception.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);
    }

    public async Task EnsureConnectedAsync(TimeSpan? timeout = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FikaNotificationManager));
        if (Connected) return;
        lock (_sync)
        {
            if (_connected.Task.IsCompleted) _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (timeout is null) await _connected.Task.ConfigureAwait(false);
        else await _connected.Task.WaitAsync(timeout.Value).ConfigureAwait(false);
    }

    private async Task ConnectLoopAsync()
    {
        while (!_stop.IsCancellationRequested && !_disposed)
        {
            ClientWebSocket socket = null;
            try
            {
                lock (_sync)
                {
                    // A failed attempt must not poison the completion source used
                    // by the next caller of EnsureConnectedAsync.
                    if (_connected.Task.IsCompleted)
                    {
                        _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
                socket = FikaBackendSocket.Create(SessionId);
                lock (_sync) _webSocket = socket;
                await socket.ConnectAsync(new Uri(Url), _stop.Token).ConfigureAwait(false);
                _connected.TrySetResult(true);
                _logger.LogInfo("Connected to Fika notification websocket");
                await ReceiveLoopAsync(socket, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (_stop.IsCancellationRequested && WebSocketShutdown.IsTeardown(ex))
            {
                // Close() aborts the socket, and everything that was in flight on it comes back as one of
                // these. That is the normal end of this loop and not a finding: nothing is logged, and the
                // connection waiter is not faulted - nobody is waiting on it any more.
                break;
            }
            catch (Exception ex)
            {
                _connected.TrySetException(ex);
                _logger.LogWarning($"Notification websocket: {ex.GetBaseException().Message}");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_webSocket, socket)) _webSocket = null;
                }
                socket?.Dispose();
            }
            if (!await WebSocketShutdown.WaitBeforeReconnectAsync(TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The manager is closing: the pending read was cancelled, which is exactly how this loop
                // is supposed to end. It is not a warning and must not be logged as one.
                return;
            }
            catch (Exception exception) when (token.IsCancellationRequested && WebSocketShutdown.IsTeardown(exception))
            {
                // Close() aborts the socket; the read that was in flight comes back as an aborted,
                // disposed or failed stream instead of a clean close frame.
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            message.SetLength(0);
            // Even notification Description getters access native localization and
            // session time. Queue the whole dispatch, not just the final UI call.
            MainThreadDispatcher.Post(() => Dispatch(json), token);
        }
    }

    private void Dispatch(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("type", out var typeElement)) return;
            if (!Enum.TryParse<EFikaNotification>(typeElement.ToString(), true, out var type))
                if (!typeElement.TryGetInt32(out var numeric) || !Enum.IsDefined(typeof(EFikaNotification), numeric)) return;
                else type = (EFikaNotification)numeric;
#if DEBUG
            _logger.LogDebug($"Received type: {type}");
#endif
            switch (type)
            {
                case EFikaNotification.StartedRaid:
                    var started = FikaJson.Deserialize<StartRaidNotification>(json);
                    if (!FikaGlobals.IsInRaid) Show(started.Description, started.Icon);
                    break;
                case EFikaNotification.SentItem:
                    var sent = FikaJson.Deserialize<ReceivedSentItemNotification>(json);
                    Show(sent.Description, sent.Icon);
                    break;
                case EFikaNotification.PushNotification:
                    var push = FikaJson.Deserialize<PushNotification>(json);
                    Show(push.Description, push.Icon);
                    break;
                case EFikaNotification.OpenAdminSettings:
                    var admin = FikaJson.Deserialize<OpenAdminMenuNotification>(json);
                    if (admin.Success) AdminSettingsUIScript.Create();
                    break;
                case EFikaNotification.ShutdownClient:
                    if (FikaBackendUtils.IsHeadless) Application.Quit();
                    break;
                case EFikaNotification.HeadlessConnected:
                    var connected = FikaJson.Deserialize<HeadlessConnectedNotification>(json);
                    Show(connected.Description, connected.Icon);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to process Fika notification: {ex}");
        }
    }

    private static void Show(string description, ENotificationIconType icon)
    {
        var notifier = Singleton<PreloaderUI>.Instance?.NotifierView;
        notifier?.NotificationReceivedHandler(new CustomNotification(description,
            ENotificationDurationType.Default, icon, new Il2CppSystem.Nullable<Color>(), true));
    }

    public void Close()
    {
        _disposed = true;
        _stop.Cancel();
        lock (_sync)
        {
            try { _webSocket?.Abort(); } catch { }
            _webSocket = null;
        }
    }

    public void Dispose() => Close();
}












