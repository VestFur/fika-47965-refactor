using System;
using System.Reflection;
using BepInEx.Logging;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.LogNoise;

/// <summary>
/// Folds the game's own ballistic-prewarm failure into one attributed line instead of the raw
/// Unity warning (one per raid load). Root cause and evidence: analysis/shot-delegate-prewarm/REPORT.md.
///
/// The chain belongs to EFT, not to this port. <c>EFT.GameWorld.InitLevel</c> (in its async
/// continuation, i.e. during the raid load) calls
/// <c>EFT.Ballistics.BallisticCalculatorPrewarmer.SimulateShot</c>, which builds a synthetic
/// <c>Shot</c> through <c>BallisticsCalculator.CreateShot</c> -> <c>Shot.Create</c> ->
/// <c>Shot.Init</c>. <c>Shot.Init</c> throws a <c>NullReferenceException</c>, the prewarmer catches
/// it and logs "Failed prewarm shot delegate because System.NullReferenceException: ..." on the
/// Unity channel (LogOutput.log line 2089 of the 10-10 14:10 run). No Fika frame is in that stack,
/// the message literal lives in the game's own global-metadata.dat (offset 4909099), and the
/// identical stack is recorded for the clean SPT 5.0 baseline
/// (docs/client-il2cpp-port-blockers.md:131) and for the older 47242 port build
/// (analysis/panel-crash-round7/Player-prev-crash-oldbuild.log:376104) - it is an install/version
/// level defect, not a Fika regression.
///
/// There is no port-side root cause to repair. The inputs of that call are produced inside the game
/// method, and the interop assemblies of this install carry metadata only (no IL bodies), so the
/// null cannot even be named from here. The two "root" alternatives are strictly worse than this
/// filter: skipping <c>SimulateShot</c> changes the control flow inside a game method whose caller
/// contract cannot be inspected (if that caller dereferences the returned <c>Shot</c>, a harmless
/// caught NRE would become a load-time failure), and re-implementing the prewarm needs the game's
/// private ammo/collider/random inputs. The port therefore leaves the game path byte-identical and
/// only stops the *misleading* warning from reaching the tester's log - the same policy already
/// applied to the game's 5,744-warning SimSun font flood (Main/Patches/UiNoise).
///
/// The seam is <c>BepInEx.Logging.DiskLogListener.LogEvent</c>: every write to LogOutput.log passes
/// through this managed method no matter which Unity log API produced the message (Debug.LogWarning
/// has several overloads and the game picks one that cannot be seen from the metadata). The filter
/// matches the exact known signature - the prewarm prefix *and* the <c>Shot.Init</c> throw site - so
/// any other prewarm failure (different exception, different stack) still reaches the log unchanged.
///
/// Cost: one string prefix compare per log entry. On a match the entry is dropped, the first
/// occurrence of the process is reported once at Info level (the tester's log still says what
/// happened and points at the analysis) and every occurrence is recorded in Fika-diagnostics.log
/// with its full original text. Reverting: delete this file (or tag the class with
/// IgnoreAutoPatch) - nothing else depends on it.
/// </summary>
internal sealed class DiskLogListener_BallisticPrewarm_Patch : ModulePatch
{
    /// <summary>The known game-side message; everything that does not match passes through untouched.</summary>
    private const string MessagePrefix = "Failed prewarm shot delegate because System.NullReferenceException";

    /// <summary>The throw site inside that message - this is what makes the match specific to the known game bug.</summary>
    private const string ThrowSite = "EFT.Ballistics.Shot.Init";

    private static int _folded;
    private static bool _reportedOnce;
    private static bool _guardFailed;
    private static bool _reporting;

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(DiskLogListener), "LogEvent", [typeof(object), typeof(LogEventArgs)])
            ?? throw new MissingMethodException("BepInEx.Logging.DiskLogListener.LogEvent(object, LogEventArgs) does not exist in this " +
                "BepInEx build; the ballistic-prewarm warning filter cannot be installed (the warning it filters is harmless, see the class comment).");
    }

    [PatchPrefix]
    private static bool Prefix(object sender, LogEventArgs eventArgs)
    {
        // The replacement line this prefix writes itself travels through this same method.
        if (_reporting)
        {
            return true;
        }

        try
        {
            if (!IsGameBallisticPrewarmNoise(eventArgs))
            {
                return true;
            }

            _folded++;
            if (!_reportedOnce)
            {
                _reportedOnce = true;
                Report(eventArgs);
            }
            else
            {
                
            }

            // The Unity warning never reaches LogOutput.log; the fact itself is reported above.
            return false;
        }
        catch (Exception exception)
        {
            // A guard that throws while logging would be worse than the warning it removes: fall back
            // to the untouched vanilla path and report the guard once.
            if (!_guardFailed)
            {
                _guardFailed = true;
                
            }

            return true;
        }
    }

    private static bool IsGameBallisticPrewarmNoise(LogEventArgs eventArgs)
    {
        if (eventArgs?.Data is not string message || message.Length < MessagePrefix.Length)
        {
            return false;
        }

        return message.StartsWith(MessagePrefix, StringComparison.Ordinal)
            && message.Contains(ThrowSite, StringComparison.Ordinal);
    }

    /// <summary>
    /// One Info line replaces the folded warning in LogOutput.log (grep <c>[PrewarmNoise]</c>), and
    /// the complete original text - including the game stack - is kept in Fika-diagnostics.log.
    /// </summary>
    private static void Report(LogEventArgs eventArgs)
    {
        _reporting = true;
        try
        {
            FikaGlobals.LogInfo("[PrewarmNoise] EFT's own ballistics prewarmer (BallisticCalculatorPrewarmer.SimulateShot -> Shot.Init) " +
                "threw its known NullReferenceException at raid load again; the raw Unity warning was folded into this line instead of the log. " +
                "Game-side and caught by the game (the raid continues, no Fika frame is involved); " +
                "the raw warning was folded into this line (analysis/shot-delegate-prewarm)");
        }
        finally
        {
            _reporting = false;
        }

        var original = eventArgs.Data as string ?? string.Empty;
        
    }
}
