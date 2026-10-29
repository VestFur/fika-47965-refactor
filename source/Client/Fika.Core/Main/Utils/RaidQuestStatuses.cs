using System;
using EFT;
using EFT.Quests;

namespace Fika.Core.Main.Utils;

internal static class RaidQuestStatuses
{
    internal static void Apply(Il2CppSystem.Collections.Generic.List<QuestStatusData> statuses,
        EPlayerSide side, Func<string, Quest> find, Action<string> missing)
    {
        foreach (var status in statuses)
        {
            var template = status.Template;
            if (template == null) continue;
            var quest = find(template.Id);
            if (quest == null)
            {
                // SPT stores Scav repeatables in the shared PMC quest data, while
                // the raid quest book intentionally excludes the other side.
                var otherSide = (side is EPlayerSide.Bear or EPlayerSide.Usec && template.PlayerGroup == EPlayerGroup.Scav) ||
                    (side == EPlayerSide.Savage && template.PlayerGroup == EPlayerGroup.Pmc);
                if (!otherSide) missing($"Quest with id {template.Id} wasn't found! Side = {template.PlayerGroup}");
                continue;
            }
            // Preserve the native end-of-raid failure transition. Do not remove
            // shared statuses or modify the live collection during enumeration.
            if (quest.StatusHasBeenChangedInPveOffline && quest.QuestStatus is EQuestStatus.Fail or EQuestStatus.FailRestartable)
                status.Status = EQuestStatus.MarkedAsFailed;
        }
    }
}
