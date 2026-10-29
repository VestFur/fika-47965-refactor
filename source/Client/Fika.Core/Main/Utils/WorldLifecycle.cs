using System;
using Comfort.Common;
using EFT;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.Patches.AI;
using Fika.Core.Main.Patches.BTR;
using Fika.Core.Main.HostClasses;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The one place that answers "is the world this process is holding still the world that is up?".
///
/// Why it exists (round 12, analysis/logs-r12test-20261012-184047): both crashes of that run are native
/// access violations inside UnityPlayer.dll (Windows event 1000, exception code 0xc0000005, fault offsets
/// 0x161f56e at 15:48:18 and 0xe27f07 at 16:45:51), and both land in a teardown window: the first one
/// 4 s after the host logged "world swap #2 noted (reason=DestroyNetManager: transit teardown, host world
/// is down)" (Fika-diagnostics.log, frame=48297), the second one 5 s into the shutdown after a raid start
/// that the engine had cancelled twice. A native AV cannot be caught, so the only way to survive that
/// window is to make sure nothing of ours still touches the world that went down.
///
/// The epoch is the cheap half of that: every object we keep across a frame boundary records the epoch it
/// was created in, and a holder that sees a different epoch knows it is looking at a world that is gone.
/// <see cref="WorldsAlive"/> is false between the teardown of a world and the creation of its successor,
/// which is exactly the window in which Singleton&lt;GameWorld&gt;.Instance can still hand back the disposed
/// world of the raid that just ended.
///
/// The teardown itself is a single, idempotent, exception free sequence: stop the routines the pump would
/// otherwise keep stepping into the dead world, drop the native enumerator the session sequence holds, and
/// clear every static cache that is keyed by - or holds - a native object of that world. Each clear reports
/// how many entries it dropped, so the next run shows in one line whether a cache really carried state
/// across the swap.
///
/// Revert: every call site of this class is a single statement, and the whole behaviour can be switched off
/// with <see cref="Enabled"/> = false (one line, see the report of analysis/transit-crash-unload).
/// </summary>
internal static class WorldLifecycle
{
    /// <summary>Diagnostics scope of the judgement lines of this class (prefix matched, see FikaDiagnostics).</summary>
    private const string Scope = "WorldLifecycle";

    private const string Tag = "[WorldLifecycle]";

    /// <summary>
    /// The one-line kill switch of this round's changes. False makes every method here a no-op, which is
    /// the same as the build before analysis/transit-crash-unload for everything except the [LoadCost] line.
    /// </summary>
    internal static bool Enabled = true;

    private static int _epoch;
    private static bool _worldsAlive = true;
    private static string _downReason = "none";
    private static int _downFrame = -1;
    private static float _downAt;
    private static string _upReason = "none";

    /// <summary>The current world generation. Bumped once per world teardown.</summary>
    internal static int Epoch => _epoch;

    /// <summary>False between the teardown of a world and the creation of the next one.</summary>
    internal static bool WorldsAlive => _worldsAlive;

    /// <summary>True exactly inside that window. Cheap enough for a per-frame caller.</summary>
    internal static bool WorldDown => !_worldsAlive;

    /// <summary>
    /// True when a value captured in <paramref name="epoch"/> belongs to the world generation that is
    /// current now. Deliberately *not* gated on <see cref="WorldsAlive"/>: the objects of the next raid are
    /// constructed inside the swap window (its world controller, its game world components), and they must
    /// count as valid the moment they exist - the window state answers a different question, namely whether
    /// the raid that just ended may still be used, and that one is asked by <see cref="WorldDown"/>.
    /// </summary>
    internal static bool IsCurrent(int epoch)
    {
        return epoch == _epoch;
    }

    /// <summary>
    /// True while the given world object is the world the engine currently hands out. A holder that stored
    /// a world reference across a frame boundary can call this before using it; it is false for the disposed
    /// world of a raid that ended and for a world the engine has already dropped.
    /// </summary>
    internal static bool WorldIsCurrent(World world)
    {
        if (world == null)
        {
            return false;
        }

        try
        {
            return Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance != null
                && Singleton<GameWorld>.Instance.Pointer == world.Pointer;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Records that the world this process was holding is gone. Idempotent: the second call inside the same
    /// window only writes a line, so a teardown that runs through both the world component and the network
    /// manager cannot clear twice.
    /// </summary>
    internal static void MarkWorldTornDown(string reason)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            if (!_worldsAlive)
            {
                Diag("world teardown ignored (reason=" + reason + "): the world of epoch " + _epoch
                    + " is already down since frame " + _downFrame + " (" + _downReason + ")");
                return;
            }

            _epoch++;
            _worldsAlive = false;
            _downReason = reason;
            _downFrame = UnityEngine.Time.frameCount;
            _downAt = UnityEngine.Time.realtimeSinceStartup;

            

            var routines = FikaRoutinePump.Instance.StopWorldBound("the world went down (" + reason + ")");
            var sessionEnumerator = SessionPump.OnWorldTornDown(reason);

            var controllers = FikaWorldControllers.ClearForWorldTeardown();
            var quests = FikaQuestController.ClearForWorldTeardown();
            var magazines = FikaMagazineProcesses.ClearForWorldTeardown();
            var interactions = RaidInteractionSync.ForceResetForWorldTeardown(reason);
            var btr = BTRSide_Patches.ClearForWorldTeardown();
            var firstAid = BotFirstAidPending.ClearForWorldTeardown();
            var surgery = BotSurgeryPending.ClearForWorldTeardown();
            var timeouts = BotFirstAidTimeouts.ClearForWorldTeardown();
            var grenades = FikaGrenadeExplosion.ClearForWorldTeardown();
            var questSessions = ServerAutoStartQuests.ClearForWorldTeardown();
            // The transit controller's co-located-exit suppression and its dropout registry both hold native
            // objects of the world that is going down; the transit side owns the clearing (analysis/
            // transit-abort-rules §5), this is the one authoritative place that calls it.
            var transitExits = TransitCoLocatedExits.ForceResetForWorldTeardown(reason);

            

        }
        catch (Exception exception)
        {
            // A teardown path must not gain a new failure mode: the world is going down either way.
            Warn("the world teardown of '" + reason + "' threw and was cut short: " + exception.Message);
        }
    }

    /// <summary>
    /// Records that a fresh world exists. Clears the "worlds are down" state, closes the [LoadCost] window
    /// and prints how long the swap window lasted.
    /// </summary>
    internal static void MarkWorldUp(string reason)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var wasDown = !_worldsAlive;
            var seconds = wasDown ? UnityEngine.Time.realtimeSinceStartup - _downAt : 0f;
            _worldsAlive = true;
            _upReason = reason;
            var frames = wasDown ? UnityEngine.Time.frameCount - _downFrame : 0;

            

            if (wasDown)
            {
            }
        }
        catch (Exception exception)
        {
            Warn("the world-up record of '" + reason + "' threw: " + exception.Message);
        }
    }

    /// <summary>One line for the report: what the process thinks about the world right now.</summary>
    internal static string Describe()
    {
        return "epoch=" + _epoch + ", worldsAlive=" + _worldsAlive + ", downReason=" + _downReason
            + ", downFrame=" + _downFrame + ", upReason=" + _upReason;
    }

    private static void Diag(string message)
    {
        
        try
        {
            FikaGlobals.LogInfo(Tag + " " + message, "WorldLifecycle");
        }
        catch (Exception)
        {
        }

        try
        {
            
        }
        catch (Exception)
        {
        }
    }

    private static void Warn(string message)
    {
        try
        {
            FikaGlobals.LogWarning(Tag + " " + message, "WorldLifecycle");
        }
        catch (Exception)
        {
        }

        try
        {
            
        }
        catch (Exception)
        {
        }
    }
}
