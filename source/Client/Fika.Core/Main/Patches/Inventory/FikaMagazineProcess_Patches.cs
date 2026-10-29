using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Inventory;
public sealed class FikaLoadMagazineStartPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Player.PlayerInventoryController.LoadMagazineProcess), "Start");
    [PatchPrefix]
    private static bool Prefix(Player.PlayerInventoryController.LoadMagazineProcess __instance, ref Il2CppSystem.Threading.Tasks.Task<IResult> __result)
    {
        var process = FikaMagazineProcesses.Find(__instance);
        if (process == null) return true;
        __result = NativeTask.FromManaged(process.Start());
        return false;
    }
}

public sealed class FikaLoadMagazineProceedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Player.PlayerInventoryController.LoadMagazineProcess), "Proceed");
    [PatchPrefix]
    private static bool Prefix(Player.PlayerInventoryController.LoadMagazineProcess __instance, bool success)
    {
        var process = FikaMagazineProcesses.Find(__instance);
        if (process == null) return true;
        process.Proceed(success);
        return false;
    }
}

public sealed class FikaLoadMagazineItemPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Player.PlayerInventoryController.LoadMagazineProcess), "TryProceedForItem");
    [PatchPrefix]
    private static bool Prefix(Player.PlayerInventoryController.LoadMagazineProcess __instance, Item item)
    {
        var process = FikaMagazineProcesses.Find(__instance);
        if (process == null) return true;
        process.TryProceedForItem(item);
        return false;
    }
}

public sealed class FikaLoadMagazineCancelledPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredPropertyGetter(typeof(Player.PlayerInventoryController.LoadMagazineProcess), "Cancelled");
    [PatchPrefix]
    private static bool Prefix(Player.PlayerInventoryController.LoadMagazineProcess __instance, ref bool __result)
    {
        var process = FikaMagazineProcesses.Find(__instance);
        if (process == null) return true;
        __result = process.IsCancelled;
        return false;
    }
}
public sealed class FikaUnloadMagazineStartPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Player.PlayerInventoryController.UnloadMagazineProcess), "Start");
    [PatchPrefix]
    private static bool Prefix(Player.PlayerInventoryController.UnloadMagazineProcess __instance, ref Il2CppSystem.Threading.Tasks.Task<IResult> __result)
    {
        var process = FikaMagazineProcesses.Find(__instance);
        if (process == null) return true;
        __result = NativeTask.FromManaged(process.Start());
        return false;
    }
}

public sealed class FikaUnloadMagazineProceedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Player.PlayerInventoryController.UnloadMagazineProcess), "Proceed");
    [PatchPrefix]
    private static bool Prefix(Player.PlayerInventoryController.UnloadMagazineProcess __instance, bool success)
    {
        var process = FikaMagazineProcesses.Find(__instance);
        if (process == null) return true;
        process.Proceed(success);
        return false;
    }
}

public sealed class FikaUnloadMagazineItemPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(Player.PlayerInventoryController.UnloadMagazineProcess), "TryProceedForItem");
    [PatchPrefix]
    private static bool Prefix(Player.PlayerInventoryController.UnloadMagazineProcess __instance, Item item)
    {
        var process = FikaMagazineProcesses.Find(__instance);
        if (process == null) return true;
        process.TryProceedForItem(item);
        return false;
    }
}

public sealed class FikaUnloadMagazineCancelledPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredPropertyGetter(typeof(Player.PlayerInventoryController.UnloadMagazineProcess), "Cancelled");
    [PatchPrefix]
    private static bool Prefix(Player.PlayerInventoryController.UnloadMagazineProcess __instance, ref bool __result)
    {
        var process = FikaMagazineProcesses.Find(__instance);
        if (process == null) return true;
        __result = process.IsCancelled;
        return false;
    }
}
