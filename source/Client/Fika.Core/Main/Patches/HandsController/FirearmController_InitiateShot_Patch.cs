using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.ClientClasses.HandsControllers;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.HandsController;

/// <summary>SPT 5's shot entry point is nonvirtual, including calls from native operations.</summary>
public sealed class FirearmController_InitiateShot_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot),
        [typeof(IWeapon), typeof(Ammo), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(int), typeof(float)]);

    [PatchPrefix]
    private static void Prefix(Player.FirearmController __instance, IWeapon weapon, Ammo ammo,
        Vector3 shotPosition, Vector3 shotDirection, Vector3 fireportPosition, int chamberIndex, float overheat)
    {
        // IL2CPP can supply a base wrapper for an injected controller.
        var controller = __instance.TryCast<FikaClientFirearmController>();
        controller?.SendInitiatedShot(weapon, ammo, shotPosition, shotDirection, fireportPosition, chamberIndex, overheat);
    }
}
