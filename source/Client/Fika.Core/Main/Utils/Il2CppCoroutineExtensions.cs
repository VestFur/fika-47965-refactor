using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace Fika.Core;

/// <summary>
/// The one place Fika still hands the engine a managed enumerator, and the place that names it in the
/// diagnostics file.
///
/// This is deliberately the only remaining hand-over shape: the engine holds an
/// <c>BepInEx.Unity.IL2CPP.Utils.Collections.Il2CppManagedEnumerator</c> for the whole life of the coroutine and
/// releases it afterwards, and that release path is where the engine dereferenced the bogus pointer 0xA
/// (analysis/panel-crash-round7 for the menu panel, analysis/host-raid-coroutine for the host raid start).
/// Every site that ran during the raid start is now driven by <see cref="FikaRoutinePump"/> instead; the sites
/// that are left here have run in verified sessions without a fault, and each start writes one line naming the
/// site, the managed routine and both pointers, so the next fault can be attributed instead of guessed.
///
/// Round 2026-10-10 21:35 (analysis/red-errors-coroutines, E1): the hand-over is now wrapped in
/// <see cref="EngineRoutineBridge"/>, which translates every yielded value on the way out. A routine driven
/// from here used to hand the engine a managed <c>WaitSeconds</c> and lost the wait, because
/// <c>Il2CppManagedEnumerator.Current</c> can only forward an il2cpp object. The engine never receives a managed
/// value now, so that failure mode is closed for every site below at once.
/// </summary>
/// <remarks>
/// The class is public so that the other Fika assembly (Fika.Headless, whose raid start hands the engine
/// <c>CreateStashes()</c> and <c>SyncTraps()</c>) can be routed through the same boundary instead of calling
/// <c>WrapToIl2Cpp()</c> directly - those two routines yield <c>WaitSeconds</c> and reproduced exactly the
/// failure this class closes. See analysis/red-errors-coroutines, E1, "invariant completeness".
/// </remarks>
public static class EngineCoroutines
{
    /// <summary>Starts logged per site and session; a per-frame site must not flood the diagnostics file.</summary>
    private const int ReportsPerSite = 4;

    /// <summary>
    /// Steps allowed in one engine call. Unity steps nested routines until a real wait in the same frame; the
    /// cap stops a routine that loops on completed tasks from stalling the frame (see EngineRoutineBridge).
    /// </summary>
    internal const int MaxStepsPerTrigger = 128;

    private static readonly Dictionary<string, int> _startsPerSite = [];

    /// <summary>Conversions, refusals and step limits reported so far, one entry per site and kind.</summary>
    private static readonly HashSet<string> _reportedKinds = [];

    /// <summary>Upper bound of distinct report keys; enough to name every site a raid reaches.</summary>
    private const int MaxReportedKinds = 128;

    /// <summary>
    /// Wraps a managed routine for the engine and writes the line that names it. Use this instead of
    /// <c>routine.WrapToIl2Cpp()</c> so every engine driven managed coroutine is attributable and value safe.
    /// </summary>
    public static Il2CppSystem.Collections.IEnumerator Wrap(IEnumerator routine, string site, string host = null)
    {
        var bridge = Bridge(routine, site);
        var wrapped = bridge.WrapToIl2Cpp();
        var enumerator = wrapped is Il2CppObjectBase wrappedObject
            ? NativeRefGuard.Describe(wrappedObject)
            : (routine is null ? "null" : routine.GetType().FullName);

        Report(site, routine, host, enumerator);
        return wrapped;
    }

    /// <summary>
    /// The bridge itself, unwrapped. The value boundary needs it for a nested routine (see
    /// <see cref="EngineRoutineBridge"/>), which must be bridged before the engine can step into it.
    /// </summary>
    internal static EngineRoutineBridge Bridge(IEnumerator routine, string site)
    {
        return new EngineRoutineBridge(routine, site);
    }

    private static void Report(string site, IEnumerator routine, string host, string enumerator)
    {
        var key = string.IsNullOrEmpty(site) ? "unknown" : site;
        _startsPerSite.TryGetValue(key, out var count);
        _startsPerSite[key] = count + 1;
        if (count > ReportsPerSite)
        {
            return;
        }

        if (count == ReportsPerSite)
        {
            
            return;
        }

        
    }

    /// <summary>
    /// A managed wait was translated for the engine. One line per site: the site is the evidence that the
    /// boundary is exercised, the value is the evidence that the translation kept the routine's timing.
    /// </summary>
    internal static void ReportConversion(string site, string detail)
    {
        Report(site, "converted", detail);
    }

    /// <summary>The engine wait object could not be built, so the wait is measured against Time.time instead.</summary>
    internal static void ReportConversionFallback(string site, float seconds, Exception exception)
    {
        Report(site, "conversion-fallback", "the engine WaitForSeconds for " + seconds +
            " s could not be built (" + exception.GetType().Name + ": " + exception.Message +
            "); the wait is kept, measured against Time.time");
    }

    /// <summary>
    /// A value with no engine counterpart was yielded by an engine driven routine. It is not handed over (the
    /// engine's conversion of it raises), so the routine waits one frame instead and the line names both.
    /// </summary>
    internal static void ReportUnsupportedYield(string site, object yielded)
    {
        Report(site, "unsupported-yield:" + (yielded?.GetType().FullName ?? "null"),
            "an engine driven routine yielded " + (yielded?.GetType().FullName ?? "null") +
            ", which has no il2cpp counterpart; it waits one frame instead of losing the step");
    }

    /// <summary>A routine ran into the per-call step cap; the rest of it continues on the next frame.</summary>
    internal static void ReportStepLimit(string site)
    {
        Report(site, "step-limit", "the routine reached the per-call step cap (" + MaxStepsPerTrigger +
            "); it continues on the next frame");
    }

    private static void Report(string site, string kind, string detail)
    {
        try
        {
            if (_reportedKinds.Count >= MaxReportedKinds || !_reportedKinds.Add(site + " | " + kind))
            {
                return;
            }

            
        }
        catch (Exception)
        {
            // A report must never break the coroutine it reports about.
        }
    }
}

internal static class Il2CppCoroutineExtensions
{
    public static Coroutine StartCoroutine(this MonoBehaviour host, IEnumerator routine, [CallerMemberName] string site = null)
    {
        // The engine drives this routine from here on; see EngineCoroutines for the one line that names it and
        // EngineRoutineBridge for the values it may yield.
        var hostDescription = NativeRefGuard.Describe(host);
        var wrapped = EngineCoroutines.Wrap(routine, site, hostDescription);
        return host.StartCoroutine(wrapped);
    }
}
