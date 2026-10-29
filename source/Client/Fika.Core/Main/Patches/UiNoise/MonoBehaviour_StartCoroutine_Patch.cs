using System;
using System.Reflection;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.InteropTypes;
using SPTushonka.Reflection.Patching;
using UnityEngine;

namespace Fika.Core.Main.Patches.UiNoise;

/// <summary>
/// Removes the red "<c>Coroutine couldn't be started because the the game object 'TimerPanel' is inactive!</c>"
/// family without changing what the game does.
///
/// Analysis: analysis/red-errors-coroutines (E2). The 2026-10-10 21:35 raid (BHERKP, Sandbox) wrote that error
/// five times between the host spawn and the local player's death. None of them came from Fika: every Fika drive
/// of that panel is behind <see cref="FikaUiPanels.CanDrive"/> (four lines in LogOutput.log name the skipped
/// sites), a drive that passes the guard cannot produce the error at all (the guard requires an active object),
/// and the one unguarded Fika statement that touches the panel
/// (<c>BaseGameController.cs:524 _abstractGame.GameUi.TimerPanel.ProfileId = profileId</c>) did not run in that
/// raid - the diagnostic journal records the master switch "Skip Countdown Body" as ON, with statement 2/8
/// explicitly skipped. The driving code is therefore the game's own HUD code, which keeps updating the closed
/// panel's timer.
///
/// Unity's own answer to that call is: write one red error, start nothing, return null. This prefix produces the
/// identical outcome - <c>__result = null</c>, the original is skipped - and reports the driver once through
/// <see cref="FikaUiPanels.ReportRefusedEngineStart"/> (component type, coroutine type, game object name). The
/// routine type is the part Unity's message withholds: for an iterator method the runtime names its state
/// machine after the method (<c>EFT.UI.ExtractionTimersPanel+&lt;Co_MovePivot&gt;d__NN</c>), so the next raid
/// names the game method that drove the closed panel instead of only counting the errors.
///
/// Why the engine method and not the panel: skipping a whole call of <c>ExtractionTimersPanel.SetTime</c> would
/// also skip its non-UI bookkeeping (profile binding, stored times), while suppressing only the coroutine start
/// reproduces exactly the vanilla outcome of an inactive object. Every coroutine the game starts elsewhere is
/// untouched: an active object returns from the prefix on the first check.
///
/// The guard is exception safe: an unexpected shape degrades to one warning line and the vanilla call runs
/// unchanged.
/// </summary>
internal sealed class MonoBehaviour_StartCoroutine_Patch : ModulePatch
{
    private static bool _guardFailed;

    protected override MethodBase GetTargetMethod()
    {
        // Only the IEnumerator overload can animate a panel; the two string overloads cannot produce this error.
        foreach (var method in typeof(MonoBehaviour).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != nameof(MonoBehaviour.StartCoroutine))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Il2CppSystem.Collections.IEnumerator))
            {
                return method;
            }
        }

        throw new MissingMethodException("UnityEngine.MonoBehaviour.StartCoroutine(" +
            typeof(Il2CppSystem.Collections.IEnumerator).FullName +
            ") does not exist in this build; the inactive-panel coroutine guard cannot be installed.");
    }

    [PatchPrefix]
    private static bool Prefix(MonoBehaviour __instance, Il2CppSystem.Collections.IEnumerator routine, ref Coroutine __result)
    {
        try
        {
            

            if (__instance == null)
            {
                return true;
            }

            var owner = __instance.gameObject;
            if (owner == null || owner.activeInHierarchy)
            {
                return true;
            }

            // The engine cannot start a coroutine on an inactive object: it writes the red error and returns
            // null. Return the same null and let the report name the caller.
            __result = null;
            FikaUiPanels.ReportRefusedEngineStart(__instance, routine);
            return false;
        }
        catch (Exception exception)
        {
            if (!_guardFailed)
            {
                _guardFailed = true;
                
            }

            return true;
        }
    }
}
