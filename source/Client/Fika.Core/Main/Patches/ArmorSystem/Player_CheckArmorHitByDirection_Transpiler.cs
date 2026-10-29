using System.Reflection;
using EFT;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.ArmorSystem;

// Same SPT 5 hit matching, using Fika's equipped-armor cache.
internal class Player_CheckArmorHitByDirection_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.CheckArmorHitByDirection));

    [PatchPrefix]
    public static bool Prefix(Player __instance, BodyPartCollider bodyPart, ref bool __result)
    {
        if (__instance.TryCast<Fika.Core.Main.Players.FikaPlayer>() == null) return true;
        var plate = bodyPart.TryCast<ArmorPlateCollider>();
        var plateType = plate != null ? plate.ArmorPlateColliderType : (EArmorPlateCollider)0;
        var armors = __instance._preAllocatedArmorComponents;
        __result = false;
        for (var i = 0; i < armors.Count; i++)
        {
            if (!armors[i].ShotMatches(bodyPart.BodyPartColliderType, plateType)) continue;
            __result = true;
            break;
        }
        return false;
    }
}
