using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.World;

// BJ (analysis/terminal-rauf-card): TERMINAL-RAUF-CARD.
//
// The four patches of this file are the whole native surface of the rule 「the Russian soldiers at the
// terminal transfer point stop attacking a player who passed the card check」:
//
//   Player_ExecuteInteraction_TerminalCard_Patch - the swipe itself, on the machine that completes the
//       interaction. (native <c>EFT.Player.ExecuteInteraction(WorldInteractiveObject, InteractionResult)</c>,
//       reached by the client's own <c>FikaPlayer.ExecuteInteraction</c>; a machine that owns no sentry takes
//       the accepted card from here and reports it - the hop a client was missing)
//   SentryBossLogic_OnTerminalAccessGained_Patch   - the write of the state, on the machine it happens on.
//       (native <c>SentryBossLogic.OnTerminalAccessGained(Player)</c>, subscribed to
//       <c>GlobalEventDispatcher.OnTerminalTransitAccessGained</c> in the sentry's constructor)
//   SentryBossLogic_HaveAccess_Patch              - the read of the state, every hostility recalculation.
//       (native <c>IAccessBossLogic.HaveAccess(Int32)</c>, called by <c>BossSentryHostilityRule</c>)
//   GlobalEventDispatcher_TerminalAccess_Emit_Patch - the emission of the native event. This is the only
//       surface a machine WITHOUT a sentry of its own has (every Fika client: it raises the event for its
//       own player and owns no bot at all, analysis/terminal-rauf-hostility-2), so it is where the fact is
//       routed to the authority - the machine that simulates the bots and therefore owns the answer.
//
// Nothing is written to a native value from the patches themselves: the swipe reads the native result of the
// interaction and hands the fact to the report entry (the actor is the player object of the call, see that
// patch); the grant reports the native write and (on the authority) hands the same fact to the other machines
// so their sentries can apply it through the same native entry point; the read only writes lines; the emission
// writes lines and, when this machine owns no sentry to apply the fact to, hands it to the authority / applies
// it to the group hostility data (both under the one switch family of FikaSentryAccess). All four pass
// something through untouched, so turning FikaSentryAccess.RepairEnabled off restores the previous behaviour
// without a rebuild.

/// <summary>
/// THE SWIPE - the interaction the machine that owns the player completes:
/// <c>EFT.Player.ExecuteInteraction(WorldInteractiveObject, InteractionResult)</c> (interop :275004, public
/// virtual, parameters <c>door</c> and <c>interactionResult</c>). A Fika client runs the interaction of its own
/// player locally - <c>FikaPlayer.ExecuteInteraction</c> (Main/Players/FikaPlayer.cs:1517) calls the native
/// base of this very method through <c>NativeBaseCalls.Call103</c> and only sends the packet as the host's
/// copy - so this is where the swipe of this machine's player ends, and where the fact 「the card was
/// accepted」 exists on the machine that owns no bot.
///
/// <para>WHO SWIPED is not inferred: <c>__instance</c> is the player object that performed the interaction, and
/// the patch accepts it only when it is this machine's own player (<c>Player.IsYourPlayer</c>, :275673) on a
/// Fika client. The native base is patched instead of the <c>FikaPlayer</c> override because that override is a
/// managed method of an injected class, which this port's PatchManager refuses (it demands the IL2CPP detour
/// backend for every Il2CppObjectBase declaring type, PatchManager.cs:163-176); the pattern is the one of
/// Main/Patches/ArmorSystem/FikaPlayer_OnItemAddedOrRemoved_Transpiler.cs:11 - a native method, the Fika type
/// only as the runtime instance.</para>
///
/// <para>WHICH INTERACTION: only an unlock of a <c>CardReader</c>. A card swipe is an unlock -
/// <c>CardReader.UnlockOperation(KeyComponent, Player, WorldInteractiveObject)</c> (:205017) builds the
/// <c>UnlockResult</c> whose <c>Succeed</c> (:212752) is the reader's answer for that card - and the reader's
/// own reaction function <c>ResolveReaction(KeyComponent)</c> (:205015) decides the beep and the trigger the
/// same interaction plays. Every other interactive object, and every result without a key, is not the fact of
/// 「the card check was passed」 and is skipped.</para>
///
/// <para>The fact is not applied and not broadcast from here: it goes to
/// <see cref="FikaSentryAccess.NoteNativeEmission"/>, the entry that reports a machine without bots to the
/// authority (and the one place the switch family decides what happens next).</para>
/// </summary>
internal sealed class Player_ExecuteInteraction_TerminalCard_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(Player)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(Player.ExecuteInteraction)
                && method.GetParameters() is { Length: 2 } parameters
                && parameters[0].ParameterType == typeof(WorldInteractiveObject)
                && parameters[1].ParameterType == typeof(InteractionResult));
        if (target == null)
        {
            throw new MissingMethodException(
                "Player.ExecuteInteraction(WorldInteractiveObject, InteractionResult)");
        }

        return target;
    }

    [PatchPostfix]
    public static void Postfix(Player __instance, WorldInteractiveObject __0, InteractionResult __1)
    {
        try
        {
            Observe(__instance, __0, __1);
        }
        catch (System.Exception)
        {
            // A diagnostic must never break the native chain that carries it.
        }
    }

    private static void Observe(Player player, WorldInteractiveObject target, InteractionResult interactionResult)
    {
        // The actor is the player object the native call runs on: on a client that is this machine's own player,
        // and the native base is reached by that player's own interaction only (a remote character is an
        // ObservedPlayer, whose override never calls it). A fact of the call, not an inference of ours.
        if (player == null || player.IsYourPlayer != true)
        {
            return;
        }

        // A machine that owns a sentry applies the fact through its own native chain; only a machine without one
        // (every Fika client) has to report it.
        if (!FikaBackendUtils.IsClient || FikaSentryAccess.CountLocalSentries() != 0)
        {
            return;
        }

        var reader = target != null ? target.TryCast<CardReader>() : null;
        if (reader == null)
        {
            return;
        }

        var unlock = interactionResult != null ? interactionResult.TryCast<UnlockResult>() : null;
        if (unlock == null || unlock.Key == null)
        {
            // A reader interaction without an unlock result is not a card swipe. The line stays because a log
            // that distinguishes "the hook never fired" from "the hook fired without a key" is what decides the
            // next round; it is written once per reader.
            FikaSentryAccess.Line("swipe-nokey:" + reader.Id,
                "card=swiped role=" + FikaSentryAccess.RoleOf(player) + " state=card-reader applied=no side="
                + FikaSentryAccess.Side + " source=native-execute reader=" + reader.Id
                + " -> result=the interaction of this machine's player carries no unlock result (nothing is reported)");
            return;
        }

        var key = KeyOf(unlock);

        if (!unlock.Succeed)
        {
            FikaSentryAccess.Line("swipe-denied:" + reader.Id + ":" + key,
                "card=swiped role=" + FikaSentryAccess.RoleOf(player) + " state=card-reader applied=no side="
                + FikaSentryAccess.Side + " source=native-execute reader=" + reader.Id + " key=" + key
                + " -> result=the reader refused this card on this machine (nothing is reported)");
            return;
        }

        FikaSentryAccess.Line("swipe:" + reader.Id + ":" + key,
            "card=swiped role=" + FikaSentryAccess.RoleOf(player) + " state=card-reader applied=- side="
            + FikaSentryAccess.Side + " source=native-execute reader=" + reader.Id + " key=" + key
            + " profile=" + player.ProfileId
            + " -> result=the swipe of this machine's player was accepted by the reader; the fact goes to NoteNativeEmission");

        // (2026-10-21) Temporary [SyncDiag] line of this round: the one line that says the upward report exists.
        FikaGlobals.LogInfo("[SyncDiag] swipe-report profile=" + player.ProfileId,
            "Player_ExecuteInteraction_TerminalCard_Patch");

        FikaSentryAccess.NoteNativeEmission(player);
    }

    /// <summary>The card that was used - named in the lines only, never part of the report itself.</summary>
    private static string KeyOf(UnlockResult unlock)
    {
        try
        {
            var item = unlock?.Key?.Item;
            return item == null || string.IsNullOrEmpty(item.Id) ? "-" : item.Id;
        }
        catch (System.Exception)
        {
            return "-";
        }
    }
}

/// <summary>
/// The native write of the grant (also the machine that owns the card reader emits it here). Reports the
/// set the player was added to and, on the authority machine, replicates the fact.
/// </summary>
internal sealed class SentryBossLogic_OnTerminalAccessGained_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(SentryBossLogic)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(SentryBossLogic.OnTerminalAccessGained));
        if (target == null)
        {
            throw new MissingMethodException("SentryBossLogic.OnTerminalAccessGained(Player)");
        }

        return target;
    }

    [PatchPostfix]
    public static void Postfix(SentryBossLogic __instance, Player __0)
    {
        try
        {
            var count = -1;
            try { count = __instance?._accessedPlayerIds?.Count ?? -1; } catch (System.Exception) { }
            FikaSentryAccess.NoteNativeGrant(__0, count, "native");
        }
        catch (System.Exception)
        {
            // A diagnostic must never break the native chain that carries it.
        }
    }
}

/// <summary>
/// The native read of the grant: the one question <c>BossSentryHostilityRule</c> asks. Written only when
/// the answer would keep the player an enemy, so the log carries exactly the refusals the report is about
/// (one line per player on the host, and on a client the line that proves its sentry never learned the
/// fact).
/// </summary>
internal sealed class SentryBossLogic_HaveAccess_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(SentryBossLogic)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(SentryBossLogic.HaveAccess));
        if (target == null)
        {
            throw new MissingMethodException("SentryBossLogic.HaveAccess(Int32)");
        }

        return target;
    }

    [PatchPostfix]
    public static void Postfix(int __0, bool __result)
    {
        if (__result)
        {
            // The grant is in force on this machine: the first yes of a player is worth a line, the rest are
            // the per-frame noise the hostility recalculation would produce.
            if (FikaSentryAccess.NoteAccessYes(__0))
            {
                FikaSentryAccess.Line("haveaccess-yes:" + __0,
                    "card=swiped role=- state=verify applied=yes side=" + FikaSentryAccess.Side
                    + " raidId=" + __0 + " -> result=the sentry of this machine accepts the player");
            }

            return;
        }

        FikaSentryAccess.NoteAccessNo(__0);
    }
}

/// <summary>
/// THE RULE ITSELF: the verdict of the sentry rule of one player on this machine. This is the line the
/// report is read from - it names the player, the verdict and whether he carried anything, and it makes
/// 「the sentry of this machine refused an unarmed player」 a fact of the log instead of an impression.
/// One line per (player, verdict) per raid, because the hostility of a group is recalculated every frame.
/// </summary>
internal sealed class BossSentryHostilityRule_EvaluateHostility_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(BossSentryHostilityRule)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(BossSentryHostilityRule.EvaluateHostility));
        if (target == null)
        {
            throw new MissingMethodException("BossSentryHostilityRule.EvaluateHostility(BotGroupInnerHostilityData, PlayerHostilityData)");
        }

        return target;
    }

    [PatchPostfix]
    public static void Postfix(BossSentryHostilityRule __instance, BotGroupInnerHostilityData __0,
        PlayerHostilityData __1, BotHostilityStatus __result)
    {
        try
        {
            var iplayer = __1?.Player;
            if (iplayer == null || iplayer.IsAI)
            {
                return;
            }

            // PlayerHostilityData carries an IPlayer; the rule only matters for a human of this raid, and the
            // concrete player is what the hands of the reported case are read from.
            var player = iplayer.TryCast<Player>();
            if (player == null)
            {
                return;
            }

            var raidId = -1;
            try { raidId = player.RaidId; } catch (System.Exception) { }
            var verdict = __result.ToString();
            FikaSentryAccess.RuleVerdict(player, raidId, verdict);
        }
        catch (System.Exception)
        {
        }
    }
}

/// <summary>
/// The emission of <c>GlobalEventDispatcher.OnTerminalTransitAccessGained</c> - the moment the native
/// chain decides that a player has the access. A line here on the host named session says the card was
/// accepted by the authority; the r32 client run proved the line appears on a client too (the client runs
/// its own player's interaction locally), which is why this patch is also the routing point of the fact:
/// <c>FikaSentryAccess.NoteNativeEmission</c> reports it to the authority when this machine owns no bots.
/// </summary>
internal sealed class GlobalEventDispatcher_TerminalAccess_Emit_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var target = typeof(GlobalEventDispatcher)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == nameof(GlobalEventDispatcher.ApplyOnTerminalTransitAccessGained));
        if (target == null)
        {
            throw new MissingMethodException("GlobalEventDispatcher.ApplyOnTerminalTransitAccessGained(Player)");
        }

        return target;
    }

    [PatchPostfix]
    public static void Postfix(Player __0)
    {
        try
        {
            // The line and the routing both live in FikaSentryAccess: this patch stays a pure observation
            // point of the native chain, and the switch family of the component governs what happens next.
            FikaSentryAccess.NoteNativeEmission(__0);
        }
        catch (System.Exception)
        {
        }
    }
}
