using System.Reflection;
using EFT;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.MovementContextPatches;

// Preserve Fika's local rotation path without the vanilla online-controller
// lookup. The SPT 5 native setters also refresh cached movement directions.
internal class MovementContext_SetPreviousRotation_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(MovementContext), "SetPreviousRotation");

    [PatchPrefix]
    public static bool Prefix(MovementContext __instance, Vector2 rotation)
    {
        __instance._previousRotation = rotation;
        return false;
    }
}
