using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using Fika.Core.Main.Custom;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Models.Admin;
using Fika.Core.Networking.Models.Headless;
using Fika.Core.Networking.Models.Presence;
using Fika.Core.UI.Models;
using Fika.Core.Networking.Serialization;
using SPTushonka.Common.Http;

namespace Fika.Core.Networking.Http;

public static class FikaRequestHandler
{
    private static readonly Client _httpClient;

    static FikaRequestHandler()
    {
        _httpClient = RequestHandler.HttpClient;
    }

    /// <summary>
    /// Resolves the WAN address this machine announces when it hosts a raid.
    ///
    /// Returns <see langword="null"/> when no provider answers. This is a degradation and
    /// never a failure: the address is read by exactly one caller (FikaServer.Init, the raid
    /// host) and the client join path does not touch it. Throwing here used to turn an
    /// offline install into a "RunChecks" error line that hid the real problem.
    /// </summary>
    public static async Task<IPAddress> GetPublicIP()
    {
        var client = _httpClient.HttpClient;
        string[] ipv4Urls = [
            "https://api.ipify.org/",
            "https://checkip.amazonaws.com/",
            "https://ipv4.icanhazip.com/"
        ];

        // An IPv6 only network has no IPv4 egress at all, so a second pass over
        // providers that answer over IPv6 keeps the raid host announceable there
        // instead of burning three timeouts and failing.
        string[] ipv6Urls = [
            "https://api64.ipify.org/",
            "https://ipv6.icanhazip.com/",
            "https://ifconfig.co/ip"
        ];

        // The first provider that fails is the one that explains the whole lookup, and every provider
        // after it usually repeats the same transport failure. Reported once per cause accordingly: the
        // first failure keeps the full evidence (message, stack, inner exceptions), the others are one
        // line each without a stack. Before this, one reset TLS handshake produced one stack block per
        // provider plus the first-chance entries of every throw inside HttpClient
        // (2026-10-10, analysis/log-noise-cleanup: api.ipify.org reset the handshake, 10 first-chance
        // entries, ~54 lines of stack for a single provider failure).
        string firstFailure = null;

        void ReportFailure(string url, AddressFamily family, string reason, Exception exception)
        {
            if (firstFailure == null)
            {
                firstFailure = url + ": " + reason;
                if (exception == null)
                {
                    FikaGlobals.LogWarning($"Request to {url} timed out after 5 seconds.");
                    
                }
                else
                {
                    FikaGlobals.LogWarning($"Could not get public IP address from [{url}]. Error: {reason}");
                    
                }

                return;
            }

            
        }

        async Task<IPAddress> Query(string[] urls, AddressFamily family)
        {
            foreach (var url in urls)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                try
                {
                    var response = await client.GetAsync(url, cts.Token);
                    response.EnsureSuccessStatusCode();

                    var ipString = (await response.Content.ReadAsStringAsync()).Trim();
                    if (!IPAddress.TryParse(ipString, out var ipAddress))
                    {
                        continue;
                    }

                    if (ipAddress.AddressFamily == family)
                    {
                        return ipAddress;
                    }

                    FikaGlobals.LogWarning($"Provider {url} answered with a {ipAddress.AddressFamily} address ({ipAddress}), expected {family}");
                }
                catch (OperationCanceledException)
                {
                    ReportFailure(url, family, "timed out after 5 seconds", null);
                }
                catch (Exception ex)
                {
                    ReportFailure(url, family, ex.GetBaseException().Message, ex);
                }
            }

            return null;
        }

        var ipv4 = await Query(ipv4Urls, AddressFamily.InterNetwork);
        if (ipv4 != null)
        {
            return ipv4;
        }

        if (FikaPlugin.Instance.Settings.UseIPv6.Value)
        {
            var ipv6 = await Query(ipv6Urls, AddressFamily.InterNetworkV6);
            if (ipv6 != null)
            {
                FikaGlobals.LogInfo($"Announcing the public IPv6 address {ipv6} for this raid host");
                return ipv6;
            }
        }

        const string failure = "Could not retrieve or parse the external IP address from any provider.";
        // Degraded from error to warning, with the first reason in the same line: no provider answering
        // costs the raid host its announced WAN address (set 'Force IP' to announce one) and leaves the
        // join path untouched. The reason itself is what a tester needs, and it used to be buried under
        // the per-provider stack blocks.
        FikaGlobals.LogWarning(firstFailure == null ? failure : failure + " First failure: " + firstFailure);
        
        return null;
    }

    private static byte[] EncodeBody<T>(T o)
    {
        var serialized = FikaJson.Serialize(o);
        return Encoding.UTF8.GetBytes(serialized);
    }

    private static T DecodeBody<T>(byte[] data)
    {
        var json = Encoding.UTF8.GetString(data);
        return FikaJson.Deserialize<T>(json);
    }

    private static async Task<T> GetJsonAsync<T>(string path)
    {
        var response = await _httpClient.GetAsync(path);
        return DecodeBody<T>(response);
    }

    private static T GetJson<T>(string path)
    {
        // Only transport runs off-thread. Game objects must be decoded on the
        // caller's thread, especially while Plugin.Load blocks the Unity loop.
        var response = Task.Run(() => _httpClient.GetAsync(path))
            .GetAwaiter()
            .GetResult();
        return DecodeBody<T>(response);
    }

    private static async Task<T2> PostJsonAsync<T1, T2>(string path, T1 o)
    {
        var data = EncodeBody(o);
        var response = await _httpClient.PostAsync(path, data);
        return DecodeBody<T2>(response);
    }

    private static T2 PostJson<T1, T2>(string path, T1 o)
    {
        var data = EncodeBody(o);
        var response = Task.Run(() => _httpClient.PostAsync(path, data))
            .GetAwaiter()
            .GetResult();
        return DecodeBody<T2>(response);
    }

    private static async Task<byte[]> PutJsonAsync<T>(string path, T o)
    {
        var data = EncodeBody(o);
        return await _httpClient.PutAsync(path, data);
    }

    private static byte[] PutJson<T>(string path, T o)
    {
        return Task.Run(() => PutJsonAsync(path, o))
            .GetAwaiter()
            .GetResult();
    }

    public static BotDifficulties GetBotDifficulties()
    {
        return GetJson<BotDifficulties>("/singleplayer/settings/bot/difficulties/");
    }

    public static ClientConfigModel GetClientConfig()
    {
        return GetJson<ClientConfigModel>("/fika/client/config");
    }

    public static NatPunchServerConfigModel GetNatPunchServerConfig()
    {
        return GetJson<NatPunchServerConfigModel>("/fika/natpunchserver/config");
    }

    public static RestartAfterRaidAmountModel GetHeadlessRestartAfterRaidAmount()
    {
        return GetJson<RestartAfterRaidAmountModel>("/fika/headless/restartafterraidamount");
    }

    public static async Task UpdatePing(PingRequest data)
    {
        await PutJsonAsync("/fika/update/ping", data);
    }

    public static async Task UpdateSetStatus(SetStatusModel data)
    {
        await PutJsonAsync("/fika/update/setstatus", data);
    }

    public static async Task UpdatePlayerSpawn(PlayerSpawnRequest data)
    {
        await PutJsonAsync("/fika/update/playerspawn", data);
    }

    public static void UpdateSetHost(SetHostRequest data)
    {
        PutJson("/fika/update/sethost", data);
    }

    public static void RaidLeave(PlayerLeftRequest data)
    {
        PutJson("/fika/raid/leave", data);
    }

    public static CreateMatch RaidJoin(MatchJoinRequest data)
    {
        return PostJson<MatchJoinRequest, CreateMatch>("/fika/raid/join", data);
    }

    public static void UpdateAddPlayer(AddPlayerRequest data)
    {
        PutJson("/fika/update/addplayer", data);
    }

    public static async Task RaidCreate(CreateMatch data)
    {
        // The SPT 5.0 server intentionally returns an empty body for raid
        // creation. Treat a completed request as success and let transport
        // exceptions propagate to the caller.
        await PutJsonAsync("/fika/raid/create", data);
    }

    /// <summary>
    /// The backend address this client is pointed at (for example https://127.0.0.1:6969), for
    /// diagnostics only. The launcher owns this value; it is printed next to every host lookup
    /// failure so a report says which server was asked.
    /// </summary>
    public static string ServerHost
    {
        get
        {
            try
            {
                var host = RequestHandler.Host;
                return string.IsNullOrWhiteSpace(host) ? "<empty>" : host;
            }
            catch (Exception exception)
            {
                return "<unavailable: " + exception.GetBaseException().Message + ">";
            }
        }
    }

    /// <summary>
    /// Raw JSON of the last <see cref="GetHost"/> answer, or <see langword="null"/> when that call
    /// produced none. The join path prints it when a host answers without an address: the decoded DTO
    /// alone cannot show whether the server sent an empty list, a "null" body for an unknown match or
    /// something else entirely.
    /// </summary>
    public static string LastGetHostResponseBody { get; private set; }

    /// <summary>
    /// Transport-level failure of the last <see cref="GetHost"/> call, or <see langword="null"/> when
    /// it returned. The HTTP layer (SPTushonka's Client.SendAsync) throws
    /// "Http response status code: {code}" for every non-2xx answer and never hands the body back, so
    /// the status is only ever visible here, as this text. Reading the status separately would need a
    /// change inside that layer; a raw-body channel for the one call that matters avoids it.
    /// </summary>
    public static string LastGetHostFailure { get; private set; }

    public static GetHostResponse GetHost(GetHostRequest data)
    {
        LastGetHostResponseBody = null;
        LastGetHostFailure = null;

        var payload = EncodeBody(data);
        byte[] response;
        try
        {
            // Same transport path as PostJson: only the transfer runs off-thread.
            response = Task.Run(() => _httpClient.PostAsync("/fika/raid/gethost", payload))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            LastGetHostFailure = exception.GetBaseException().Message;
            throw;
        }

        // Kept before decoding so a body that fails to deserialize is still readable.
        LastGetHostResponseBody = Encoding.UTF8.GetString(response);
        return FikaJson.Deserialize<GetHostResponse>(LastGetHostResponseBody);
    }

    public static LobbyEntry[] LocationRaids(RaidSettings data)
    {
        return PostJson<RaidSettings, LobbyEntry[]>("/fika/location/raids", data);
    }

    public static Dictionary<string, string> AvailableReceivers(AvailableReceiversRequest data)
    {
        return PostJson<AvailableReceiversRequest, Dictionary<string, string>>("/fika/senditem/availablereceivers", data);
    }

    public static async Task<RaidSettingsResponse> GetRaidSettings(RaidSettingsRequest data)
    {
        return await PostJsonAsync<RaidSettingsRequest, RaidSettingsResponse>("/fika/raid/getsettings", data);
    }

    public static async Task<DownloadProfileResponse> GetProfile()
    {
        return await GetJsonAsync<DownloadProfileResponse>("/fika/profile/download");
    }

    public static async Task<StartHeadlessResponse> StartHeadless(StartHeadlessRequest request)
    {
        return await PostJsonAsync<StartHeadlessRequest, StartHeadlessResponse>("/fika/raid/headless/start", request);
    }

    public static AvailableHeadlessClientsRequest[] GetAvailableHeadlesses()
    {
        return GetJson<AvailableHeadlessClientsRequest[]>("/fika/headless/available");
    }

    public static async Task RegisterPlayer(RegisterPlayerRequest request)
    {
        await PutJsonAsync("/fika/raid/registerPlayer", request);
    }

    public static async Task PlayerDied(AddPlayerRequest request)
    {
        await PutJsonAsync("/fika/update/playerdied", request);
    }

    public static CheckVersionResponse CheckServerVersion()
    {
        return GetJson<CheckVersionResponse>("/fika/client/check/version");
    }

    public static FikaPlayerPresence[] GetPlayerPresences()
    {
        return GetJson<FikaPlayerPresence[]>("/fika/presence/get");
    }

    public static void SetPresence(FikaSetPresence data)
    {
        PutJson("/fika/presence/set", data);
    }

    public static FikaPlayerPresence[] SetAndGetPresence(FikaSetPresence data)
    {
        return PostJson<FikaSetPresence, FikaPlayerPresence[]>("/fika/presence/setget", data);
    }

    public static CurrentSettingsResponse GetServerSettings()
    {
        return GetJson<CurrentSettingsResponse>("/fika/admin/get");
    }

    public static SetSettingsResponse SaveServerSettings(SetSettingsRequest request)
    {
        return PostJson<SetSettingsRequest, SetSettingsResponse>("/fika/admin/set", request);
    }
}
