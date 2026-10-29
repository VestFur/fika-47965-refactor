using System.Reflection;
using EFT;
using Fika.Core.Main.Players;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.ArmorSystem;

internal class Player_RecalculateEquipmentParams_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.RecalculateEquipmentParams));

    [PatchPostfix]
    public static void Postfix(Player __instance)
    {
        if (__instance.TryCast<FikaPlayer>() == null) return;
        // SPT 5 uses this list as scratch storage and clears it before returning.
        // Preserve all native equipment/stat updates, then restore the persistent
        // armor list used by Fika damage, durability and replication handlers.
        var armors = __instance._preAllocatedArmorComponents;
        armors.Clear();
        __instance.Inventory.GetPutOnArmorsNonAlloc(armors);
    }
}
