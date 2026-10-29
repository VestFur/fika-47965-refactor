using System;
using EFT;
using EFT.CameraControl;
using UnityEngine;

namespace Fika.Core.Main.Utils;

/// <summary>
/// (2026-10-18 refactor W1b / tracker A11) The night-vision depth of field is applied by the NVG effect
/// itself and the component has no OnDisable: when the effect is switched off without going through
/// NightVision.Switch (the free camera writes NightVision.On directly), nothing removes the blur and the
/// camera keeps a "hazy, looks like depth of field" frame for the rest of the raid. That is the only job
/// this class has left - the per-tick self-heal that used to fight the headwear device is gone.
/// </summary>
internal static class NvgDiagnostics
{
    private static float _nextTick;

    internal static void Tick()
    {
        // (2026-10-18 refactor W2a) Called from the per-frame plugin update; the check itself is two reads,
        // the rest runs at most twice a second - the reconcile only has to be faster than the player can
        // notice a stuck blur, not per-frame.
        var now = Time.unscaledTime;
        if (now < _nextTick)
        {
            return;
        }

        _nextTick = now + 0.5f;

        try
        {
            if (!CameraManager.Exist)
            {
                return;
            }

            var cameraManager = CameraManager.Instance;
            if (cameraManager == null)
            {
                return;
            }

            var zBlur = cameraManager.NightVisionZBlur;
            if (zBlur == null || !zBlur.IsApplied)
            {
                return;
            }

            var nightVision = cameraManager.NightVision;
            if (nightVision == null || nightVision.On || nightVision.InProcessSwitching)
            {
                return;
            }

            zBlur.Apply(false);
        }
        catch (Exception)
        {
        }
    }
}
