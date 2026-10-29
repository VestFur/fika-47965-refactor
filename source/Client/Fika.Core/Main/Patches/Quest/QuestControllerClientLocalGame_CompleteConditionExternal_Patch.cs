using System.Reflection;
using EFT.Quests;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.ObservedClasses;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Quest;

/// <summary>
/// Observed players must not complete quest conditions against the local
/// profile.  Their controller is display-only and receives quest state over
/// Fika packets.
/// </summary>
public sealed class QuestControllerClientLocalGame_CompleteConditionExternal_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(QuestControllerClientLocalGame).GetMethod(
            "EFT.Quests.IActiveQuestController.CompleteConditionExternal",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    [PatchPrefix]
    private static bool Prefix(QuestControllerClientLocalGame __instance) =>
        FikaQuestController.Find<ObservedQuestController>(__instance) == null;
}
