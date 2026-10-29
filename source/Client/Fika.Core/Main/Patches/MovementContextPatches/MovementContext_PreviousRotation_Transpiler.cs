using System.Reflection;
using EFT;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.MovementContextPatches;

// Preserve Fika's local rotation path without the vanilla online-controller
// lookup. The SPT 5 native setters also refresh cached movement directions.
internal class MovementContext_PreviousRotation_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(MovementContext), "get_PreviousRotation");

    [PatchPrefix]
    public static bool Prefix(MovementContext __instance, ref Vector2 __result)
    {
        __result = __instance._previousRotation;
        return false;
    }
}
