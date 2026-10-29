using System.Reflection;
using Comfort.Common;
using EFT.InventoryLogic.Operations;
using EFT.Quests;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Networking.Packets.Backend;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Quest;

public sealed class FikaQuestConditionChangedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ConditionalController<EFT.Quests.Quest>), "OnConditionValueChanged");
    [PatchPostfix]
    private static void Postfix(ConditionalController<EFT.Quests.Quest> __instance, EFT.Quests.Quest conditional,
        EQuestStatus status, Condition condition, bool notify)
    {
        FikaQuestController.Find<ClientQuestController>(__instance)?.OnConditionValueChanged(conditional, status, condition, notify);
    }
}

public sealed class FikaQuestDisposePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(QuestControllerClient), nameof(QuestControllerClient.Dispose));
    [PatchPrefix]
    private static bool Prefix(QuestControllerClient __instance)
    {
        var observed = FikaQuestController.Find<ObservedQuestController>(__instance);
        if (observed == null) return true;
        observed.DisposeObservedNative();
        return false;
    }

    [PatchPostfix]
    private static void Postfix(QuestControllerClient __instance) => FikaQuestController.Find<FikaQuestController>(__instance)?.OnNativeDisposed();
}

public sealed class ObservedQuestRunPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(QuestControllerClientBackend), nameof(QuestControllerClientBackend.Run));
    [PatchPrefix]
    private static bool Prefix(QuestControllerClientBackend __instance) => FikaQuestController.Find<ObservedQuestController>(__instance) == null;
}

public sealed class ObservedQuestManagePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(QuestController), nameof(QuestController.ManageConditional));
    [PatchPrefix]
    private static bool Prefix(QuestController __instance) => FikaQuestController.Find<ObservedQuestController>(__instance) == null;
}

public sealed class FikaQuestFinishOperationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(QuestFinishOperation), nameof(QuestFinishOperation.ExecuteAsync));
    [PatchPrefix]
    private static void Prefix(QuestFinishOperation __instance, out InRaidQuestPacket? __state)
    {
        __state = FikaQuestController.Find<ClientQuestController>(__instance.QuestController)?.CaptureFinish(__instance);
    }
    [PatchPostfix]
    private static void Postfix(QuestFinishOperation __instance, InRaidQuestPacket? __state, ref Il2CppSystem.Threading.Tasks.Task<IResult> __result)
    {
        if (__state.HasValue && FikaQuestController.Find<ClientQuestController>(__instance.QuestController) is { } controller)
            __result = controller.SynchronizeAfterOperation(__result, __state.Value);
    }
}

public sealed class FikaQuestHandoverOperationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(QuestHandoverOperation), nameof(QuestHandoverOperation.ExecuteAsync));
    [PatchPrefix]
    private static void Prefix(QuestHandoverOperation __instance, out InRaidQuestPacket? __state)
    {
        __state = FikaQuestController.Find<ClientQuestController>(__instance.QuestController)?.CaptureHandover(__instance);
    }
    [PatchPostfix]
    private static void Postfix(QuestHandoverOperation __instance, InRaidQuestPacket? __state, ref Il2CppSystem.Threading.Tasks.Task<IResult> __result)
    {
        if (__state.HasValue && FikaQuestController.Find<ClientQuestController>(__instance.QuestController) is { } controller)
            __result = controller.SynchronizeAfterOperation(__result, __state.Value);
    }
}
