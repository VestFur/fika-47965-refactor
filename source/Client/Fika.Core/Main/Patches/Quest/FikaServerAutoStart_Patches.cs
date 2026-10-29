using System;
using System.Reflection;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Quest;

public sealed class FikaServerAutoStartResponsePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(
        typeof(QuestController), nameof(QuestController.HandleAutoStartQuestsDTO));

    [PatchPrefix]
    private static void Prefix(QuestController __instance, AutoStartQuestsDTO autoStartQuestsDto, out IDisposable __state)
    {
        __state = FikaQuestController.Find<ClientQuestController>(__instance) != null
            ? ServerAutoStartQuests.Begin(__instance, autoStartQuestsDto) : null;
    }

    [PatchPostfix]
    private static void Postfix(IDisposable __state, ref Il2CppSystem.Threading.Tasks.Task __result)
    {
        if (__state != null) __result = ServerAutoStartQuests.Complete(__result, __state);
    }
}

public sealed class FikaServerAutoStartTransactionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(
        typeof(Player.PlayerInventoryController), nameof(ItemController.TryRunNetworkTransaction));

    [PatchPrefix]
    private static bool Prefix(Player.PlayerInventoryController __instance, OperationResult operationResult,
        Callback callback, ref Il2CppSystem.Threading.Tasks.Task<IResult> __result)
    {
        if (operationResult.Failed ||
            !ServerAutoStartQuests.Contains(__instance, operationResult.Value?.TryCast<QuestAcceptResult>())) return true;

        // A backend-confirmed quest accept is profile bookkeeping, including after
        // death. Keep the native transaction's validation, operation, callback and
        // failure handling; do not open the player's other inventory transactions.
        __result = NativeBaseCalls.CallServerQuestTransaction(__instance, operationResult, callback);
        return false;
    }
}
