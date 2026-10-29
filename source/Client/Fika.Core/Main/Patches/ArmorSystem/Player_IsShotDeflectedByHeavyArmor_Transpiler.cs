using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.ArmorSystem;

internal class Player_IsShotDeflectedByHeavyArmor_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.IsShotDeflectedByHeavyArmor));

    [PatchPrefix]
    public static bool Prefix(Player __instance, EBodyPartColliderType colliderType, EArmorPlateCollider armorPlateCollider,
        int shotSeed, ref bool __result)
    {
        if (__instance.TryCast<Fika.Core.Main.Players.FikaPlayer>() == null) return true;
        __result = false;
        if (!__instance.Skills.HeavyVestNoBodyDamageDeflectChance) return false;
        var settings = Singleton<GlobalConfiguration>.Instance.SkillsSettings.HeavyVests;
        var armors = __instance._preAllocatedArmorComponents;
        for (var i = 0; i < armors.Count; i++)
        {
            var armor = armors[i];
            var repairable = armor.Repairable;
            if (armor.ArmorType != EArmorType.Heavy ||
                repairable.Durability < repairable.MaxDurability * settings.RicochetChanceHVestsCurrentDurabilityThreshold ||
                repairable.Durability < repairable.TemplateDurability * settings.RicochetChanceHVestsMaxDurabilityThreshold ||
                !armor.ShotMatches(colliderType, armorPlateCollider)) continue;
            if (__instance._heavyVestsDeflectRandoms.GetRandom(shotSeed) >= settings.RicochetChanceHVestsEliteLevel) continue;
            __result = true;
            break;
        }
        return false;
    }
}
