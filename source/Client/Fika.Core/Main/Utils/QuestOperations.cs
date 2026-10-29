using System;
using EFT;
using EFT.InventoryLogic.Operations;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Classification and diagnostics for the operations that only change the quest
/// data of the player who triggered them: reading a note, a chapter's conditions,
/// a single condition or a note link, and studying a tape.
/// <para>
/// Quest state is authoritative on the machine that owns the profile. A client
/// runs its own quest controller and its profile is what <c>LocalRaidEnded</c>
/// uploads when the raid ends, while the host only keeps a display copy of a
/// peer's quests (<c>ObservedQuestController</c>, fed by <c>QuestSyncPacket</c>).
/// These operations move no items and pay no rewards - they are pure profile
/// bookkeeping - so they have to run on the local controller and must never be
/// relayed: a relay would delay the read until the host acknowledges it and turn
/// any rejection into an inventory resync in the middle of the quest screen.
/// </para>
/// <para>
/// They are not <c>BaseQuestOperation</c> subclasses, which is why the port used
/// to dispatch them even though it already executed every other quest
/// transaction locally. That dispatch is also what produced the
/// "Serializer delegate missing for registered type" failures for
/// <c>ReadChapterConditionsOperationDescriptor</c> and
/// <c>ReadQuestNoteOperationDescriptor</c> in real raids.
/// </para>
/// <para>
/// Every accessor is guarded: this classification drives execution, so a
/// diagnostic that throws must never decide it.
/// </para>
/// </summary>
internal static class QuestOperations
{
    /// <summary>
    /// True for operations that only change the local player's quest bookkeeping.
    /// <paramref name="detail"/> describes the target for the <c>[QuestOp]</c> log.
    /// </summary>
    internal static bool IsLocalOnly(AbstractOperation operation, out string detail)
    {
        detail = null;
        if (operation == null)
        {
            return false;
        }

        if (operation.TryCast<ReadQuestNoteOperation>() is { } readNote)
        {
            detail = $"note {Safe(() => readNote._note?.Id?.ToString())}";
            return true;
        }

        if (operation.TryCast<ReadChapterConditionsOperation>() is { } readChapter)
        {
            detail = $"chapter {Safe(() => readChapter._chapter?.Template?.Id)} " +
                $"({Safe(() => readChapter._type.ToString())})";
            return true;
        }

        if (operation.TryCast<ReadQuestConditionOperation>() is { } readCondition)
        {
            detail = $"condition {Safe(() => readCondition._condition?.id?.ToString())}";
            return true;
        }

        if (operation.TryCast<ReadQuestLinkOperation>() is { } readLink)
        {
            detail = $"note link {Safe(() => readLink._link?.Id?.ToString())}";
            return true;
        }

        // StudyItemPlayerOperation derives from StudyItemBaseOperation, so it has
        // to be tested first.
        if (operation.TryCast<StudyItemPlayerOperation>() is { } studyForPlayer)
        {
            detail = $"tape {Safe(() => studyForPlayer.Item?.Id)} (recorder)";
            return true;
        }

        if (operation.TryCast<StudyItemOperation>() is { } study)
        {
            detail = $"tape {Safe(() => study.Item?.Id)}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Describes a quest-family operation for the <c>[QuestOp]</c> log lines.
    /// Returns false for every unrelated operation.
    /// </summary>
    internal static bool TryDescribe(AbstractOperation operation, out string detail)
    {
        detail = null;
        if (operation == null)
        {
            return false;
        }

        if (operation.TryCast<BaseQuestOperation>() is { } questOperation)
        {
            detail = $"quest {Safe(() => questOperation.Quest?.Template?.Id)}";
            return true;
        }

        return IsLocalOnly(operation, out detail);
    }

    /// <summary>
    /// Describes a quest-family operation descriptor for the <c>[QuestOp]</c> log
    /// lines. Returns false for every unrelated descriptor.
    /// </summary>
    internal static bool TryDescribe(InventoryOperationDescriptor descriptor, out string detail)
    {
        detail = null;
        if (descriptor == null)
        {
            return false;
        }

        // The player variant derives from the plain study descriptor.
        if (descriptor.TryCast<StudyItemPlayerOperationDescriptor>() is { } studyForPlayer)
        {
            detail = $"tape {studyForPlayer.ItemId} (recorder)";
            return true;
        }

        if (descriptor.TryCast<StudyItemOperationDescriptor>() is { } study)
        {
            detail = $"tape {study.ItemId}";
            return true;
        }

        if (descriptor.TryCast<ReadQuestNoteOperationDescriptor>() is { } note)
        {
            detail = $"note {note.NoteId}";
            return true;
        }

        if (descriptor.TryCast<ReadChapterConditionsOperationDescriptor>() is { } chapter)
        {
            detail = $"chapter {chapter.ChapterId} ({chapter.Type})";
            return true;
        }

        if (descriptor.TryCast<ReadQuestConditionOperationDescriptor>() is { } condition)
        {
            detail = $"condition {condition.ConditionId}";
            return true;
        }

        if (descriptor.TryCast<ReadQuestLinkOperationDescriptor>() is { } link)
        {
            detail = $"note link {link.LinkId}";
            return true;
        }

        return false;
    }

    private static string Safe(Func<string> read)
    {
        try
        {
            return read() ?? "unknown";
        }
        catch (Exception exception)
        {
            return $"unknown ({exception.GetType().Name})";
        }
    }
}
