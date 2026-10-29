using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Drive guards for the game HUD panels Fika touches.
///
/// The game panels start their own UI coroutines (slide-in, colour blend, countdown) from inside
/// the very methods callers invoke - for example <c>EFT.UI.ExtractionTimersPanel</c> owns
/// _panelCoroutine/_timerCoroutine/_timerColorCoroutine and the matching Co_PullUpPanel,
/// Co_MovePivot and ChangeColor enumerators, and <c>EFT.UI.BattleUITimerPanel</c> owns
/// Co_Countdown through ShowPanel/ShowCountdownPanel. Unity refuses to start a coroutine on a
/// component whose GameObject is inactive and writes one error per attempt:
///
/// [Error : Unity] Coroutine couldn't be started because the the game object 'TimerPanel' is inactive!
///
/// Closing a panel (extract, death, raid stop) leaves the component alive but its GameObject
/// inactive, so a later drive from our side can only produce that error - the panel is not visible
/// and cannot animate. Skipping the call keeps the functional outcome (nothing was shown before
/// either) and removes the noise; the first skip per site is still reported once at Info level so
/// the evidence for the decision is not lost.
/// </summary>
internal static class FikaUiPanels
{
    /// <summary>Sites already reported; one line per site and session, never one per frame.</summary>
    private static readonly HashSet<string> Reported = [];

    /// <summary>
    /// True when <paramref name="panel"/> is a usable il2cpp reference whose GameObject is active,
    /// i.e. when the game may legally start the panel coroutines for it.
    /// </summary>
    /// <param name="panel">The game panel that would be driven.</param>
    /// <param name="site">Stable caller name used for the one-time report.</param>
    internal static bool CanDrive(Component panel, string site, string detail = null)
    {
        if (panel is null || !NativeRefGuard.IsUsable(panel))
        {
            Report(site, "the panel reference is null or not a usable il2cpp object" + Suffix(detail));
            return false;
        }

        try
        {
            var panelObject = panel.gameObject;
            if (panelObject is null || !panelObject.activeInHierarchy)
            {
                Report(site, "the panel object is inactive" + Suffix(detail));
                return false;
            }
        }
        catch (Exception exception)
        {
            Report(site, "reading the panel object failed (" + exception.GetType().Name + ": " + exception.Message + ")");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Brings a panel of this game online and answers whether the engine may now start that panel's
    /// coroutines on it.
    ///
    /// Why this exists (analysis/terminal-transit-mechanism): <c>SetActive(true)</c> on the arcade UI
    /// root is the only statement of the game that switches the in-raid HUD on, and this port skips it
    /// (the 'Skip Countdown Body' master switch) and restores it later from
    /// <c>BaseGameController.RestoreInRaidHudRoutine</c>. Every HUD panel that is *not* covered by that
    /// restore - <c>EFT.UI.LocationTransitTimerPanel</c>, whose GameObject is 'LocationTransitTimerPanel'
    /// and which shows the transit countdown - therefore stays inactive. Its native applier ends in
    /// <c>Display()</c> + <c>Show(seconds)</c>, and both methods start the panel's own coroutines, so the
    /// countdown is refused exactly like the extraction panel's drive is: the gate is passed, the
    /// coroutine is refused, and the machine that asked for the countdown shows nothing. The
    /// 2026-10-13 rounds prove the shape for the sibling panel
    /// (<c>[PanelGuard] skipped ClientGameController.Spawn.TimerPanel.SetTime because the panel object
    /// is inactive</c> and <c>raw: [PanelGuard] refused a coroutine start on the inactive game object
    /// 'TimerPanel': host=EFT.UI.ExtractionTimersPanel</c>).
    ///
    /// The call is deliberately limited: only the panel's own GameObject is switched on, and only while
    /// its ancestors (the HUD root, the canvas it lives on) are active - switching a child on under an
    /// inactive parent changes nothing in Unity, so that case is reported instead of forced. A UI element
    /// that is inactive exactly because the HUD hierarchy never came up is the case this exists for.
    /// </summary>
    /// <param name="panel">The game panel that should carry a countdown.</param>
    /// <param name="site">Stable caller name used for the one-time report.</param>
    /// <returns>True when the panel may be driven right now.</returns>
    internal static bool TryActivateForDrive(Component panel, string site)
    {
        if (panel is null || !NativeRefGuard.IsUsable(panel))
        {
            Report(site, "the panel reference is null or not a usable il2cpp object");
            return false;
        }

        try
        {
            var panelObject = panel.gameObject;
            if (panelObject is null)
            {
                Report(site, "the panel has no game object");
                return false;
            }

            if (panelObject.activeInHierarchy)
            {
                return true;
            }

            if (panelObject.activeSelf)
            {
                // The element is on, so the reason is above it: a disabled canvas or HUD root. Nothing here
                // activates an ancestor - that would fight whatever switched it off.
                Report(site, "the panel is inactive inside an inert HUD hierarchy (" + DescribeChain(panelObject) +
                    "); the panel is not forced on");
                return false;
            }

            panelObject.SetActive(true);
            Report(site, "the panel was switched on (it was inactive, and this port's countdown drive must not " +
                "run on an object the engine refuses to start coroutines on); chain now " + DescribeChain(panelObject));

            if (!panelObject.activeInHierarchy)
            {
                Report(site, "the panel was switched on but its hierarchy is still inert (" + DescribeChain(panelObject) +
                    "); the panel is not driven");
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            Report(site, "reading or activating the panel failed (" + exception.GetType().Name + ": " + exception.Message + ")");
            return false;
        }
    }

    /// <summary>
    /// The activation chain of an object, from the object itself up to the scene root: <c>name(self)/name(activeInHierarchy) &lt;- ...</c>.
    /// This is what names *where* an inactive panel sits - the fact the [PanelGuard] line alone cannot show.
    /// Read-only, never throws, and bounded so one odd hierarchy cannot spin here.
    /// </summary>
    internal static string DescribeChain(Component panel, string fallback = "unreadable")
    {
        try
        {
            var owner = panel?.gameObject;
            return owner is null ? fallback : DescribeChain(owner);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>Activation chain of a GameObject, see <see cref="DescribeChain(Component, string)"/>.</summary>
    internal static string DescribeChain(GameObject start)
    {
        try
        {
            var builder = new System.Text.StringBuilder();
            var current = start;
            for (var hops = 0; current != null && hops < 8; hops++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(" <- ");
                }

                builder.Append(ReadName(current)).Append('(').Append(ReadActive(current)).Append(')');
                var transform = current.transform;
                current = transform == null ? null : transform.parent?.gameObject;
            }

            return builder.Length == 0 ? "unreadable" : builder.ToString();
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    private static string ReadName(GameObject target)
    {
        try
        {
            var name = target.name;
            return string.IsNullOrEmpty(name) ? "?" : name;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string ReadActive(GameObject target)
    {
        try
        {
            var self = target.activeSelf;
            var inHierarchy = target.activeInHierarchy;
            if (self && !inHierarchy)
            {
                return "self-active, hierarchy-inactive";
            }

            return self ? "active" : "inactive";
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    private static string Suffix(string detail) => string.IsNullOrEmpty(detail) ? string.Empty : "; " + detail;

    /// <summary>Drivers already reported by <see cref="ReportRefusedEngineStart"/>, one line each.</summary>
    private static readonly HashSet<string> ReportedRefusals = [];

    /// <summary>Upper bound of the refusal report; more distinct drivers than this stay silent.</summary>
    private const int MaxReportedRefusals = 64;

    /// <summary>
    /// Called by <c>MonoBehaviour_StartCoroutine_Patch</c> for a start the engine itself would refuse: the
    /// component's GameObject is inactive, so Unity writes
    /// <c>Coroutine couldn't be started because the the game object 'X' is inactive!</c> and starts nothing.
    /// The patch starts nothing as well - the caller still receives null, exactly as before - and this line
    /// names the driver (component type, coroutine type, object) once, because that red line names only the
    /// object and the 2026-10-10 21:35 raid (BHERKP) left five of them unattributed.
    /// </summary>
    internal static void ReportRefusedEngineStart(Component host, object routine)
    {
        try
        {
            if (host is null)
            {
                return;
            }

            string hostName = NativeRefGuard.TryReadTypeName(host.Pointer) ?? host.GetType().FullName;
            string routineName = DescribeCoroutine(routine);

            if (ReportedRefusals.Count >= MaxReportedRefusals ||
                !ReportedRefusals.Add(hostName + " | " + routineName))
            {
                return;
            }

            string objectName = "unreadable";
            try
            {
                objectName = host.gameObject?.name ?? "null";
            }
            catch (Exception)
            {
            }

            FikaGlobals.LogInfo("[PanelGuard] refused a coroutine start on the inactive game object '" + objectName +
                "': host=" + hostName + " routine=" + routineName +
                "; chain " + DescribeChain(host, "unreadable") +
                "; nothing was started, exactly as the engine's own refusal would have it");
        }
        catch (Exception)
        {
            // A report must never break the call it reports about.
        }
    }

    /// <summary>
    /// Names a coroutine handed to the engine. The il2cpp class name is preferred - the runtime names an
    /// iterator's state machine after its method (<c>EFT.UI.ExtractionTimersPanel+&lt;Co_MovePivot&gt;d__NN</c>),
    /// which is the part Unity's own error message withholds - and the managed type is the fallback for a
    /// value whose reference cannot be read.
    /// </summary>
    private static string DescribeCoroutine(object routine)
    {
        if (routine is null)
        {
            return "null";
        }

        try
        {
            if (routine is Il2CppObjectBase native)
            {
                string name = NativeRefGuard.TryReadTypeName(native.Pointer);
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
        }
        catch (Exception)
        {
        }

        try
        {
            return routine.GetType().FullName ?? routine.GetType().Name;
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    /// <summary>
    /// Names a panel for a journal line: its GameObject name when it can be read, <paramref name="fallback"/>
    /// when the reference is null or its name cannot be read. Never throws - it is used while building a
    /// diagnostic line (analysis/inraid-ui).
    /// </summary>
    internal static string DescribeObject(Component panel, string fallback)
    {
        if (panel is null)
        {
            return fallback;
        }

        try
        {
            var owner = panel.gameObject;
            if (owner is null)
            {
                return fallback;
            }

            var name = owner.name;
            return string.IsNullOrEmpty(name) ? fallback : "'" + name + "'";
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void Report(string site, string detail)
    {
        if (!Reported.Add(site))
        {
            return;
        }

        FikaGlobals.LogInfo("[PanelGuard] skipped " + site + " because " + detail +
            "; the game starts this panel's coroutines from inside the call, which Unity refuses on an inactive object");
    }
}
