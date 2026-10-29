using EFT;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.DebugPatches;

/// <summary>
/// Removes the need for a Lab Keycard on debug builds
/// </summary>
[DebugPatch]
internal class LabsKeycardDebugPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MatchmakerOperation)
            .GetMethod(nameof(MatchmakerOperation.LocationKeyCheck));
    }

    [PatchPrefix]
    public static bool Prefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}
