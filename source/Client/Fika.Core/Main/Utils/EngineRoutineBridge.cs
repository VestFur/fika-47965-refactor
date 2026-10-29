using System;
using System.Collections;
using System.Threading.Tasks;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The value boundary of every managed routine Fika hands to the engine.
///
/// Analysis: analysis/red-errors-coroutines (E1). The port expresses its timed waits with the managed
/// <see cref="WaitSeconds"/> because reading the engine's <c>WaitForSeconds.m_Seconds</c> from managed code is
/// what ended the 2026-10-10 host raids. That rule belongs to the *pump*. A routine the engine drives
/// (<c>MonoBehaviour.StartCoroutine</c> through <see cref="EngineCoroutines.Wrap"/>) hands every yielded value
/// to <c>Il2CppManagedEnumerator.Current</c>, which can only forward an il2cpp object. On 2026-10-10 21:35
/// (raid BHERKP, frame 15998) one of them arrived there:
///
/// <code>
/// System.NotSupportedException: Type Fika.Core.Main.Utils.WaitSeconds cannot be converted directly to an Il2Cpp object
///   at BepInEx.Unity.IL2CPP.Utils.Collections.Il2CppManagedEnumerator.get_Current()
///   at UnityEngine.MonoBehaviour.StartCoroutine
///   at Fika.Core.Il2CppCoroutineExtensions.StartCoroutine
///   at Fika.Core.Main.FreeCamera.FreeCameraController.MainPlayer_DiedEvent
/// </code>
///
/// The interop trampoline swallows that exception, so the routine does not die visibly - it loses the wait and
/// runs on out of step (the 5 s death-camera wait became one frame).
///
/// This class holds the invariant "no managed wait object ever reaches the engine": the value the inner routine
/// yields is translated before the engine can read it.
/// <list type="bullet">
/// <item><description><see cref="WaitSeconds"/> becomes a real engine <c>WaitForSeconds</c>. The engine reads
/// that object's own field in native code, which is safe - only the *managed* read of <c>m_Seconds</c> was
/// fatal. The port already constructs engine objects of exactly this kind elsewhere (<c>new WaitForEndOfFrame()</c>
/// in the same routines).</description></item>
/// <item><description>A nested managed <c>IEnumerator</c> becomes another <see cref="EngineRoutineBridge"/>,
/// wrapped for il2cpp, so the invariant also holds one level down.</description></item>
/// <item><description>An il2cpp object (engine waits, <c>AsyncOperation</c>, a native enumerator) is passed
/// through unchanged.</description></item>
/// <item><description><c>null</c> stays <c>null</c> (the engine's own one-frame yield).</description></item>
/// <item><description>Anything else costs one frame and one diagnostics line; it is never handed over, because
/// the engine's conversion of it can only raise the same exception.</description></item>
/// </list>
/// If the engine <c>WaitForSeconds</c> cannot be built, the wait degrades to the same duration measured against
/// <c>Time.time</c> and the diagnostics name the routine - the wait is never dropped silently.
/// </summary>
internal sealed class EngineRoutineBridge : IEnumerator
{
    private readonly IEnumerator _routine;
    private readonly string _site;

    /// <summary>End of a managed fallback wait, in <c>Time.time</c> seconds; negative infinity means "not parked".</summary>
    private float _parkUntilScaled = float.NegativeInfinity;

    internal EngineRoutineBridge(IEnumerator routine, string site)
    {
        _routine = routine ?? throw new ArgumentNullException(nameof(routine));
        _site = string.IsNullOrEmpty(site) ? "unknown" : site;
    }

    /// <summary>The value the engine reads. Always safe for it to convert (null or an il2cpp object).</summary>
    public object Current { get; private set; }

    public bool MoveNext()
    {
        if (Time.time < _parkUntilScaled)
        {
            // A managed fallback wait is still running: the engine sees an ordinary one-frame yield.
            Current = null;
            return true;
        }

        _parkUntilScaled = float.NegativeInfinity;

        for (var steps = 0; steps < EngineCoroutines.MaxStepsPerTrigger; steps++)
        {
            if (!_routine.MoveNext())
            {
                Current = null;
                return false;
            }

            var yielded = _routine.Current;

            // Managed tasks: the pump's convention is "continue while finished, otherwise one frame".
            if (yielded is Task task)
            {
                if (task.IsCompleted)
                {
                    continue;
                }

                Current = null;
                return true;
            }

            if (yielded is Il2CppSystem.Threading.Tasks.Task nativeTask)
            {
                if (nativeTask.IsCompleted)
                {
                    continue;
                }

                Current = null;
                return true;
            }

            Current = Convert(yielded);
            return true;
        }

        EngineCoroutines.ReportStepLimit(_site);
        Current = null;
        return true;
    }

    public void Reset()
    {
        // Unity's coroutine scheduler never calls Reset on an enumerator it drives, and BepInEx's wrapper
        // only carries the member because the interface requires it.
    }

    /// <summary>
    /// The one conversion point. Its contract: the returned value is <c>null</c> or an <see cref="Il2CppObjectBase"/>,
    /// so <c>Il2CppManagedEnumerator.Current</c> can never raise again.
    /// </summary>
    private object Convert(object yielded)
    {
        switch (yielded)
        {
            case null:
                return null;

            case WaitSeconds wait:
                return EngineWait(wait);

            case Il2CppSystem.Collections.IEnumerator nativeNested:
                // A native enumerator (the game's own coroutine shape) is already an engine value; the engine
                // steps it itself, exactly as the pump's own native case does.
                return nativeNested;

            case IEnumerator nested:
                // A nested managed routine runs under the same rule: the engine receives an il2cpp enumerator
                // whose Current this class answers, so a WaitSeconds deeper in the nesting is translated too.
                return EngineCoroutines.Bridge(nested, _site).WrapToIl2Cpp();

            case Il2CppObjectBase engineObject:
                // WaitForSeconds, WaitForEndOfFrame, WaitUntil, a native enumerator, an AsyncOperation: the
                // engine owns these and reads them natively.
                return engineObject;

            default:
                // A managed object with no engine counterpart. Handing it over would raise the exception this
                // class exists to prevent, so it costs one frame and names itself once.
                EngineCoroutines.ReportUnsupportedYield(_site, yielded);
                return null;
        }
    }

    private object EngineWait(WaitSeconds wait)
    {
        try
        {
            var engineWait = new WaitForSeconds(wait.Seconds);
            EngineCoroutines.ReportConversion(_site, "WaitSeconds(" + wait.Seconds + " s) -> engine WaitForSeconds");
            return engineWait;
        }
        catch (Exception exception)
        {
            // The wait is kept, only its clock changes: from now on it is measured against Time.time by this
            // bridge instead of by the engine object.
            _parkUntilScaled = Time.time + wait.Seconds;
            EngineCoroutines.ReportConversionFallback(_site, wait.Seconds, exception);
            return null;
        }
    }
}
