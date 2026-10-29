using EFT;
using System.Linq;
using System.Reflection;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Camera;

public class Firearms_UpdateModsVisualControllers_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Firearms).GetMethod(nameof(Firearms.UpdateModsVisualControllers));
    }

    [PatchPrefix]
    public static bool Prefix(Firearms __instance)
    {
        if (__instance.Player != null && !__instance.Player.IsYourPlayer)
        {
            __instance._tacticalComboVisualControllers = __instance._weaponHierarchy.GetComponentsInChildrenActiveIgnoreFirstLevel<TacticalComboVisualController>().AsManaged().ToArray();
            __instance._sightModVisualControllers = __instance._weaponHierarchy.GetComponentsInChildrenActiveIgnoreFirstLevel<SightModVisualControllers>().AsManaged().ToArray();
            __instance._launcherViauslControllers = __instance._weaponHierarchy.GetComponentsInChildrenActiveIgnoreFirstLevel<LauncherViauslController>().AsManaged().ToArray();
            __instance._bipodViewController = __instance._weaponHierarchy.GetComponentsInChildrenActiveIgnoreFirstLevel<BipodViewController>().AsManaged().FirstOrDefault();

            return false;
        }

        return true;
    }
}
