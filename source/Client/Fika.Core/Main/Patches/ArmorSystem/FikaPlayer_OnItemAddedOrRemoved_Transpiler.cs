using System.Reflection;
using EFT;
using Fika.Core.Main.Players;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.ArmorSystem;

internal class FikaPlayer_OnItemAddedOrRemoved_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.OnItemAddedOrRemoved));

    [PatchPostfix]
    public static void Postfix(Player __instance)
    {
        if (__instance.TryCast<FikaPlayer>() == null) return;
        // Includes nested plate changes, which need not replace the armor slot.
        // Keep the native item event path and refresh Fika's cache afterwards.
        var armors = __instance._preAllocatedArmorComponents;
        armors.Clear();
        __instance.Inventory.GetPutOnArmorsNonAlloc(armors);
    }
}
