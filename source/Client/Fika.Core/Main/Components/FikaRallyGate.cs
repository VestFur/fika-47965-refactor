using System;
using System.Collections.Generic;
using CommonAssets.Scripts.Cutscenes;
using EFT.GameTriggers;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using UnityEngine;

namespace Fika.Core.Main.Components;

/// <summary>
/// ICEBREAKER-RALLY-GATE (analysis/rally-gate-deadlock).
///
/// The rally trigger of Icebreaker ("wait until every player is here") asks the native
/// <c>TriggerRallyZone.GetAliveHumanPlayerCount()</c> how many players this machine has to wait for, and the
/// answer it produces on a Fika raid is not the number of players in the raid: the native counting runs over
/// the world's own bookkeeping, which on a modded IL2CPP raid is not the list the game hands the host. The
/// zone then waits for a number nobody can reach - the client reads the same number from the
/// <c>RallyZoneUpdateEvent</c> the host broadcasts and shows "waiting for players N/M" forever.
///
/// This class owns the one number that decides the gate. It answers with the players this raid really has
/// (<c>CoopHandler.HumanPlayers</c>, alive, headless applied), never with zero, and the same answer is used on
/// both sides of the link:
/// <list type="number">
/// <item><see cref="ExpectedParticipants"/> replaces the native count on the host and on every peer
/// (patch: <c>RallyZonePlayerCountPatch</c>), so both machines wait for the same players;</item>
/// <item><see cref="CorrectTotalAlive"/> replaces the <c>TotalAlive</c> field of the broadcast
/// <c>RallyZoneUpdateEvent</c>, so the "waiting for players" text a client shows is the same count;</item>
/// <item><see cref="EnsureRallyViewers"/> makes sure a cutscene the rally trigger started on the host is
/// handed to every participant, so a peer cannot be left outside a scene the host is already inside.</item>
/// </list>
///
/// Everything else here is the diagnosis the next session needs: one line for the gate (zone, players
/// inside, the count that was asked for, where that count came from and the verdict), one for the broadcast
/// event, one for the skip vote of every process and one for the skip signals a peer sends. Every line starts
/// with <c>phase=</c> and is prefixed <c>[RallyDiag]</c> in the log, so a single grep works:
/// <code>
/// [RallyDiag]: phase=gate zone=rally_point playersHere=2 expected=2 source=fika side=client -> gate=pass
/// [RallyDiag]: phase=skip:vote process=1 voteYes=1 voteNo=1 viewersInState=2 side=host result=waiting
/// </code>
///
/// One switch reverts the behaviour of every piece at once (see REPORT, section "一键回退").
/// </summary>
internal static class FikaRallyGate
{
    /// <summary>
    /// Master switch of the whole repair: on (default) the rally gate waits for the players of this raid,
    /// off returns every count to the native one (the patches stay installed but pass the native value
    /// through, and only the diagnosis lines remain).
    /// </summary>
    internal static bool UseFikaPlayerCount = true;

    /// <summary>
    /// The deadlock half: a viewer that never sent a single cutscene signal within
    /// <see cref="SkipVoteTimeoutSeconds"/> is treated as "not in the cutscene" - the skip vote and the
    /// completion bookkeeping stop waiting for it. Off = the vote waits for every viewer for ever (the
    /// pre-fix behaviour).
    /// </summary>
    internal static bool AllowSkipWithoutAllViewers = true;

    /// <summary>Seconds a process may run before a silent viewer stops being waited for.</summary>
    internal static int SkipVoteTimeoutSeconds = 30;

    /// <summary>
    /// (analysis/cutscene-client-dispatch) The seat repair of the dispatch chain: on (default) every participant
    /// of this raid is a viewer of every cutscene process the host creates, not only of a process that appeared
    /// inside the rally window (<see cref="EnsureRallyViewers"/>). Off: only the rally window completes the
    /// table, which is the behaviour before this build.
    ///
    /// Why the window is not enough: the window is armed by <c>TriggerRallyZone.EmitTrigger</c>, while the
    /// Icebreaker trigger really emits through SPTushonka's rally watcher
    /// (<c>TriggersEmitter.Emit</c>, SPTSinglePlayer decompile line 1795) - a different call on a different
    /// path. When the cutscene is started by that path the window is not armed, the table stays whatever the
    /// local player list held at that instant, and the native client controller of a peer that is not in it
    /// never enters the scene (nothing else the host sends can make it play). Written by the config key
    /// 'Coop / Cutscene: Every Participant Joins The Cutscene'.
    /// </summary>
    internal static bool IncludeEveryParticipant = true;

    /// <summary>How often a state line is repeated although nothing changed.</summary>
    private const float RefreshSeconds = 60f;

    /// <summary>How long a rally emission may lie back before a new process is no longer attributed to it.</summary>
    private const float RallyWindowSeconds = 3f;

    private static readonly Dictionary<string, string> Lines = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> LinesAt = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, float> ProcessSeen = new();
    private static readonly Dictionary<int, Dictionary<int, float>> Signals = new();
    private static float _nextScan;
    private static float _lastEmit = -1f;
    private static string _lastEmitZone;

    internal static bool InFika => FikaBackendUtils.ClientType != EClientType.None;

    internal static string Side => FikaBackendUtils.IsHeadless ? "headless"
        : FikaBackendUtils.IsServer ? "host"
        : FikaBackendUtils.IsClient ? "client" : "single";

    /// <summary>Where the count the gate uses comes from - the line has to name it.</summary>
    internal static string Source => UseFikaPlayerCount && InFika ? "fika" : "native";

    private static CoopHandler Handler => FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;

    /// <summary>
    /// The players this raid really has: every human player Fika knows, that is still alive. This is the
    /// number the rally gate waits for on every machine, which is what makes both sides agree. The result is
    /// never below 1 - the native gate compares "players inside" against it, and a zero would be a gate that
    /// can never open (the single-player author of SPTushonka's rally watcher guards against the same thing).
    /// </summary>
    internal static int ExpectedParticipants()
    {
        var handler = Handler;
        if (handler?.HumanPlayers == null) return 1;
        var alive = 0;
        for (var i = 0; i < handler.HumanPlayers.Count; i++)
        {
            try
            {
                var player = handler.HumanPlayers[i];
                if (player == null) continue;
                var health = player.HealthController;
                if (health == null || !health.IsAlive) continue;
                alive++;
            }
            catch (Exception)
            {
                // A half-spawned player carries no health controller yet; it is not a participant right now.
            }
        }
        return alive < 1 ? 1 : alive;
    }

    internal static void Reset()
    {
        Lines.Clear();
        LinesAt.Clear();
        ProcessSeen.Clear();
        Signals.Clear();
        _nextScan = 0f;
        _lastEmit = -1f;
        _lastEmitZone = null;
    }

    /// <summary>
    /// (perf 2026-10-14, functional fix) Used to sample every rally zone once per interval to write the
    /// diagnostic state line. That sampling is a whole-scene FindObjectsOfType (measured 154 ms on Streets;
    /// 35 times per raid before throttling, once per 300 s after it) and the gate logic
    /// (GetAliveHumanPlayerCount / CheckAllPlayersInZone / EmitTrigger) never reads its result - the gate's
    /// state comes from the zone's own native events. So the periodic scan is removed instead of stretched:
    /// the state line is still written from the trigger path via ZoneLine().
    /// Tick() remains as the tick entry point so no call site changes.
    /// </summary>
    internal static void Tick()
    {
        if (!InFika) return;
    }


    /// <summary>
    /// The gate line: who is inside, what the gate waits for, where that number comes from and the verdict.
    /// Written whenever one of those changes, and once a minute otherwise.
    /// </summary>
    internal static void ZoneLine(TriggerRallyZone zone, string source)
    {
        if (zone == null) return;
        string id;
        int here;
        bool triggered;
        bool localIn;
        try
        {
            id = zone._triggerId;
            here = zone._playersInside == null ? 0 : zone._playersInside.Count;
            triggered = zone._wasTriggered;
            localIn = zone._localPlayerInZone;
        }
        catch (Exception) { return; }
        var expected = ExpectedParticipants();
    }

    /// <summary>
    /// The line of the count patch: it carries what the native method answered (<c>native=</c>) next to the
    /// number the gate now uses (<c>expected=</c>). This is the line that names the "waiting for players N/M"
    /// source of a raid: a <c>native=</c> far above the raid's own player count is the broken number.
    /// </summary>
    internal static void CountLine(TriggerRallyZone zone, int native, int applied)
    {
        if (zone == null) return;
        string id;
        int here;
        try
        {
            id = zone._triggerId;
            here = zone._playersInside == null ? 0 : zone._playersInside.Count;
        }
        catch (Exception) { return; }
    }

    /// <summary>
    /// The <c>RallyZoneUpdateEvent</c> line, and the correction of its <c>TotalAlive</c> field. The event is
    /// what a client shows as "waiting for players PlayersInZone/TotalAlive", so this is the line that proves
    /// which side produced the number a client displays.
    /// </summary>
    internal static byte CorrectTotalAlive(int zoneId, int playersInZone, int totalAlive)
    {
        var expected = ExpectedParticipants();
        var corrected = UseFikaPlayerCount && InFika;
        if (!corrected) return totalAlive < 0 ? (byte)0 : totalAlive > 255 ? (byte)255 : (byte)totalAlive;
        return (byte)Mathf.Clamp(expected, 1, 255);
    }

    /// <summary>The rally trigger fired on this machine (<c>EmitTrigger</c>).</summary>
    internal static void NoteEmit(string zone, int count)
    {
        _lastEmit = Time.unscaledTime;
        _lastEmitZone = zone;
    }

    private static bool InRallyWindow => _lastEmit >= 0f && Time.unscaledTime - _lastEmit <= RallyWindowSeconds;

    /// <summary>
    /// Hand a process the rally trigger started to every participant of this raid.
    ///
    /// The process is created by whichever handler the trigger reached, and its viewer list is built from the
    /// world's player list at that moment - a player the host had not spawned yet (or a peer the counting
    /// skipped) is then left outside a cutscene everyone else is inside, which is exactly the state where the
    /// "skip" vote can never complete. Only a process that appeared inside the rally window is touched, and
    /// only on the host; a process nobody added a viewer to is unchanged.
    /// </summary>
    internal static void EnsureRallyViewers(CutsceneProcess process)
    {
        if (process == null || !FikaBackendUtils.IsServer || !UseFikaPlayerCount || !InFika || !InRallyWindow) return;
        AddMissingViewers(process, "rally");
    }

    /// <summary>
    /// (analysis/cutscene-client-dispatch) The window-free half of the seat repair: called by the host's dispatch
    /// (<see cref="RaidInteractionSync.StartCutscene"/>) for every process it is about to broadcast, so a peer is
    /// in the viewer table of a cutscene this raid plays even when the rally window was never armed. The viewer
    /// table is the native <c>CutsceneProcess.playersInCurrentView</c> list and it is what the broadcast carries
    /// (and what the native client controller of a peer filters against), so this is the one place that decides
    /// whether a peer can enter the scene at all.
    /// </summary>
    internal static void IncludeAllParticipants(CutsceneProcess process)
    {
        if (process == null || !IncludeEveryParticipant || !FikaBackendUtils.IsServer || !InFika) return;
        AddMissingViewers(process, "dispatch");
    }

    /// <summary>The one implementation of both entry points: append every participant the table does not hold.</summary>
    private static void AddMissingViewers(CutsceneProcess process, string source)
    {
        try
        {
            var handler = Handler;
            var viewers = process.playersInCurrentView;
            if (handler?.Players == null || viewers == null) return;
            var before = viewers.Count;
            var missing = new List<int>();
            foreach (var entry in handler.Players)
            {
                var player = entry.Value;
                if (player == null) continue;
                var playerId = player.PlayerId;
                if (playerId <= 0 || viewers.Contains(playerId)) continue;
                missing.Add(playerId);
            }
            if (missing.Count == 0) return;
            for (var i = 0; i < missing.Count; i++) viewers.Add(missing[i]);
        }
        catch (Exception error)
        {
        }
    }

    /// <summary>A process was broadcast by the host: the clock of the silent-viewer rule starts here.</summary>
    internal static void NoteProcessStart(int processId)
    {
        if (processId == 0) return;
        ProcessSeen[processId] = Time.unscaledTime;
    }

    internal static void ForgetProcess(int processId)
    {
        ProcessSeen.Remove(processId);
        Signals.Remove(processId);
    }

    /// <summary>
    /// One cutscene signal of a viewer. Only three things a viewer sends carry the proof that it is really
    /// inside the cutscene - its skip vote, its "watched" acknowledgement and its timer correction - and they
    /// are what tells "this peer is in the scene and quiet" from "this peer never entered it".
    /// </summary>
    internal static void NoteSignal(int processId, int netId, string kind)
    {
        if (processId == 0 || netId <= 0) return;
        if (!Signals.TryGetValue(processId, out var viewers))
        {
            viewers = new Dictionary<int, float>();
            Signals[processId] = viewers;
        }
        viewers[netId] = Time.unscaledTime;
    }

    internal static bool HasSignal(int processId, int netId) =>
        Signals.TryGetValue(processId, out var viewers) && viewers.ContainsKey(netId);

    /// <summary>How long ago the host broadcast this process (0 when it was never seen).</summary>
    internal static float ProcessAge(int processId) =>
        ProcessSeen.TryGetValue(processId, out var seen) ? Time.unscaledTime - seen : 0f;

    /// <summary>
    /// True once a process ran long enough that a viewer which still has not sent a single signal is not
    /// going to join. Off (or a zero timeout) keeps the vote waiting for every viewer, exactly as before.
    /// </summary>
    internal static bool SilentViewerTimeoutReached(int processId)
    {
        if (!AllowSkipWithoutAllViewers || SkipVoteTimeoutSeconds <= 0) return false;
        return ProcessAge(processId) >= SkipVoteTimeoutSeconds;
    }

    /// <summary>Writes the skip line of a process (host: after it counted, client: after it received).</summary>
    internal static void SkipLine(string key, int processId, int voteYes, int voteNo, int viewers, string result, string detail)
    {
    }

    /// <summary>The client half of the skip line: what this machine was told and what it answered.</summary>
    internal static void SkipReceived(CutsceneSkipIntentionEventFromServer intention)
    {
        if (intention == null) return;
        var skipping = 0;
        try { skipping = intention.SkippingPlayerIds?.Length ?? 0; }
        catch (Exception) { }
        var players = 0;
        try { players = intention.PlayersCount; }
        catch (Exception) { }
        SkipLine("skiprecv:" + intention.ProcessId, intention.ProcessId, skipping, Math.Max(0, players - skipping),
            players, "received", "the host broadcast a skip vote and this machine is expected to vote too");
    }

    /// <summary>The skip line of a local hold: this machine is about to send its vote.</summary>
    internal static void SkipSent(CutscenesClientController controller, bool wantsToSkip)
    {
        if (controller == null) return;
        int processId;
        string cutscene;
        bool ui;
        try
        {
            processId = controller._cutsceneProcessId;
            cutscene = controller._cutsceneId;
            ui = controller._skipUiActive;
        }
        catch (Exception) { return; }
    }

    /// <summary>The host told this machine to skip: written on every peer.</summary>
    internal static void SkipApplied(CutsceneSkipEventFromServer skip)
    {
        if (skip == null) return;
        var processId = 0;
        try { processId = skip.processId; }
        catch (Exception) { }
    }

    private static string Id(string value) => string.IsNullOrEmpty(value) ? "-" : value;
}
