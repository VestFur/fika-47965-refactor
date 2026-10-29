using System;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The thresholds of the loading screen / session wait path as *static fields*, so a config entry can be
/// bound to them without another edit here (the same pattern as TransitStartGate: FikaConfig assigns the
/// fields at startup and on every SettingChanged, the call sites only read them).
///
/// Every value is a backstop, never the normal path: a wait ends when the condition it waits for is met,
/// and these numbers only decide when a wait that cannot be satisfied is given up and *reported as such*
/// (one [LoadSync] line, see LoadSync).
///
/// Why they have to be adjustable in the field: the whole class exists because a fixed value was wrong -
/// the client used to give up on the host's location acknowledgement after a fixed 40 s
/// (TarkovApplication_LocalGameCreator_Patch, 2026-10-11 18:58 round: the host needed 145 s to load
/// TarkovStreets), and the host's pre-raid barrier had no bound at all, so it waited minutes for a peer
/// that had already given up. Both are machine- and map-dependent, i.e. exactly the kind of number that
/// must be tunable without a rebuild.
/// </summary>
public static class LoadSyncGate
{
    /// <summary>
    /// Host side: how long the pre-raid barrier (<c>HostGameController.WaitForOtherPlayersToLoad</c>)
    /// waits for a peer that is still connected but has not announced its character. A peer that
    /// disconnects does not consume this budget - <c>FikaServer.OnPeerDisconnected</c> decrements
    /// PlayerAmount and the barrier shrinks immediately. Default 300 s.
    /// </summary>
    public static int HostPeerLoadTimeoutSeconds = 300;

    /// <summary>
    /// Client side: how long the client waits for the host's handshake steps (the location
    /// acknowledgement and the host's own raid initialisation) in a normal raid start. The wait is
    /// additionally bounded by the transport: as soon as the connection to the host is not Connected
    /// any more, it fails immediately instead of using this budget. Default 600 s.
    /// </summary>
    public static int ClientHostWaitCeilingSeconds = 600;

    /// <summary>
    /// Client side: the same ceiling inside a transit reload window (<c>FikaBackendUtils.IsTransit</c>).
    /// This is the window in which both machines have just torn their world down and are loading the next
    /// map, so the host can be minutes away from answering, on a slower disk alone. Kept separate from
    /// the normal ceiling because the transit reload is exactly the case the fixed 40 s cap got wrong.
    /// Default 900 s.
    /// </summary>
    public static int ClientTransitReloadCeilingSeconds = 900;

    /// <summary>
    /// Client side: after this many seconds without an application-level response from the host the
    /// [LoadSync] line escalates to a warning. It never ends the wait by itself (the transport decides
    /// that), it only makes the state visible: a host whose main thread is busy (its own map load) stops
    /// answering while its socket thread keeps the connection alive. Default 30 s.
    /// </summary>
    public static int ClientSilentWarnSeconds = 30;

    /// <summary>The client's wait ceiling for the state this machine is in.</summary>
    public static int ClientCeilingSeconds(bool transit)
    {
        var ceiling = transit
            ? Math.Max(ClientHostWaitCeilingSeconds, ClientTransitReloadCeilingSeconds)
            : ClientHostWaitCeilingSeconds;
        return Math.Max(30, ceiling);
    }

    /// <summary>The values in force, for the [LoadSync] header line of a run.</summary>
    public static string Describe()
    {
        return "hostPeerLoadTimeout=" + HostPeerLoadTimeoutSeconds + "s, clientHostWaitCeiling="
            + ClientHostWaitCeilingSeconds + "s, clientTransitReloadCeiling=" + ClientTransitReloadCeilingSeconds
            + "s, clientSilentWarn=" + ClientSilentWarnSeconds + "s";
    }
}
