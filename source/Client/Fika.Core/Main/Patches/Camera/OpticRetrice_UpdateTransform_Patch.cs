using System.Reflection;
using EFT.CameraControl;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Camera;

public class OpticRetrice_UpdateTransform_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(OpticRetrice).GetMethod(nameof(OpticRetrice.UpdateTransform));
    }

    [PatchPrefix]
    public static bool Prefix(OpticSight opticSight, EFT.CameraControl.OpticRetrice __instance)
    {
        return opticSight.ScopeData != null && opticSight.ScopeData.Reticle != null && __instance._renderer != null;
    }
}
