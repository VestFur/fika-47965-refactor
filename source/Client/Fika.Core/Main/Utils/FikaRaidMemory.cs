using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Fika.Core.Networking;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The memory side of a transit reload, in one place.
///
/// Why this exists (analysis/transit-reload-oom, client Fika-diagnostics.log frame=48430 /
/// LogOutput.log:4880): the client reloads the next map inside the same process. The old world is
/// torn down, but nothing ever releases the assets that world had loaded. The client never called
/// UnityEngine.Resources.UnloadUnusedAssets() anywhere - the only call sites in the whole product
/// source are the headless ones (FikaHeadlessPlugin.cs:531, HeadlessGame.cs:350) and the headless
/// deliberately skips them for a transit (FikaHeadlessPlugin.cs:529) - and the game's own memory
/// management only reacts once the next raid is already up (LogOutput.log:4872 "EmptyWorkingSet",
/// 4873 "Can't disable GC due to low memory size 15.24219GB, requested 25GB", 4880 the failed
/// 20 MB TempOverflow allocation). The second map is therefore loaded on top of the first one, and
/// the process dies exactly when the characters of the second raid are spawned.
///
/// The release runs at the one boundary where it is both possible and useful: the new world object
/// exists but its level (bundles, pools, scenes) has not been loaded yet, i.e. directly before the
/// native ClientLocalGameWorld.InitLevel call. At that moment the previous world is gone, so
/// everything it held is unreachable and the asset pass can free it; and the new map is still
/// unloaded, so nothing that this pass could free is needed by the raid being started.
///
/// The [MemDiag] lines make the next run readable: one line per boundary with the managed heap, the
/// process working set and private bytes and, where the engine exposes them, the Unity allocator
/// totals. A second transit run then answers "the old map was never released" (the numbers drop
/// only by the release, and the new map starts from a high base) versus "the new map alone is too
/// big" (the base is low and the raid still runs out).
/// </summary>
internal static class FikaRaidMemory
{
    /// <summary>
    /// Diagnostics scope for the [MemDiag] lines. The prefix is on <c>FikaDiagnostics.QuietSteps</c>,
    /// so these lines survive the quiet diagnostics mode exactly like the [LoadSync] ones do.
    /// </summary>
    private const string DiagScope = "LoadingScreen.MemDiag";


    /// <summary>
    /// Frame bound of the asset pass. It needs a handful of frames; the bound only exists so that a
    /// pass which never reports <c>isDone</c> cannot hold the raid start forever.
    /// </summary>
    private const int MaxUnloadFrames = 300;

    private static bool _worldSwapPending;
    private static int _worldSwaps;

    /// <summary>True between the teardown of a world and the level load of its replacement.</summary>
    internal static bool WorldSwapPending => _worldSwapPending;

    /// <summary>
    /// Records that the current world has been torn down and the next one is being created. Called from
    /// the transit branch of <see cref="NetManagerUtils.DestroyNetManager"/> for BOTH roles (the client
    /// branch arms the world packet queue there; the host branch keeps the server alive there), so both
    /// facts are read from the same log position.
    ///
    /// Gated by <c>FikaBackendUtils.IsTransit</c> on purpose: the release below must only ever run for a
    /// transit that is really continuing. Outside that window the assets of the raid that just ended
    /// belong to the next raid's own loading path (and the process is about to return to the menu, which
    /// releases them itself), so an ordinary raid start is never touched.
    /// </summary>
    internal static void NoteWorldSwap(string reason)
    {
        if (!FikaBackendUtils.IsTransit)
        {
            return;
        }

        _worldSwapPending = true;
        _worldSwaps++;
    }

    /// <summary>
    /// Releases the previous raid's assets, once, at the given boundary. A no-op when no world swap was
    /// noted (the ordinary raid start of a process pays nothing for this), and a no-op when the swap is
    /// no longer a transit by the time the boundary is reached.
    ///
    /// The returned task NEVER faults and never runs the release twice: every failure path inside logs one
    /// WARN line and continues. A release that fails must not stop the raid that is being loaded - this is
    /// awaited in front of the native level load on both roles, the host included.
    /// </summary>
    internal static Task ReleaseWorldSwapAsync(string boundary)
    {
        if (!_worldSwapPending)
        {
            return Task.CompletedTask;
        }

        var wasTransit = FikaBackendUtils.IsTransit;
        _worldSwapPending = false;
        if (!wasTransit)
        {
            return Task.CompletedTask;
        }

        try
        {
            return MainThreadDispatcher.RunAsync(async () =>
            {
                try
                {
                    var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    var managedBefore = ManagedHeap();
                    var frames = await UnloadUnusedAssetsAsync();
                    var collect = ForceCollect();
                    var managedAfter = ManagedHeap();
                    var cost = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                catch (Exception exception)
                {
                    FikaGlobals.LogWarning("FikaRaidMemory: the swap release at '" + boundary
                        + "' failed, the load continues regardless: " + exception);
                }
            });
        }
        catch (Exception exception)
        {
            // Belt and braces: the dispatcher itself must not be able to fail the raid that is loading.
            FikaGlobals.LogWarning("FikaRaidMemory: the swap release at '" + boundary
                + "' could not be scheduled, the load continues regardless: " + exception);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// One Unity asset pass, driven to completion on the pumping thread. Everything the old world held
    /// is unreachable at the call site, so this is the call that turns the old map's textures, meshes and
    /// audio (Unity's ALLOC_CACHEOBJECTS, 3.89 GB / 4.18 GB reserved in the reported crash) back into
    /// free memory. Objects that are still referenced - the loading screen, the menu, the network manager,
    /// every prefab a live object points at - are deliberately kept by the engine.
    /// </summary>
    private static async Task<int> UnloadUnusedAssetsAsync()
    {
        var frames = 0;
        try
        {
            var unload = UnityEngine.Resources.UnloadUnusedAssets();
            while (unload != null && !unload.isDone && frames < MaxUnloadFrames)
            {
                frames++;
                await Task.Yield();
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning("FikaRaidMemory: the asset pass failed: " + exception.Message);
        }

        return frames;
    }

    /// <summary>
    /// One forced, blocking, compacting managed collection. A transit carries a whole raid of managed
    /// state (profiles, items, packets, the item factory caches) into the next one; without this the
    /// old raid's garbage is still on the heap while the new raid is being built. Runs on the loading
    /// screen, so the one-time pause costs nothing that the player can see.
    /// </summary>
    /// <summary>
    /// One-click switch for the expensive form of the release (analysis/transit-crash-unload).
    ///
    /// Measured in the round-12 log, the release boundary of the first transit of the crash session
    /// (Fika-diagnostics.log, frames 31414 -> 31415, "before the release" 330055 ms -> "after the release"
    /// 330571 ms): the asset pass finished in one frame and the whole 516 ms of that window was the two
    /// blocking *compacting* collections with a finalizer wait - while the pass itself freed 2.6 MiB of
    /// Unity memory and the collections freed 25 MiB of managed garbage. 516 ms of frozen main thread is
    /// exactly the "the next map loads slowly" complaint, so the default is now one blocking, non-compacting
    /// collection (which still drops the old raid's garbage) and the compaction is opt in.
    /// </summary>
    private static readonly bool CompactAfterUnload = false;

    /// <summary>
    /// The managed collection of the release. One blocking collection of generation 2 without compaction by
    /// default; with <see cref="CompactAfterUnload"/> set it is the previous pair, compaction included.
    /// Never throws.
    /// </summary>
    private static string ForceCollect()
    {
        try
        {
            if (CompactAfterUnload)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                return "forced+compacting";
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            return "forced";
        }
        catch (Exception exception)
        {
            return "failed (" + exception.GetType().Name + ")";
        }
    }

    /// <summary>MEMORYSTATUSEX, filled by <see cref="GlobalMemoryStatusEx"/>.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    private static long ManagedHeap()
    {
        try
        {
            return GC.GetTotalMemory(false);
        }
        catch (Exception)
        {
            return -1L;
        }
    }

    // ------------------------------------------------------------------ commit watch
    // (AB: commit-watch) Why this exists: the 2026-10-12 19:59:56 death was a CLR fail fast
    // (0x80131506, coreclr.dll) and the last two [MemDiag] lines before it read
    // sysCommitAvail=820.7MiB / 883.9MiB while this process held committed=12.8GB - the commit of the
    // whole machine was gone, not this process's heap. The page file is a user-side setting
    // (Win32_PageFileUsage reports only C: 15608 MB while the registry configures d:/pagefile.sys), so
    // the only two things this plugin can do about it are: say it out loud before the runtime does, and
    // give back what it is allowed to give back.
    //
    // The watch is driven from LoadCostMeter.Tick, i.e. from the plugin Update on every frame, whether or
    // not a world window is open - the death above happened inside a live raid, not in a swap window.
    // Outside a warning it costs one bool test, one timestamp compare and (once per second) one call into
    // kernel32!GlobalMemoryStatusEx, which allocates nothing.
    //
    // Levels (see LevelFor):
    //   normal   sysCommitAvail above WarnMiB (default 1536 MiB)
    //   WARN     at or below WarnMiB: one [MemWatch] line at most every 5 s, carrying every number the
    //            diagnosis needs (this process's committed / unityAllocated, and our queues)
    //   CRITICAL at or below CriticalMiB (default 1024 MiB): the same line tagged CRITICAL, plus the relief
    // The WARN band is only left above WarnMiB + 512 MiB, so a 1.5 GiB band cannot turn the 5 s rate limit
    // into one line per second.
    //
    // The relief (own switch, arms only below CRITICAL) is the "give back" half: it pauses our own
    // low-priority work and, at most once per 30 s, runs the release the transit path already proved safe.
    // Every item it gives up is pure observation or reconstructible; nothing that carries inventory or
    // synchronisation state is ever dropped. The list, its reasons and the one-click revert are in
    // analysis/commit-watch/REPORT.md.

    /// <summary>How often the commit is read. One read per second is free and enough for a curve.</summary>
    private const long ProbeMillis = 1000L;

    /// <summary>Hard rate limit of the warning lines: the brief's "at most one line per 5 s".</summary>
    private const long WatchReportMillis = 5000L;

    /// <summary>Shortest distance between two releases while the machine stays below CRITICAL.</summary>
    private const long ReleaseCooldownMillis = 30000L;

    /// <summary>Distance above the warn threshold at which the band is left again (hysteresis).</summary>
    private const long ReleaseHysteresisMiB = 512L;

    private static long _nextProbeAt;
    private static int _level = -1;
    private static long _lastWatchAt;
    /// <summary>Uptime of the last release. Zero (not long.MinValue) so the first cooldown cannot overflow.</summary>
    private static long _lastReleaseAt;
    private static bool _reliefActive;
    private static long _reliefArmedAt;
    private static int _reliefArmedFrame = -1;
    private static bool _releaseInFlight;
    private static long _releases;
    private static long _reliefSkips;
    private static long _everLow;

    private static bool _windowBaselineValid;
    private static long _baselineCommitted;
    private static long _baselineUnity;
    private static long _baselineManaged;
    private static long _baselineAvailable;
    private static string _baselineReason = "none";

}
