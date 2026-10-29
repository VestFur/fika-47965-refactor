using System;
using System.Collections.Generic;
using CommonAssets.Scripts.Cutscenes;
using EFT;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.Components;

/// <summary>
/// BD (analysis/cutscene-client-native-trigger): the client plays the story cutscene through its own native
/// trigger chain, in place.
///
/// Why this exists. The route "host dispatch -&gt; CutSceneStartEvent -&gt; the local client controller" needs the
/// host to have sent a CutsceneStartPacket first. In the round of analysis/logs-coop3-20261013-122918 the client
/// has no single 'phase=dispatch' line - and that line is written outside the 'Coop / Diagnostics' switch, once
/// per process id - so this raid's host never dispatched at all. What the client did have is its own native
/// chain: the rally trigger reached SPTushonka's TriggerCutscenePatches.Start, which called the native
/// CutscenesServerController.StartCutscene, and Fika's own CutsceneStartSyncPatch.Prefix short-circuited it to
/// __result = null - which is exactly the "trigger cutscene: 'Icebreaker_1' refused" line of LogOutput.log:1947.
/// The client therefore had neither a picture nor a process.
///
/// This class takes that dropped request and runs it natively in place: once the client's own trigger asks for
/// cutscene X, the bridge lets the machine's own <c>CutscenesServerController.StartCutscene</c> run - the very
/// call the host runs - unless the host already dispatched the same one (a host dispatch wins, nothing is
/// triggered locally, and that verdict is known before any wait because the start packet is recorded on arrival).
/// The local server controller creates the process, the bridge hands it to this machine's client controller, and
/// when the local viewer has watched it the native <c>EndCutscene</c> of that same controller runs the timeline's
/// end actions (the door of the map).
///
/// One cutscene is triggered locally at most once (<see cref="Played"/>); a cutscene the host dispatched is never
/// triggered locally (<see cref="HostStarted"/>); and a cutscene this machine already plays natively is not
/// started a second time when the host packet arrives later (see FikaClient.RaidInteractions.cs). The host path
/// and the single-player path are untouched: every entry point here starts with <see cref="Enabled"/>, which is
/// false on a host.
///
/// One switch reverts the whole behaviour (<see cref="UseNativeClientTrigger"/>).
/// </summary>
internal static class FikaLocalCutsceneTrigger
{
    /// <summary>
    /// Master switch of the whole repair: on (default) a client that asked its own native chain to start a
    /// cutscene runs that native chain itself when no host packet for the same cutscene arrives; off restores
    /// the previous behaviour (the local start is dropped, exactly as CutsceneStartSyncPatch did before).
    /// </summary>
    internal static bool UseNativeClientTrigger = true;

    /// <summary>
    /// The seat table of a locally triggered process: on (default) it holds this machine's own player only, so
    /// the native completion counter of the process reaches its own total (1 of 1) and the native EndCutscene -
    /// with the timeline's end actions - runs on this machine by itself. Off keeps the native list (every
    /// non-AI alive player of this world, the host character included), which for a scene nobody else can watch
    /// means the native end never comes.
    /// </summary>
    internal static bool SingleLocalViewer = true;

    /// <summary>
    /// Seconds a queued local request waits before the bridge triggers the native chain itself. Zero on purpose:
    /// a cutscene the host dispatched is already known when the packet lands (HostStarted), so waiting only delays
    /// the cutscenes that exist on this machine alone.
    /// </summary>
    // (2026-10-27 T5) 0f：客机自起**不等主机**。这个值只决定"排队中的请求什么时候由本机点火"，不决定
    // 谁先起过场——主机派发的过场由 HostStarted 让路（NoteHostStart 在收包时立刻记下，比任何延时都早），
    // 所以给主机留 2 秒既拦不住"两个进程撞同一个 cutscene id"，又让只存在于本机的过场（终点站 / 破冰船）
    // 白等 2 秒。W30 的 2f 是这个误判，按用户要求改回 0。
    internal static float LocalTriggerDelaySeconds = 0f;

    /// <summary>
    /// (2026-10-21) How long a local process may stay open before this machine ends it by itself. It is the
    /// second end condition of <see cref="PendingEnds"/>: a scene nobody acknowledges would otherwise hold the
    /// process - and the immunity of its start - for the rest of the raid.
    /// </summary>
    internal static float LocalProcessTimeoutSeconds = 60f;

    /// <summary>Cutscenes this machine's own trigger asked to start, with the moment it asked.</summary>
    private static readonly Dictionary<string, float> Requests = new(StringComparer.Ordinal);
    /// <summary>Cutscenes this machine already ran through its own native StartCutscene.</summary>
    private static readonly HashSet<string> Played = new(StringComparer.Ordinal);
    /// <summary>Cutscenes the host started (a CutsceneStartPacket for it was seen on this client).</summary>
    private static readonly HashSet<string> HostStarted = new(StringComparer.Ordinal);
    /// <summary>Process ids this machine created itself, mapped to their cutscene (the local native process).</summary>
    private static readonly Dictionary<int, string> LocalProcesses = new();
    /// <summary>
    /// (2026-10-21) The local processes that have not ended yet, keyed by process id: a process this machine
    /// created itself is closed from here - on the local <c>TimelineShowed</c> or after
    /// <see cref="LocalProcessTimeoutSeconds"/> - with the native
    /// <c>CutscenesServerController.EndCutscene(cutsceneId, processId, process, playersId, skip)</c>
    /// (interop :94617). The native end of a process whose seat table this machine narrowed to itself may never
    /// come, and a process that stays open keeps the sniper immunity its start granted.
    /// </summary>
    private static readonly Dictionary<int, PendingLocalProcess> PendingEnds = new();
    /// <summary>The one cutscene the bridge granted to the next native StartCutscene call.</summary>
    private static string _grant;
    private static float _nextScan;

    /// <summary>True only on a client of a Fika raid - the host keeps its own path.</summary>
    internal static bool Enabled => UseNativeClientTrigger && FikaBackendUtils.IsClient;

    internal static void Reset()
    {
        Requests.Clear();
        Played.Clear();
        HostStarted.Clear();
        LocalProcesses.Clear();
        PendingEnds.Clear();
        _grant = null;
        _nextScan = 0f;
    }

    /// <summary>Clears the cutscene state of one world when the raid ends (a new raid must not inherit it).</summary>
    internal static void ForgetWorld()
    {
        Requests.Clear();
        Played.Clear();
        HostStarted.Clear();
        LocalProcesses.Clear();
        PendingEnds.Clear();
        _grant = null;
        _nextScan = 0f;
    }

    /// <summary>Whether this machine already plays (or played) this cutscene through its own native trigger.</summary>
    internal static bool HasPlayedLocally(string cutsceneId) =>
        !string.IsNullOrEmpty(cutsceneId) && Played.Contains(cutsceneId);

    /// <summary>
    /// Whether this process is one this machine created itself (the local native trigger). The boundary uses it
    /// to tell "my own cutscene, my own native end" from "the host's process, the host's end".
    /// </summary>
    internal static bool OwnsLocalProcess(int processId, out string cutscene) =>
        LocalProcesses.TryGetValue(processId, out cutscene) && !string.IsNullOrEmpty(cutscene);

    /// <summary>The host started this cutscene (a start packet reached this client): the local trigger stands down.</summary>
    internal static void NoteHostStart(string cutsceneId)
    {
        if (!string.IsNullOrEmpty(cutsceneId)) HostStarted.Add(cutsceneId);
    }

    /// <summary>
    /// The client's own native chain asked for a cutscene and Fika dropped the call (the prefix of
    /// <c>CutsceneStartSyncPatch</c>). The request is remembered and re-run natively unless the host speaks first.
    /// </summary>
    internal static void NoteNativeRequest(string cutsceneId)
    {
        if (!Enabled || string.IsNullOrEmpty(cutsceneId)) return;
        if (Played.Contains(cutsceneId) || HostStarted.Contains(cutsceneId))
        {
            return;
        }
        if (Requests.ContainsKey(cutsceneId)) return;

        // (2026-10-21) Event driven: the request is run natively right here instead of waiting two seconds for a
        // host packet. The host still wins when its own start arrives first (HostStarted), and the 2 s window that
        // made the door open two seconds before the scene is gone.
        var world = Comfort.Common.Singleton<GameWorld>.Instance;
        if (world?.CutscenesServerController != null)
        {
            Fire(world, cutsceneId);
            return;
        }

        Requests[cutsceneId] = UnityEngine.Time.unscaledTime;
    }

    /// <summary>
    /// The one grant the prefix reads: the bridge is about to let this machine's own native StartCutscene through.
    /// The seat table of that call is narrowed to this machine when <see cref="SingleLocalViewer"/> is on.
    /// </summary>
    internal static bool GrantNativeStart(string cutsceneId,
        Il2CppSystem.Collections.Generic.List<int> viewers)
    {
        if (!Enabled || string.IsNullOrEmpty(cutsceneId)) return false;
        if (!string.Equals(_grant, cutsceneId, StringComparison.Ordinal)) return false;
        _grant = null;
        if (!SingleLocalViewer || viewers == null) return true;
        var local = RaidInteractionSync.LocalPlayerId;
        if (local <= 0) return true;
        // The table is edited in place: the caller's own list is the one the native body reads.
        viewers.Clear();
        viewers.Add(local);
        return true;
    }

    /// <summary>The native body ran: this machine owns a process of its own now.</summary>
    internal static void NoteLocalProcess(CutsceneProcess process)
    {
        if (process == null) return;
        var cutscene = process.currentCutsceneId;
        if (string.IsNullOrEmpty(cutscene)) return;
        Played.Add(cutscene);
        Requests.Remove(cutscene);
        LocalProcesses[process.processId] = cutscene;
        // (2026-10-21) The process is registered for its own end here: <see cref="Tick"/> closes it when the local
        // viewer is done or when it outlived its timeout, so a scene whose native end never comes cannot keep the
        // immunity its start granted.
        PendingEnds[process.processId] = new PendingLocalProcess
        {
            Cutscene = cutscene,
            Process = process,
            StartedAt = UnityEngine.Time.unscaledTime
        };
        FikaGlobals.LogInfo("[SyncDiag] local-process start pid=" + process.processId, "FikaLocalCutsceneTrigger");
    }

    /// <summary>
    /// Runs on the raid tick of this machine (RaidInteractionSync.Tick). It is the decision point of the repair:
    /// a request whose host signal never came is turned into one native StartCutscene of this machine.
    /// </summary>
    internal static void Tick(GameWorld world)
    {
        // (2026-10-21) The end half runs first and independently of the switch: a process already registered must
        // still be closed after <see cref="UseNativeClientTrigger"/> was turned off mid-raid.
        PumpEnds(world);
        if (!Enabled || world == null || Requests.Count == 0) return;
        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextScan) return;
        _nextScan = now + 0.25f;
        foreach (var entry in new List<KeyValuePair<string, float>>(Requests))
        {
            var cutscene = entry.Key;
            if (Played.Contains(cutscene))
            {
                Requests.Remove(cutscene);
                continue;
            }
            if (HostStarted.Contains(cutscene))
            {
                Requests.Remove(cutscene);
                continue;
            }
            if (now - entry.Value < LocalTriggerDelaySeconds) continue;
            Requests.Remove(cutscene);
            Fire(world, cutscene);
        }
    }

    /// <summary>
    /// The repair itself: this machine's own native StartCutscene, with the grant that lets the call through the
    /// Fika prefix. Everything after it is native - the process, the local viewer's playback and the native end
    /// with the timeline's end actions.
    /// </summary>
    private static void Fire(GameWorld world, string cutscene)
    {
        var server = world == null ? null : world.CutscenesServerController;
        if (server == null)
        {
            return;
        }
        // The seat table of the native call: the native list (every non-AI alive player of this world, the host
        // character included) is built here so that SingleLocalViewer off behaves exactly like the trigger that
        // called us; on (default) the grant narrows it to this machine's own player instead.
        var viewers = new Il2CppSystem.Collections.Generic.List<int>();
        var local = RaidInteractionSync.LocalPlayerId;
        // (2026-10-20) Without a local player id the grant cannot narrow the seat table to 1/1, and the process
        // would then wait for seats this machine cannot complete - the exact deadlock this trigger exists to
        // avoid. Skip this poll; the request stays queued and is retried on the next tick.
        if (local <= 0) return;
        try
        {
            var all = world.AllAlivePlayersList;
            if (all != null)
                for (var i = 0; i < all.Count; i++)
                {
                    var player = all[i];
                    if (player == null || player.IsAI) continue;
                    viewers.Add(player.PlayerId);
                }
        }
        catch (Exception)
        {
            // A half-spawned player list is not a reason to skip the trigger: fall back to this machine's own id.
        }
        if (viewers.Count == 0 && local > 0) viewers.Add(local);
        _grant = cutscene;
        CutsceneProcess process;
        try
        {
            process = server.StartCutscene(cutscene, viewers, false, false, 0f);
        }
        catch (Exception error)
        {
            _grant = null;
            return;
        }
        if (process == null)
        {
            _grant = null;
            return;
        }

        // (2026-10-21) The process is not touched here: the postfix of the native start
        // (CutsceneStartSyncPatch -> RaidInteractionSync.PlayLocalNativeProcess -> NoteLocalProcess) hands it to
        // the end registry, which is the one place a local process is opened and closed.
    }

    /// <summary>
    /// (2026-10-21) The local viewer's timeline was shown on this machine (the <c>TimelineShowed</c> postfix of
    /// <c>CutsceneViewedLocalProcessPatch</c>). The process of this machine is marked for its native end; the end
    /// itself runs once in <see cref="Tick"/>, so the notification never ends a process inside the native
    /// callback that is reporting it.
    /// </summary>
    internal static void NoteViewed(int processId, string cutsceneId)
    {
        if (PendingEnds.Count == 0) return;
        if (processId > 0 && PendingEnds.TryGetValue(processId, out var byId))
        {
            byId.Viewed = true;
            return;
        }

        if (string.IsNullOrEmpty(cutsceneId)) return;
        foreach (var entry in PendingEnds)
        {
            if (string.Equals(entry.Value.Cutscene, cutsceneId, StringComparison.Ordinal))
            {
                entry.Value.Viewed = true;
            }
        }
    }

    /// <summary>
    /// The end half of the lifecycle: every registered process whose viewer is done, or which outlived
    /// <see cref="LocalProcessTimeoutSeconds"/>, is ended once. Runs on the raid tick, before the request scan, so
    /// an end is never delayed by a pending local start.
    /// </summary>
    private static void PumpEnds(GameWorld world)
    {
        if (PendingEnds.Count == 0) return;
        var now = UnityEngine.Time.unscaledTime;
        List<int> ending = null;
        foreach (var entry in PendingEnds)
        {
            var pending = entry.Value;
            if (pending.Viewed || PlaybackLeftThisProcess(world, pending) ||
                now - pending.StartedAt >= LocalProcessTimeoutSeconds)
            {
                (ending ??= new List<int>()).Add(entry.Key);
            }
        }

        if (ending == null) return;
        for (var i = 0; i < ending.Count; i++)
        {
            EndLocal(world, ending[i]);
        }
    }

    /// <summary>
    /// (2026-10-27 T1) Whether the playback of this machine has left a local process behind.
    /// <para>
    /// Why this exists. A local process is closed by the client controller's own "timeline shown" notification;
    /// when that notification never arrives the entry only ends at <see cref="LocalProcessTimeoutSeconds"/>
    /// (sixty seconds), and for that whole window the machine is "in a cutscene": the player stands in it, the
    /// transit interaction of the point is unusable, and the group's countdown is twenty seconds long - so the
    /// client watched while the host transferred without him, which is the report this repairs. The controller
    /// names the process it plays (<c>_cutsceneProcessId</c>), and that is the same fact the notification
    /// carries: once it no longer names ours, nothing of this machine is being viewed any more.
    /// </para>
    /// <para>
    /// The entry has to be seen playing first (<see cref="PendingLocalProcess.SeenPlaying"/>): a process that
    /// was just created has not been handed to the client controller yet, and ending it on that frame would
    /// cancel every scene before its first picture.
    /// </para>
    /// </summary>
    private static bool PlaybackLeftThisProcess(GameWorld world, PendingLocalProcess pending)
    {
        if (pending?.Process == null) return false;
        try
        {
            var client = world == null ? null : world.CutscenesClientController;
            if (client == null) return false;
            if (client._cutsceneProcessId == pending.Process.processId)
            {
                pending.SeenPlaying = true;
                return false;
            }

            return pending.SeenPlaying;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// (2026-10-27 T1) Ends every process this machine created itself, right now, outside the tick.
    /// <para>
    /// The one caller is the moment a transfer of this machine lands (<c>ClientTransitController.HandleClientExtract</c>):
    /// the raid of this machine ends there, and a process that is still open would keep the player inside a
    /// cutscene while the level is replaced - the native end of such a process then runs on a world that is
    /// already going down (the NullReferenceException of the 2026-10-21 20:57 playthrough). Ending it before the
    /// extract also releases the immunity its start granted, so the next map cannot inherit either.
    /// </para>
    /// <para>Idempotent and bounded: it only walks the entries this machine registered itself.</para>
    /// </summary>
    internal static int EndOpenLocalProcesses(string reason)
    {
        if (PendingEnds.Count == 0) return 0;
        var ending = new List<int>();
        foreach (var entry in PendingEnds)
        {
            ending.Add(entry.Key);
        }

        var world = Comfort.Common.Singleton<GameWorld>.Instance;
        for (var i = 0; i < ending.Count; i++)
        {
            EndLocal(world, ending[i], reason);
        }

        return ending.Count;
    }

    /// <summary>
    /// The native end of one process this machine created itself
    /// (<c>EndCutscene(cutsceneId, processId, process, playersId, skip)</c>, interop :94617; skip = false so the
    /// timeline's end actions - the door of the map - still run), then the entry is dropped and the sniper immunity
    /// the start granted is released. Both steps are idempotent, so a native end that also arrives is harmless.
    /// <paramref name="forcedReason"/> names the caller's own end condition in the line of this end (a transfer
    /// that lands), so grep can tell it from the two per-tick conditions.
    /// </summary>
    private static void EndLocal(GameWorld world, int processId, string forcedReason = null)
    {
        if (!PendingEnds.Remove(processId, out var pending)) return;
        LocalProcesses.Remove(processId);
        var reason = forcedReason ?? (pending.Viewed ? "viewed" : "timeout");
        var endStatus = "skipped";
        try
        {
            var server = world == null ? null : world.CutscenesServerController;
            if (server != null && pending.Process != null)
            {
                // (2026-10-21) The seat table is rebuilt as a fresh, non-empty list: the playthrough of 2026-10-21
                // 20:57 shows this call throwing a native NullReferenceException, and the one list it receives
                // from us - the process' own <c>playersInCurrentView</c> - is the field the native completion
                // handler clears. A null list there aborts the native end before its end actions, which is why
                // the client's end of the scene never ran them.
                var viewers = new Il2CppSystem.Collections.Generic.List<int>();
                try
                {
                    var current = pending.Process.playersInCurrentView;
                    if (current != null)
                    {
                        for (var i = 0; i < current.Count; i++) viewers.Add(current[i]);
                    }
                }
                catch (Exception) { }
                if (viewers.Count == 0 && RaidInteractionSync.LocalPlayerId > 0)
                {
                    viewers.Add(RaidInteractionSync.LocalPlayerId);
                }
                server.EndCutscene(pending.Cutscene, processId, pending.Process, viewers, false);
                endStatus = "native";
            }
            else
            {
                endStatus = pending.Process == null ? "no-process" : "no-controller";
            }
        }
        catch (Exception exception)
        {
            // The entry is dropped either way: a process that outlived its end must not keep the immunity. The
            // message is reported because a native end that throws also skips the end actions of the timeline.
            endStatus = "threw:" + exception.GetType().Name + ":" + exception.Message;
        }

        FikaGlobals.LogInfo("[SyncDiag] local-process end pid=" + processId + " reason=" + reason +
            " end=" + endStatus, "FikaLocalCutsceneTrigger");
        // The native end of a process is released by the postfix of EndCutscene; this call keeps the release even
        // when the native call above was skipped or threw.
        RaidInteractionSync.ReleaseLocalSniperImmunity("local-process end pid=" + processId);
    }

    /// <summary>One process of this machine that has not ended yet (see <see cref="PendingEnds"/>).</summary>
    private sealed class PendingLocalProcess
    {
        internal string Cutscene;
        internal CutsceneProcess Process;
        internal float StartedAt;
        internal bool Viewed;
        /// <summary>(2026-10-27 T1) Set once this machine's client controller has named this process as the one it
        /// plays; only then may its disappearance from that field end the entry (see
        /// <see cref="PlaybackLeftThisProcess"/>).</summary>
        internal bool SeenPlaying;
    }
}
