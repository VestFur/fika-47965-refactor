using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Comfort.Common;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using SPTushonka.Common.Http;

namespace Fika.Core.Networking;

/// <summary>
/// Client used to verify that a connection can be established before initializing the <see cref="FikaClient"/> and <see cref="CoopGame"/>
/// </summary>
public class FikaPingingClient : INetEventListener, INatPunchListener, IDisposable
{
    /// <summary>
    /// The network client manager instance.
    /// </summary>
    public NetManager NetClient;

    /// <summary>
    /// Indicates if a successful response has been received from the server.
    /// </summary>
    public bool Received;

    /// <summary>
    /// Indicates if the connection attempt was rejected by the server.
    /// </summary>
    public bool Rejected;

    /// <summary>
    /// Indicates if the server is currently in progress (busy).
    /// </summary>
    public bool InProgress;

    private readonly ManualLogSource _logger;
    private List<IPEndPoint> _endPoints;
    private NetDataWriter _writer;
    private List<IPEndPoint> _candidates;
    private float _firstResponseTime;
    private bool _hasResponse;
    private bool _natIntroductionRequested;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;
    private string _serverId;
    private const float _responseWaitSeconds = 1f;

    /// <summary>Longest raw response body kept in a single log line.</summary>
    private const int MaxLoggedBodyChars = 512;

    /// <summary>
    /// Why <see cref="Init"/> refused to start pinging, or <see langword="null"/> while it did not
    /// fail. The join screens append it to their error box: "the host never published an address" and
    /// "the address exists but does not answer" need different words for the player.
    /// </summary>
    public string FailureReason { get; private set; }

    /// <summary>
    /// True when the server that holds the match answered with something that is not a usable host
    /// record, i.e. the missing address is on the host's side. False for a transport/backend failure
    /// (no answer or a non-2xx status), where checking our own server connection is the useful advice.
    /// </summary>
    public bool HostAddressMissing { get; private set; }

    public FikaPingingClient()
    {
        _logger = Logger.CreateLogSource("Fika.PingingClient");
        _cts = new();
    }

    /// <summary>
    /// Initializes the pinging client and attempts to resolve the server endpoint.
    /// </summary>
    /// <param name="serverId">The server identifier to connect to.</param>
    /// <returns>True if initialization was successful, otherwise false.</returns>
    public bool Init(string serverId)
    {
        NetClient = new(this)
        {
            UnconnectedMessagesEnabled = true,
            NatPunchEnabled = true,
            UseNativeSockets = NativeSocket.IsSupported
        };

        _endPoints = [];
        _candidates = [];
        _writer = new();

        _serverId = serverId;
        GetHostRequest body = new(serverId);
        GetHostResponse result;
        try
        {
            result = FikaRequestHandler.GetHost(body);
        }
        catch (Exception exception)
        {
            // The server answered, but not with a host record: an unknown match id answers an empty
            // body, a broken or unreachable backend throws out of the transport (and its message
            // carries the HTTP status). Both are reported with the raw answer before the join stops.
            HostAddressMissing = FikaRequestHandler.LastGetHostResponseBody != null;
            FailureReason = $"The raid server did not return a host record for '{serverId}' "
                + $"(/fika/raid/gethost): {exception.GetBaseException().Message}";
            LogHostLookupFailure(serverId, "Host lookup failed");
            return false;
        }

        FikaBackendUtils.ServerGuid = result.ServerGuid;
        _logger.LogInfo(result.ToString());

        FikaBackendUtils.IsHostNatPunch = result.NatPunch;
        FikaBackendUtils.IsHeadlessGame = result.IsHeadless;

        if (result.Port == 0)
        {
            HostAddressMissing = true;
            FailureReason = $"The host has not published an address on that server (their side): "
                + $"'/fika/raid/gethost' returned port 0 for server '{serverId}'.";
            LogHostLookupFailure(serverId, "Host port was empty when pinging");
            return false;
        }

        // The server can advertise an unavailable WAN address alongside valid
        // LAN addresses. Resolve each independently so one bad address does not
        // prevent joining a reachable host. NAT introduction can also supply
        // the only endpoint when the advertised list is empty.
        foreach (var address in result.IPs ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(address)) continue;
            try
            {
                var candidate = NetworkUtils.ResolveRemoteAddress(address, result.Port);
                if (candidate.AddressFamily == AddressFamily.InterNetworkV6
                    && !FikaPlugin.Instance.Settings.UseIPv6.Value)
                {
                    _logger.LogInfo($"Skipping IPv6 candidate {address}: IPv6 is disabled in the network settings");
                    continue;
                }
                _endPoints.Add(candidate);
            }
            catch (Exception exception)
            {
                _logger.LogWarning($"Cannot resolve host address {address}: {exception.Message}");
            }
        }

        if (_endPoints.Count == 0 && !result.NatPunch)
        {
            HostAddressMissing = true;
            FailureReason = $"The host has not published an address on that server (their side): "
                + $"the advertised address(es) [{DescribeAddresses(result.IPs)}] could not be used (port {result.Port}).";
            LogHostLookupFailure(serverId, "Host did not provide a usable address for pinging");
            return false;
        }

        NetClient.Start();

        if (FikaBackendUtils.IsHostNatPunch)
        {
            NetClient.NatPunchModule.Init(this);

            var natPunchServerIP = FikaPlugin.Instance.NatPunchServerIP;
            var natPunchServerPort = FikaPlugin.Instance.NatPunchServerPort;

            if (result.UseFikaNatPunchServer)
            {
                natPunchServerIP = FikaPlugin.FikaNATPunchMasterServer;
                natPunchServerPort = FikaPlugin.FikaNATPunchMasterPort;
            }

            try
            {
                var resolved = NetUtils.ResolveAddress(natPunchServerIP, AddressFamily.InterNetwork); // no ipv6 for natpunch master
                var endPoint = new IPEndPoint(resolved, natPunchServerPort);
                var token = $"Client:{FikaBackendUtils.ServerGuid}";
                _natIntroductionRequested = true;
                _ = Task.Run(() => NatIntroduceTask(endPoint, token, _cts.Token));
                _logger.LogInfo($"Sent NAT Introduce Request to Nat Punch Server: {endPoint}");
            }
            catch (Exception exception)
            {
                _logger.LogWarning($"Cannot use NAT punching server {natPunchServerIP}:{natPunchServerPort}: {exception.Message}");
                if (_endPoints.Count == 0)
                {
                    // NAT punching was the only way in and its server is unusable, so no address is
                    // left to try. Same report as the other "no address" exits.
                    HostAddressMissing = true;
                    FailureReason = $"The host has not published an address on that server (their side): "
                        + "no usable address was advertised and the NAT punch server could not be used.";
                    LogHostLookupFailure(serverId,
                        "Host did not provide a usable address for pinging (its NAT punch server is unusable too)");
                    return false;
                }
            }
        }

        return true;
    }

    private async Task NatIntroduceTask(IPEndPoint endPoint, string token, CancellationToken ct = default)
    {
        _logger.LogInfo("Send NAT Introduce Request task started.");

        while (!ct.IsCancellationRequested)
        {
            NetClient?.NatPunchModule?.SendNatIntroduceRequest(endPoint, token);

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }

        _logger.LogInfo("Send NAT Introduce Request task stopped.");
    }

    /// <summary>
    /// One log line that says everything needed to tell "the host never published an address" from
    /// "the address exists but nothing answers": who was asked, on which server, with which HTTP
    /// status and which raw answer, plus the plain-language conclusion.
    /// </summary>
    private void LogHostLookupFailure(string serverId, string summary)
    {
        var body = FikaRequestHandler.LastGetHostResponseBody;
        var failure = FikaRequestHandler.LastGetHostFailure;
        var message = $"[FikaPingingClient] {summary}: the host has not published an address on that server (their side); "
            + $"serverId={serverId}, server={FikaRequestHandler.ServerHost}, "
            + $"http={(failure == null ? "2xx (the http layer throws for every other status)" : failure)}, "
            + $"body={(body == null ? "<none: no body was returned>" : Abbreviate(body))}";
        _logger.LogError(message);
        
    }

    private static string DescribeAddresses(string[] addresses)
    {
        if (addresses == null || addresses.Length == 0)
        {
            return "none";
        }

        return string.Join(", ", addresses.Select(address => string.IsNullOrWhiteSpace(address) ? "<blank>" : address));
    }

    /// <summary>
    /// The addresses this attempt worked with: the ones that were pinged and the ones that answered.
    /// Both matter, because an endpoint leaves the ping list as soon as it replies.
    /// </summary>
    private string DescribeEndpoints()
    {
        var pinged = _endPoints is { Count: > 0 }
            ? string.Join(", ", _endPoints.Select(endpoint => endpoint.ToString()))
            : "none";
        var answered = _candidates is { Count: > 0 }
            ? string.Join(", ", _candidates.Select(candidate => candidate.ToString()))
            : "none";
        return $"pinged=[{pinged}], answered=[{answered}]";
    }

    private static string Abbreviate(string value) =>
        value.Length <= MaxLoggedBodyChars ? value : value[..MaxLoggedBodyChars] + $"...(+{value.Length - MaxLoggedBodyChars} chars)";

    public async Task<bool> AttemptToPingHost(string knockMessage, CancellationToken ct = default)
    {
        var deadline = Time.realtimeSinceStartup + 12.5f;
        while (!Rejected && !Received &&
               (Time.realtimeSinceStartup < deadline || (_hasResponse &&
                Time.realtimeSinceStartup - _firstResponseTime < _responseWaitSeconds)))
        {
            ct.ThrowIfCancellationRequested();
            PingEndPoint(knockMessage);
            if (NetClient != null)
            {
                NetClient.PollEvents();
                NetClient.NatPunchModule?.PollEvents();
            }

            await Task.Delay(250, ct);
        }

        if (!Received)
        {
            // (analysis/transit-timeout-crash) The handshake gave up without ever reaching a host: the
            // ping loop returned false after its 12.5 s deadline. One line states it as an event,
            // because it happens *before* a raid exists and therefore before any [LoadSync] line.
            var pingedCount = _endPoints?.Count ?? 0;
            
            var logError = "Unable to connect to the session!";
            if (Rejected)
            {
                logError += $" Connection was rejected! [{FikaBackendUtils.ServerGuid}] did not match the server's Guid or data was malformed.";
            }
            if (InProgress)
            {
                logError += " Session already in progress and you are not active in the session!";
            }

            // The server did hand out an address this time (Init returned true), so the failure is on
            // the wire, not in the match record: name the server, the address that was tried and the
            // body the server answered with when the join started.
            var body = FikaRequestHandler.LastGetHostResponseBody;
            var failure = FikaRequestHandler.LastGetHostFailure;
            logError += $" serverId={_serverId}, server={FikaRequestHandler.ServerHost}, tried={DescribeEndpoints()}, "
                + $"http={(failure == null ? "2xx (the http layer throws for every other status)" : failure)}, "
                + $"body={(body == null ? "<none: no body was returned>" : Abbreviate(body))}";
            FikaGlobals.LogError(logError);
            _logger.LogError(logError);
            

            FailureReason = $"The host's server did publish an address ({DescribeEndpoints()}), but nothing answered on it. "
                + "The host's port is not reachable from here (closed, not forwarded, or a stale address).";
        }

        return Received;
    }

    private void RemoveEndpointIfExists(IPEndPoint remote)
    {
        for (var i = _endPoints.Count - 1; i >= 0; i--)
        {
            var ep = _endPoints[i];
            if (ep.Address.Equals(remote.Address) && ep.Port == remote.Port)
            {
                _endPoints.RemoveAt(i);
                _logger.LogInfo($"Stopped pinging {ep}");
                return;
            }
        }
    }

    /// <summary>
    /// Sends a ping message to the configured endpoints.
    /// </summary>
    /// <param name="message">The message to send.</param>
    public void PingEndPoint(string message)
    {
        if (_disposed) return;
        // Finalize selection after wait window
        if (!Received && _hasResponse && Time.realtimeSinceStartup - _firstResponseTime >= _responseWaitSeconds)
        {
            var selected = SelectBestCandidate();

            if (selected != null)
            {
                CommitEndpoint(selected);
                Received = true;
                _endPoints.Clear(); // stop all further pings
                return;
            }

            _logger.LogError($"{_responseWaitSeconds} seconds has passed, but no candidate could be found?");
        }

        _writer.Reset();
        _writer.Put(message);

        foreach (var ipEndPoint in _endPoints)
        {
            NetClient.SendUnconnectedMessage(_writer.AsReadOnlySpan(), ipEndPoint);
        }
    }

    private void CommitEndpoint(IPEndPoint ep)
    {
        FikaBackendUtils.RemoteEndPoint = ep;
        FikaBackendUtils.LocalPort = (ushort)NetClient.LocalPort;

        _logger.LogInfo($"Picked candidate {ep.Address}:{ep.Port}, using LocalPort: {NetClient.LocalPort}");
    }

    private IPEndPoint SelectBestCandidate()
    {
        if (_candidates.Count == 0)
        {
            return null;
        }

        var preferIPv6 = FikaPlugin.Instance.Settings.UseIPv6.Value && FikaPlugin.Instance.Settings.PreferIPv6.Value;

        if (preferIPv6)
        {
            // IPv6 has no NAT: a direct candidate is the lowest latency path, and a
            // unique local address is the IPv6 equivalent of the host's LAN address.
            var ipv6 = _candidates.FirstOrDefault(c => c.Address.AddressFamily == AddressFamily.InterNetworkV6 && IsPrivate(c.Address))
                ?? _candidates.FirstOrDefault(c => c.Address.AddressFamily == AddressFamily.InterNetworkV6);
            if (ipv6 != null)
            {
                _logger.LogInfo($"Selected IPv6 candidate {ipv6.Address}:{ipv6.Port} (prefer IPv6 is enabled)");
                return ipv6;
            }
        }

        // prefer LAN
        var lan = _candidates.FirstOrDefault(c => IsPrivate(c.Address));
        if (lan != null)
        {
            return lan;
        }

        // otherwise first responder WAN
        return _candidates[0];
    }

    /// <inheritdoc/>
    public void OnConnectionRequest(ConnectionRequest request)
    {
        // Do nothing
    }

    /// <inheritdoc/>
    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        // Do nothing
    }

    /// <inheritdoc/>
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        // Do nothing
    }

    /// <inheritdoc/>
    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        // Do nothing
    }

    /// <summary>
    /// Handles unconnected network messages received from remote endpoints.
    /// </summary>
    /// <param name="remoteEndPoint">The remote endpoint that sent the message.</param>
    /// <param name="reader">The packet reader containing the message data.</param>
    /// <param name="messageType">The type of unconnected message received.</param>
    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        if (_disposed) return;
        if (reader.TryGetString(out var result))
        {
            switch (result)
            {
                case "fika.hello":
                    RemoveEndpointIfExists(remoteEndPoint);

                    if (!_hasResponse)
                    {
                        _firstResponseTime = Time.realtimeSinceStartup;
                        _hasResponse = true;
                    }

                    if (!_candidates.Any(c =>
                        c.Address.Equals(remoteEndPoint.Address) &&
                        c.Port == remoteEndPoint.Port))
                    {
                        _candidates.Add(remoteEndPoint);
                        _logger.LogInfo($"Received candidate: {remoteEndPoint}");
                    }

                    break;
                case "fika.inprogress":
                    InProgress = true;
                    break;
                case "fika.reject":
                    Rejected = true;
                    break;
                default:
                    _logger.LogError("Data was not as expected");
                    break;
            }
        }
        else
        {
            _logger.LogError("Could not parse string");
        }
    }

    private bool IsPrivate(IPAddress ip)
    {
        switch (ip.AddressFamily)
        {
            case AddressFamily.InterNetwork: // IPv4
                {
#if DEBUG
                    _logger.LogInfo($"Checking {ip} which is IPv4");
#endif
                    var b = ip.GetAddressBytes();
                    return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
                }
            case AddressFamily.InterNetworkV6: // IPv6
                {
#if DEBUG
                    _logger.LogInfo($"Checking {ip} which is IPv6");
#endif
                    var b = ip.GetAddressBytes();
                    // fc00::/7 > check top 7 bits of first byte (0b1111_1100 = 0xfc)
                    return (b[0] & 0b1111_1100) == 0b1111_1100;
                }
            default:
                return false;
        }
    }

    /// <inheritdoc/>
    public void OnPeerConnected(NetPeer peer)
    {
        // Do nothing
    }

    /// <inheritdoc/>
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        // Do nothing
    }

    /// <inheritdoc/>
    public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        // Do nothing
    }

    /// <inheritdoc/>
    public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        if (_disposed || !string.Equals(token, $"Client:{FikaBackendUtils.ServerGuid}", StringComparison.Ordinal)) return;
        if (!_natIntroductionRequested) return;
        if (_endPoints.Any(endpoint => endpoint.Equals(targetEndPoint)) ||
            _candidates.Any(endpoint => endpoint.Equals(targetEndPoint))) return;

        _logger.LogInfo($"Received endpoint {targetEndPoint} from the NAT punching server.");
        _endPoints.Add(targetEndPoint);
        // The introduction can expose both LAN and WAN routes. Keep receiving
        // them until one answers; a LAN-first selection can then choose the
        // route reachable by peers on the same network.
        _cts?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _endPoints?.Clear();
        _candidates?.Clear();
        _endPoints = null;
        _candidates = null;
        _writer?.Reset();
        _writer = null;

        NetClient?.Stop();
        _cts.Dispose();
        if (!FikaSingleton<FikaPingingClient>.TryRelease(this))
        {
            _logger.LogError("Failed to release FikaPingingClient Singleton!");
        }
        _logger.LogInfo("Stopped FikaPingingClient");
    }
}
