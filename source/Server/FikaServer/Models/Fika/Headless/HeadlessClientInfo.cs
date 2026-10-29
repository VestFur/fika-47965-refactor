using FikaServer.Models.Enums;
using SPTarkov.Server.Core.Models.Common;
using System.Diagnostics.CodeAnalysis;

namespace FikaServer.Models.Fika.Headless;

/// <summary>
/// Record containing data for a headless client
/// </summary>
public record HeadlessClientInfo
{
    [SetsRequiredMembers]
    public HeadlessClientInfo(System.Net.WebSockets.WebSocket webSocket, EHeadlessStatus state)
    {
        WebSocket = webSocket;
        State = state;
    }

    /// <summary>
    /// Websocket of the headless client
    /// </summary>
    public required System.Net.WebSockets.WebSocket WebSocket { get; set; }
    /// <summary>
    /// State of the headless client
    /// </summary>
    public required EHeadlessStatus State { get; set; }
    /// <summary>
    /// The old connection is being replaced and its match is still being
    /// cleaned. Do not offer it to another requester until the new socket is
    /// installed.
    /// </summary>
    public bool IsReconnecting { get; set; }
    /// <summary>
    /// True only after the headless has completed its menu/profile lifecycle
    /// and explicitly reported readiness over the current websocket.
    /// </summary>
    public bool ClientReady { get; private set; }
    /// <summary>
    /// Serializes reservation and connection replacement for this profile.
    /// </summary>
    public SemaphoreSlim LifecycleGate { get; } = new(1, 1);
    /// <summary>
    /// Changes whenever this headless is reserved for a new raid. Distinguishes
    /// two consecutive requests from the same player.
    /// </summary>
    public long ReservationVersion { get; private set; }
    /// <summary>
    /// Wire token for a Headless start request. A new value is issued for every
    /// reservation so a delayed create from an older raid is rejected.
    /// </summary>
    public string? ReservationToken { get; private set; }
    /// <summary>
    /// The reservation token which was just ended.  It remains available for
    /// the client's post-raid readiness handshake, then is cleared only when
    /// a new reservation starts or a replacement socket is installed. Keeping
    /// it after readiness prevents a late null-token report from impersonating
    /// this connection's initial readiness message.
    /// </summary>
    public string? LastEndedReservationToken { get; private set; }
    /// <summary>
    /// A readiness message can race the server's final /raid/leave cleanup.
    /// Keep it until Reset can safely publish the headless again.
    /// </summary>
    public string? PendingReadyToken { get; private set; }
    /// <summary>
    /// The players that are playing on this headless client, only set if the state is IN_RAID
    /// </summary>
    public List<MongoId> Players { get; set; } = [];
    /// <summary>
    /// SessionID of the person who has requested the headless client, it will only ever be set if the status is IN_RAID
    /// </summary>
    public string? RequesterSessionID { get; set; }
    /// <summary>
    /// Allows for checking if the requester has been notified the match has started through the requester WebSocket so he can auto-join
    /// </summary>
    public bool? HasNotifiedRequester { get; set; }
    /// <summary>
    /// The current level of the headless, mainly used for transits
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Sets the info of the client after starting a raid
    /// </summary>
    /// <param name="requestId">The id who requested the raid</param>
    public void StartRaid(string requestId)
    {
        ReservationVersion++;
        ReservationToken = Guid.NewGuid().ToString("N");
        LastEndedReservationToken = null;
        PendingReadyToken = null;
        ClientReady = false;
        RequesterSessionID = requestId;
        HasNotifiedRequester = false;
    }

    /// <summary>
    /// Resets the data of the <see cref="HeadlessClientInfo"/>
    /// </summary>
    public void Reset()
    {
        LastEndedReservationToken = ReservationToken;
        ReservationVersion++;
        ReservationToken = null;
        State = EHeadlessStatus.READY;
        Players.Clear();
        RequesterSessionID = null;
        HasNotifiedRequester = null;
        ClientReady = PendingReadyToken != null &&
            string.Equals(PendingReadyToken, LastEndedReservationToken, StringComparison.Ordinal);
        PendingReadyToken = null;
    }

    /// <summary>
    /// Record a readiness report from this connection. The caller must have
    /// checked socket ownership and hold the client lock.
    /// </summary>
    public bool TryMarkReady(string? reservationToken)
    {
        if (ReservationToken != null)
        {
            if (string.Equals(ReservationToken, reservationToken, StringComparison.Ordinal))
                PendingReadyToken = reservationToken;
            return false;
        }

        if (State != EHeadlessStatus.READY || RequesterSessionID != null ||
            (LastEndedReservationToken == null
                ? reservationToken != null
                : !string.Equals(LastEndedReservationToken, reservationToken, StringComparison.Ordinal)))
            return false;

        ClientReady = true;
        PendingReadyToken = null;
        return true;
    }
}
