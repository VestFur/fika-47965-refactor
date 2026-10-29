using System.Reflection;
using EFT.CameraControl;
using FikaFreeCamera = Fika.Core.Main.FreeCamera.FreeCamera;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Camera;

// Keep SPT 5's native camera LateUpdate for normal first-person gameplay.
// Fika's old patch replaced the entire method with ManualLateUpdate; on SPT 5
// that leaves the FPS camera at the deployment position.  Only suppress the
// native update while the explicit Fika free camera is active.
public class PlayerCameraController_LateUpdate_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(PlayerCameraController), nameof(PlayerCameraController.LateUpdate));

    [PatchPrefix]
    public static bool Prefix(PlayerCameraController __instance)
    {
        var camera = CameraManager.Instance?.Camera;
        var freeCamera = camera?.GetComponent<FikaFreeCamera>();
        if (freeCamera != null && freeCamera.IsActive)
        {
            __instance._currentCameraState?.ManualLateUpdate(Time.deltaTime);
            return false;
        }

        // Let the IL2CPP native implementation update and bind the FPS camera
        // to the local player's head/weapon each LateUpdate.
        return true;
    }
}
