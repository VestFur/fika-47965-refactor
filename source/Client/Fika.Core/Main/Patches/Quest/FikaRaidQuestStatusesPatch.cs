using System.Reflection;
using EFT.Quests;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Quest;

/// <summary>
/// The native end-of-raid quest settlement has to run for every quest, not only
/// the failing ones. A player scav joins with quests whose templates SPT keeps in
/// the other side's data and the native pass can complain about them, so the port
/// used to replace the whole pass with <see cref="RaidQuestStatuses.Apply"/>.
/// That dropped every completion transition: conditions such as "exit
/// &lt;location&gt;" (a CounterCreator with ExitStatus + Location children) never
/// counted, which is exactly why only some objectives appeared to progress.
/// The port now runs its own failure transitions first and then lets the native
/// settlement do its work.
/// </summary>
public sealed class FikaRaidQuestStatusesPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(
        typeof(QuestControllerClientLocalGame), nameof(QuestControllerClientLocalGame.ManageQuestStatusesForPveOfflineGameEnd));

    [PatchPrefix]
    private static bool Prefix(QuestControllerClientLocalGame __instance)
    {
        if (FikaQuestController.Find<ClientQuestController>(__instance) == null) return true;
        RaidQuestStatuses.Apply(__instance.Profile.QuestsData, __instance.Profile.Side,
            id => FindByTemplate(__instance.Quests, id), message => UnityEngine.Debug.LogError(message));

        // Let the native settlement run: it is what turns completed conditions
        // (including the end-of-raid ExitStatus/Location counters) into quest
        // status transitions. Replacing it silently dropped those completions.
        return true;
    }

    private static EFT.Quests.Quest FindByTemplate(QuestBook quests, string id)
    {
        foreach (var quest in quests)
            if (quest.Template.Id == id) return quest;
        return null;
    }
}
