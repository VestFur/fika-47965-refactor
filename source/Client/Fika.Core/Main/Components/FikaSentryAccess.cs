using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;

namespace Fika.Core.Main.Components;

/// <summary>
/// BJ (analysis/terminal-rauf-card): the access check of the sentries of the terminal (the Russian
/// soldiers at the transfer point, RAUF).
///
/// <para>NATIVE RULE - where it lives. The whole rule is a state of the sentry, not a state of the
/// player and not a state of a zone:</para>
/// <list type="number">
/// <item><c>GlobalEventDispatcher.OnTerminalTransitAccessGained</c> is the event of 「this player has
/// passed the card check」; its only consumer is <c>SentryBossLogic.OnTerminalAccessGained(Player)</c>
/// (<c>SentryBossLogic.cs</c>, interop :31193), which writes the player id into the private
/// <c>HashSet&lt;Int32&gt; _accessedPlayerIds</c>;</item>
/// <item><c>SentryBossLogic : ABossLogic, IAccessBossLogic</c> exposes exactly one question -
/// <c>HaveAccess(Int32 playerId)</c> (<c>IAccessBossLogic.cs</c>) - answered from that set;</item>
/// <item>the hostility of a bot group is recomputed on the machine itself:
/// <c>BotGroupHostility.UpdateHostilityStatusForAllPlayers()</c> (native, per frame) ->
/// <c>BotHostilityService.EvaluateHostility</c> -> the rule list, which contains
/// <c>BossSentryHostilityRule : IHostilityRulePlayer</c> -> <c>IAccessBossLogic.HaveAccess</c>
/// (<c>BossSentryHostilityRule.cs</c>, interop :70175).</item>
/// </list>
/// <para>So the emission belongs to the machine that runs the interaction and the evaluation belongs to
/// the machine that owns the bot - and on a Fika raid those are two different machines. What the r32 run
/// of this port measured (analysis/terminal-rauf-hostility-2) refines the first reading of the symptom:</para>
/// <list type="bullet">
/// <item>a client DOES raise the native event, for its own player (its local interaction runs natively:
/// 「the native event was raised on this machine」 appears in the client log with <c>sentries=0</c>);</item>
/// <item>a client holds no sentry at all - no <c>BotOwner</c>, no <c>BotBoss</c>, no
/// <c>SentryBossLogic</c>, no bot group (it runs no <c>BotsController</c> and spawns every remote character
/// as an <c>ObservedPlayer</c>), so nothing on that machine evaluates the sentry rule in the first place;</item>
/// <item>the machine that does evaluate it - the authority - was never told about the client's player.</item>
/// </list>
/// <para>So the break is not 「the client's set stays empty」, it is 「the fact never travels to the machine
/// whose sentry decides」 - and that is the reported symptom.</para>
///
/// <para>THE REPAIR - the native shape, one hop wider than native. The grant is taken where the native
/// chain makes it and applied to the sentries of every machine through the same native entry point
/// (<c>SentryBossLogic.OnTerminalAccessGained</c>). No rule, no hostility list and no bot state is
/// written from here: only the one fact the native event carries, on the machines the event does not
/// reach on its own.</para>
///
/// <para>(analysis/terminal-rauf-hostility-2) A machine in a Fika raid only owns bots when it is the
/// authority (<see cref="IsAuthority"/>); a client runs no <c>BotsController</c> at all and holds every
/// remote character - bots included - as an <c>ObservedPlayer</c> without a <c>BotOwner</c>. Such a machine
/// therefore has no sentry to write to, in any of the two landings; what it does have is the native event
/// itself, which it raises for its own player. So the fact is completed in two directions:</para>
/// <list type="bullet">
/// <item>UPWARD (client -> authority): <see cref="NoteNativeEmission"/> reports a native emission of a
/// non-authority machine to the host, which owns the bot simulation, applies the fact to its own sentries
/// and hands it back out. This is the hop that decides the reported case - 「the client swiped, the RAUF
/// kept shooting」 - because the shooting is decided on the authority;</item>
/// <item>SIDEWAYS (two landings on the machine that applies): the native entry point first, and - only when
/// this machine holds no sentry boss logic at all - the native container the group's hostility data keeps
/// for exactly this fact, <c>BotGroupInnerHostilityData.AlliesBySentryAcces</c>, of the groups of this
/// machine's sentry bots. Nothing else is written: no rule, no threshold, no enemy list.</item>
/// </list>
///
/// <para>DIAGNOSIS - one grep, the lines the brief asks for:</para>
/// <code>
/// [SentryDiag] card=swiped role=assault state=access-gained applied=yes side=client sentries=0 profile=6acdcb85 raidId=2 -> result=the native event was raised on this machine
/// [SentryDiag] card=swiped role=assault state=access-gained applied=yes side=client granted=yes profile=6acdcb85 raidId=2 -> result=reported to the authority (this machine owns no bots)
/// [SentryDiag] card=swiped role=assault state=applied applied=yes side=host source=packet-from-client via=native-sentry sentries=4/4 allies=0 profile=6acdcb85 -> result=the sentries of this machine now accept the player
/// [SentryDiag] card=swiped role=assault state=verify applied=yes side=host source=packet-from-client sentries=4 haveAccess=yes:4/no:0 -> result=this machine's sentry accepts the player
/// [SentryDiag] card=swiped role=assault state=applied applied=yes side=host source=packet-from-client via=hostility-data sentries=0/0 allies=3 -> result=the sentry groups of this machine now hold the player as an ally by sentry access
/// [SentryDiag] card=swiped role=assault state=applied applied=no side=client source=packet-from-host via=none sentries=0/0 allies=0 -> result=no-sentry-and-no-group-on-this-machine
/// </code>
/// <para>One switch reverts the behaviour (<see cref="RepairEnabled"/>), see the report for the one-line
/// rollback.</para>
/// </summary>
internal static class FikaSentryAccess
{
    /// <summary>
    /// Master switch of the repair. OFF = the port behaves exactly as before: the lines are still written
    /// (they are the evidence), the grant is never replicated. One line to revert - one field, no config
    /// file, no file edits (the main agent may bind it to a config entry later).
    /// </summary>
    internal static bool RepairEnabled = true;

    /// <summary>
    /// (analysis/terminal-rauf-hostility-2) The second landing of the same fact: the native container the
    /// group's hostility data keeps for exactly this fact -
    /// <c>BotGroupInnerHostilityData.AlliesBySentryAcces</c> - written only for the groups of this machine's
    /// sentry bots, and only when the native entry point had no object to run on. OFF = case (2) falls back
    /// to the previous behaviour (the refusal is still written).
    /// </summary>
    internal static bool HostilityDataEnabled = true;

    /// <summary>
    /// (analysis/terminal-rauf-hostility-2) The upward hop: a machine that owns no bot simulation reports the
    /// native emission of its own player to the authority, which owns the bots and therefore the answer.
    /// OFF = a client's swipe stays on the client (the previous behaviour).
    /// </summary>
    internal static bool RelayToAuthorityEnabled = true;

    /// <summary>The landing a line names: the native entry point of a sentry of this machine.</summary>
    internal const string ViaNativeSentry = "native-sentry";
    /// <summary>The landing a line names: the group hostility data of this machine's sentry bots.</summary>
    internal const string ViaHostilityData = "hostility-data";
    /// <summary>Neither landing exists on this machine (it owns no bot at all - the client case).</summary>
    internal const string ViaNone = "none";

    /// <summary>Upper bound of the verbatim lines of this family per raid; the rest is counted.</summary>
    private const int MaxLines = 400;

    private static readonly HashSet<string> Written = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Granted = new(StringComparer.Ordinal);

    /// <summary>
    /// Players whose grant the native entry point of THIS machine already applied (the sentry postfix ran).
    /// It is what keeps the emission half from applying the same fact twice on the authority.
    /// </summary>
    private static readonly HashSet<string> NativeApplied = new(StringComparer.Ordinal);
    private static readonly HashSet<int> AccessYes = new();
    private static readonly Dictionary<int, int> AccessNo = new();
    private static int _suppressed;

    /// <summary>
    /// BT (analysis/terminal-hostility-graph): the last verdict this AUTHORITY machine read back from its own
    /// native state (<see cref="Verify"/>). The authority echoes it to the reporting client, so the client log
    /// carries the answer of the machine that owns the bots without that machine's log.
    /// </summary>
    private static int _lastVerifyYes;
    private static int _lastVerifyNo;
    private static int _lastVerifyAllies;

    /// <summary>host / client / headless / single - the vocabulary of this port.</summary>
    internal static string Side => FikaRaidGate.Side;

    /// <summary>True on the machine that owns the bot simulation and therefore the emission.</summary>
    internal static bool IsAuthority => FikaBackendUtils.IsServer || FikaBackendUtils.IsHeadless;

    internal static void Reset()
    {
        lock (Written)
        {
            Written.Clear();
            Granted.Clear();
            NativeApplied.Clear();
            AccessYes.Clear();
            AccessNo.Clear();
            _suppressed = 0;
        }

        // BT (analysis/terminal-hostility-graph): the fan-out family is per raid too.
        FikaTerminalHostility.Reset();
    }

    /// <summary>
    /// The native entry point itself (<c>SentryBossLogic.OnTerminalAccessGained</c>) fired on this
    /// machine. Called from the patch on every machine that holds a sentry boss logic: the host reaches
    /// it through the native event, a client through the replica of this class.
    /// </summary>
    internal static void NoteNativeGrant(Player player, int accessedCount, string source)
    {
        var profileId = ProfileIdOf(player);
        var state = source == "native" ? "verified" : "applied";
        Line("grant:" + profileId + ":" + source, "card=swiped role=" + RoleOf(player)
            + " state=access-" + (state == "verified" ? "gained" : "applied")
            + " applied=yes side=" + Side
            + " sentries=" + CountLocalSentries()
            + " accessed=" + accessedCount
            + " profile=" + Short(profileId)
            + " -> result=" + (state == "verified" ? "sentry now treats this player as accessed" : "native entry point ran"));

        if (source != "native" || string.IsNullOrEmpty(profileId))
        {
            return;
        }

        // This machine's own sentries hold the fact through the native method now: the emission half must
        // not apply it a second time (see NoteNativeEmission).
        lock (Written)
        {
            NativeApplied.Add(profileId);
        }

        // The emission half: only the authority machine asks the others to apply the same fact, and it
        // asks once per player.
        if (IsAuthority && Remember(profileId))
        {
            Broadcast(player, profileId, true);
        }
    }

    /// <summary>
    /// The third native surface of the same rule: the machine raised the native event
    /// (<c>GlobalEventDispatcher.ApplyOnTerminalTransitAccessGained</c>) for a player. Every machine raises
    /// it for the player whose interaction it runs - including a client, which runs the interaction of its
    /// own player locally (analysis/terminal-rauf-hostility-2, the r32 line
    /// 「the native event was raised on this machine」 of the client).
    ///
    /// <para>What is missing is not the emission, it is the direction: a machine that owns no bot simulation
    /// - a Fika client - has nothing to apply the fact to, and the machine that decides (the authority,
    /// which simulates every bot) never hears about it. So this method only routes the fact: a client
    /// reports it to the authority once per player; the authority applies it (when the native subscribers
    /// of its own sentries did not: no sentry instance) and hands it to the peers.</para>
    /// </summary>
    internal static void NoteNativeEmission(Player player)
    {
        var profileId = ProfileIdOf(player);

        Line("emit:" + profileId,
            "card=swiped role=" + RoleOf(player)
            + " state=access-gained applied=yes side=" + Side
            + " sentries=" + CountLocalSentries()
            + " profile=" + profileId + " raidId=" + SafeRaidId(player)
            + " -> result=the native event was raised on this machine");

        if (!RepairEnabled || string.IsNullOrEmpty(profileId))
        {
            return;
        }

        // BT: the machine that ran the interaction hands the same fact to the OTHER systems that can fire on
        // this player here (the code-driven zones of its own scene included) - the sentry entry above is not
        // the only reader of 「this player has the access」.
        FikaTerminalHostility.Deliver(player, "native-event", true);

        var nativeHere = false;
        lock (Written)
        {
            nativeHere = NativeApplied.Contains(profileId);
        }

        if (!IsAuthority)
        {
            if (!RelayToAuthorityEnabled)
            {
                Line("emit:relay-off:" + Short(profileId),
                    "card=swiped role=" + RoleOf(player) + " state=access-gained applied=yes side=" + Side
                    + " profile=" + Short(profileId) + " raidId=" + SafeRaidId(player)
                    + " -> result=relay-off (FikaSentryAccess.RelayToAuthorityEnabled=false): this machine owns no bot and the authority is not told");
                return;
            }

            // A client owns no bots at all: the fact is worth nothing here, and the authority owns the
            // answer. One report per player per raid.
            if (Remember(profileId))
            {
                Broadcast(player, profileId, true, "reported to the authority (this machine owns no bots)");
            }

            return;
        }

        if (nativeHere)
        {
            // The sentry postfix of this machine already applied the fact (and handed it out): the native
            // entry did the work, this method adds nothing.
            return;
        }

        // The authority raised the event but no sentry of its own consumed it (it holds no sentry boss
        // logic right now, so there is no machine-local object the native entry could have run on): apply
        // it where the native chain of this machine cannot reach - the emission is the fact - and hand it
        // to the peers. Applying is idempotent and the hand-out happens once per player.
        Apply(player, true, "native-event");
        if (Remember(profileId))
        {
            Broadcast(player, profileId, true, "handed to the peers (this machine raised the event without a sentry of its own)");
        }
    }

    /// <summary>
    /// A machine reports the fact of <paramref name="profileId"/> to this machine. On the host this is a
    /// client that ran the native chain locally (it owns its player's interaction); it is applied and
    /// handed to the other peers. On a client this is the host's replica and is applied locally.
    /// </summary>
    internal static void Receive(string profileId, int raidId, bool granted, bool fromClient,
        int hostAccessYes = 0, int hostAccessNo = 0, int hostAllies = 0)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            Line("recv:empty", "card=swiped role=- state=applied applied=no side=" + Side
                + " -> result=empty-profile-id");
            return;
        }

        var world = Singleton<GameWorld>.Instance;
        var player = world != null ? world.GetAlivePlayerByProfileID(profileId) : null;
        if (player == null)
        {
            Line("recv:unresolved:" + profileId, "card=swiped role=- state=applied applied=no side=" + Side
                + " profile=" + Short(profileId) + " raidId=" + raidId
                + " -> result=player-not-in-this-world (nothing applied; the state is re-applied on the next report)");
            return;
        }

        // The judgement a client can make on its own: when the fact that arrives is the fact THIS machine
        // reported, the packet can only come from the authority, and the authority only hands it out after
        // its own sentries (or their groups) took it - so this line is the authority's confirmation, read
        // without the authority's log. Before this round a client never received such a packet about its own
        // profile at all (r32: the only packet-from-host line named the host's player).
        if (!IsAuthority && !fromClient && IsLocalProfile(world, profileId))
        {
            // BT (analysis/terminal-hostility-graph): the resourceful part of the echo - the authority now
            // carries its own read back (its sentry answered yes/no for how many objects), so the client log
            // holds the answer of the machine that owns the bots without that machine's log.
            var authorityAnswered = hostAccessYes + hostAccessNo > 0;
            Line("confirm:" + Short(profileId) + ":" + granted + ":" + Short(Side),
                "card=swiped role=" + RoleOf(player) + " state=confirm applied=- side=" + Side
                + " source=packet-from-host via=authority-echo"
                + " granted=" + (granted ? "yes" : "no")
                + " hostSentryAccess=yes:" + hostAccessYes + "/no:" + hostAccessNo
                + " hostAlliesByData=" + hostAllies
                + " profile=" + Short(profileId) + " raidId=" + raidId
                + " -> result=" + (authorityAnswered && hostAccessYes == 0
                    ? "the authority answered this machine's own report, but its own sentry still refuses the player (the native entry had no sentry object there: the group container is the landing to read on that machine)"
                    : "the authority answered this machine's own report (the authority only echoes a fact its own sentries took), so the swipe is in force where the bots live"));
        }

        var applied = Apply(player, granted, fromClient ? "packet-from-client" : "packet-from-host");
        if (IsAuthority && fromClient && applied > 0 && Remember(profileId))
        {
            // A client ran the native chain on its own world; the host owns the answer, so the other peers
            // get the same line from here instead of from the client.
            Broadcast(player, profileId, granted);
        }
    }

    /// <summary>True when the profile id of a report is the profile id of the local player of this machine.</summary>
    private static bool IsLocalProfile(GameWorld world, string profileId)
    {
        try
        {
            return world != null && world.MainPlayer != null && world.MainPlayer.ProfileId == profileId;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The single application point: the private native state is written through the native entry point
    /// (<c>SentryBossLogic.OnTerminalAccessGained</c>), so the rules that read it
    /// (<c>BossSentryHostilityRule</c>) cannot tell this grant from the native one.
    /// </summary>
    internal static int Apply(Player player, bool granted, string source)
    {
        if (player == null)
        {
            return 0;
        }

        if (!RepairEnabled)
        {
            Line("apply:off:" + ProfileIdOf(player), "card=swiped role=" + RoleOf(player)
                + " state=applied applied=no side=" + Side + " source=" + source
                + " -> result=repair-off (FikaSentryAccess.RepairEnabled=false)");
            return 0;
        }

        var applied = 0;
        var considered = 0;
        var allies = 0;
        var sentryBots = 0;
        try
        {
            var owners = LocalBotOwners();
            for (var i = 0; i < owners.Count; i++)
            {
                var logic = SentryLogicOf(owners[i]);
                if (logic == null)
                {
                    continue;
                }

                considered++;
                if (!granted)
                {
                    continue;
                }

                try
                {
                    // Native path: the same method the native event calls.
                    logic.OnTerminalAccessGained(player);
                    applied++;
                }
                catch (Exception exception)
                {
                    Line("apply:error:" + Short(ProfileIdOf(player)) + ":" + considered,
                        "card=swiped role=" + RoleOf(player) + " state=applied applied=no side=" + Side
                        + " source=" + source + " -> result=native-threw "
                        + exception.GetType().Name);
                }
            }
        }
        catch (Exception exception)
        {
            Line("apply:scan-error", "card=swiped role=- state=applied applied=no side=" + Side
                + " source=" + source + " -> result=scan-threw " + exception.GetType().Name);
            return 0;
        }

        // THE SECOND LANDING (analysis/terminal-rauf-hostility-2). The first landing has no object on a
        // machine that holds no sentry boss logic - which is every Fika client, and an authority whose
        // sentries are not spawned. The same fact exists a second time in the native state, one level up:
        // the hostility data of the group carries the container the sentry access is expressed in
        // (BotGroupInnerHostilityData.AlliesBySentryAcces, a set of player ids - the SAME id space as
        // SentryBossLogic._accessedPlayerIds). It is written only for the groups of this machine's sentry
        // bots, and only when the native entry point could not be used at all; nothing else of the group is
        // touched (no enemy list, no rule, no threshold).
        var via = applied > 0 ? ViaNativeSentry : ViaNone;
        if (granted && applied == 0 && HostilityDataEnabled)
        {
            try
            {
                ApplyToHostilityData(player, out sentryBots, out allies);
                if (allies > 0)
                {
                    via = ViaHostilityData;
                }
            }
            catch (Exception exception)
            {
                Line("apply:data-error:" + Short(ProfileIdOf(player)),
                    "card=swiped role=" + RoleOf(player) + " state=applied applied=no side=" + Side
                    + " source=" + source + " via=" + ViaNone
                    + " -> result=hostility-data-threw " + exception.GetType().Name);
            }
        }
        else if (granted && applied == 0 && !HostilityDataEnabled)
        {
            via = "none (HostilityDataEnabled=false)";
        }

        var landed = applied > 0 || allies > 0;
        var verdict = applied > 0
            ? "the sentries of this machine now accept the player"
            : allies > 0
                ? "the sentry groups of this machine now hold the player as an ally by sentry access"
                : considered > 0 ? "no-sentry-this-machine"
                : sentryBots > 0 ? "no-sentry-boss-logic"
                : "no-sentry-and-no-group-on-this-machine";
        Line("applied:" + Short(ProfileIdOf(player)) + ":" + source + ":" + considered,
            "card=swiped role=" + RoleOf(player) + " state=applied applied=" + (landed ? "yes" : "no")
            + " side=" + Side + " source=" + source + " via=" + via
            + " sentries=" + applied + "/" + considered
            + " allies=" + allies + " sentryBots=" + sentryBots
            + " profile=" + Short(ProfileIdOf(player)) + " -> result=" + verdict);

        // The check the report is judged by: did the sentries of THIS machine now answer the access
        // question with yes, and does its sentry groups' own container hold the player? Read through the
        // native members themselves, not through our own bookkeeping.
        Verify(player, source, considered, allies, sentryBots);

        // BT: the single application point of the fact is also the fan-out point - the sentry (above), the
        // group containers (above) and the systems that own no container of ours (the code-driven zones of
        // this machine, the group tables of the bots of this machine).
        FikaTerminalHostility.Deliver(player, source, granted);

        return applied > 0 ? applied : allies;
    }

    /// <summary>
    /// The verdict line: the native question <c>IAccessBossLogic.HaveAccess(playerId)</c> asked again
    /// after the application, on this machine - and, for the second landing, the native container of the
    /// sentry groups of this machine asked the same question. A yes in either column is the proof that
    /// this machine's sentry will stop treating the player as an enemy in the next hostility
    /// recalculation.
    /// </summary>
    private static void Verify(Player player, string source, int considered, int allies, int sentryBots)
    {
        var raidId = -1;
        var yes = 0;
        var no = 0;
        var dataYes = 0;
        var dataNo = 0;
        try
        {
            raidId = SafeRaidId(player);
            var owners = LocalBotOwners();
            for (var i = 0; i < owners.Count; i++)
            {
                var logic = SentryLogicOf(owners[i]);
                if (logic == null)
                {
                    continue;
                }

                if (logic.HaveAccess(raidId))
                {
                    yes++;
                }
                else
                {
                    no++;
                }
            }

            var sentries = SentryRoleOwners();
            for (var i = 0; i < sentries.Count; i++)
            {
                var state = ByData(sentries[i], raidId, false);
                if (state == 1)
                {
                    dataYes++;
                }
                else if (state == 0)
                {
                    dataNo++;
                }
            }
        }
        catch (Exception exception)
        {
            Line("verify:error:" + Short(ProfileIdOf(player)),
                "card=swiped role=" + RoleOf(player) + " state=verify applied=- side=" + Side
                + " source=" + source + " -> result=native-threw " + exception.GetType().Name);
            return;
        }

        var verdict = yes > 0
            ? "this machine's sentry accepts the player"
            : dataYes > 0
                ? "this machine's sentry groups hold the player as an ally by sentry access"
                : "this machine's sentry still refuses";
        lock (Written)
        {
            _lastVerifyYes = yes;
            _lastVerifyNo = no;
            _lastVerifyAllies = dataYes;
        }

        Line("verify:" + Short(ProfileIdOf(player)) + ":" + source + ":" + yes + ":" + no,
            "card=swiped role=" + RoleOf(player) + " state=verify applied=" + (yes > 0 || dataYes > 0 ? "yes" : "no")
            + " side=" + Side + " source=" + source + " sentries=" + considered
            + " haveAccess=yes:" + yes + "/no:" + no
            + " alliesByData=yes:" + dataYes + "/no:" + dataNo
            + " allies=" + allies + " sentryBots=" + sentryBots
            + " raidId=" + raidId
            + " -> result=" + verdict);
    }

    /// <summary>
    /// The sentry bots of this machine, by their native role (<c>BotOwner.Role</c> =
    /// <c>WildSpawnType.sentry</c>, the role of the RAUF of the terminal). A machine that owns no bots -
    /// a Fika client - has none, and every line of the family then says so.
    /// </summary>
    private static List<BotOwner> SentryRoleOwners()
    {
        var result = new List<BotOwner>();
        var owners = LocalBotOwners();
        for (var i = 0; i < owners.Count; i++)
        {
            try
            {
                if (owners[i].Role == WildSpawnType.sentry)
                {
                    result.Add(owners[i]);
                }
            }
            catch (Exception)
            {
                // A bot whose role cannot be read is not part of the question.
            }
        }

        return result;
    }

    /// <summary>
    /// One question to the native state of the second landing, the same fact one level up from the sentry:
    /// <c>BotOwner.BotsGroup</c> -> <c>BotsGroup.BotGroupHostility</c> ->
    /// <c>BotGroupHostility.InnerHostilityData</c> -> <c>BotGroupInnerHostilityData.AlliesBySentryAcces</c>.
    /// All four are native members of this build (interop face: <c>get_BotsGroup</c>,
    /// <c>get_BotGroupHostility</c>, <c>get_InnerHostilityData</c>, <c>get_AlliesBySentryAcces</c>), and the
    /// container is the id set the sentry access is expressed in - the same id space as
    /// <c>SentryBossLogic._accessedPlayerIds</c>.
    ///
    /// <para>Returns -1 when this bot carries no such container on this machine (an observed bot of a
    /// client has no group at all), 0 when the container exists without the player, 1 when it holds him.
    /// With <paramref name="write"/> the missing id is added and the answer is read back through the same
    /// native getter, so a write that did not land reports 0.</para>
    /// </summary>
    private static int ByData(BotOwner owner, int raidId, bool write)
    {
        try
        {
            var group = owner?.BotsGroup;
            if (group == null)
            {
                return -1;
            }

            var hostility = group.BotGroupHostility;
            if (hostility == null)
            {
                return -1;
            }

            var inner = hostility.InnerHostilityData;
            if (inner == null)
            {
                return -1;
            }

            var allies = inner.AlliesBySentryAcces;
            if (allies == null)
            {
                return -1;
            }

            if (write && !allies.Contains(raidId))
            {
                allies.Add(raidId);
            }

            return allies.Contains(raidId) ? 1 : 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// The write of the second landing: the player id of this machine's player object goes into the
    /// container of every sentry bot's group of this machine. Nothing else of the group is touched.
    /// </summary>
    private static void ApplyToHostilityData(Player player, out int sentryBots, out int allies)
    {
        sentryBots = 0;
        allies = 0;

        var raidId = SafeRaidId(player);
        if (raidId < 0)
        {
            return;
        }

        var owners = SentryRoleOwners();
        sentryBots = owners.Count;
        for (var i = 0; i < owners.Count; i++)
        {
            if (ByData(owners[i], raidId, true) == 1)
            {
                allies++;
            }
        }
    }

    /// <summary>
    /// Hands the fact to every other machine. The host broadcasts to its peers; a client sends it to the
    /// host (the host then owns the answer and hands it to the rest).
    /// </summary>
    private static void Broadcast(Player player, string profileId, bool granted, string verdict = null)
    {
        try
        {
            var manager = FikaSingleton<IFikaNetworkManager>.Instance;
            if (manager == null)
            {
                Line("broadcast:none:" + Short(profileId), "card=swiped role=" + RoleOf(player)
                    + " state=access-gained applied=no side=" + Side + " -> result=no-network-manager");
                return;
            }

            var packet = new SentryAccessPacket
            {
                ProfileId = profileId,
                RaidId = SafeRaidId(player),
                Granted = granted,
                FromClient = (byte)(FikaBackendUtils.IsClient ? 1 : 0),
                // BT: only the authority holds the bots and therefore the verdict; a client reports zeros and
                // the authority fills the echo it sends back.
                HostAccessYes = IsAuthority ? _lastVerifyYes : 0,
                HostAccessNo = IsAuthority ? _lastVerifyNo : 0,
                HostAllies = IsAuthority ? _lastVerifyAllies : 0
            };

            manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
            Line("broadcast:" + Short(profileId) + ":" + Short(Side) + ":" + (verdict ?? "broadcast"),
                "card=swiped role=" + RoleOf(player) + " state=access-gained applied=yes side=" + Side
                + " granted=" + (granted ? "yes" : "no") + " profile=" + Short(profileId)
                + " raidId=" + packet.RaidId
                + " -> result=" + (verdict ?? "broadcast"));
        }
        catch (Exception exception)
        {
            Line("broadcast:error:" + Short(profileId), "card=swiped role=" + RoleOf(player)
                + " state=access-gained applied=no side=" + Side
                + " -> result=broadcast-threw " + exception.GetType().Name);
        }
    }

    /// <summary>
    /// Every bot owner of this machine. The client and the host both hold their bots in the world's
    /// player list, and a bot carries its owner on <c>Player.AIData.BotOwner</c>.
    /// </summary>
    private static List<BotOwner> LocalBotOwners()
    {
        var result = new List<BotOwner>();
        var world = Singleton<GameWorld>.Instance;
        var list = world?.AllAlivePlayersList;
        if (list == null)
        {
            return result;
        }

        for (var i = list.Count - 1; i >= 0; i--)
        {
            try
            {
                var player = list[i];
                if (player == null)
                {
                    continue;
                }

                var ai = player.AIData;
                if (ai == null || !ai.IsAI)
                {
                    continue;
                }

                var owner = ai.BotOwner;
                if (owner != null)
                {
                    result.Add(owner);
                }
            }
            catch (Exception)
            {
                // A half-spawned player is not part of the question.
            }
        }

        return result;
    }

    /// <summary>
    /// The sentry boss logic of one bot, or null when this bot is not a sentry. <c>BotBoss.BossLogic</c>
    /// is where the game keeps it (<c>BotOwner.Boss.BossLogic</c>), and the sentry is the only boss logic
    /// that implements <c>IAccessBossLogic</c>.
    /// </summary>
    private static SentryBossLogic SentryLogicOf(BotOwner owner)
    {
        try
        {
            var boss = owner?.Boss;
            if (boss == null)
            {
                return null;
            }

            var logic = boss.BossLogic;
            if (logic == null)
            {
                return null;
            }

            return logic.TryCast<SentryBossLogic>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>How many sentry boss logics this machine holds (named in every line of the family).</summary>
    internal static int CountLocalSentries()
    {
        try
        {
            var owners = LocalBotOwners();
            var count = 0;
            for (var i = 0; i < owners.Count; i++)
            {
                if (SentryLogicOf(owners[i]) != null)
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>First yes of a player on this machine (the line worth writing).</summary>
    internal static bool NoteAccessYes(int raidId)
    {
        lock (Written)
        {
            return AccessYes.Add(raidId);
        }
    }

    /// <summary>
    /// A refusal of the native question. It is the default answer of a machine whose sentry never saw the
    /// grant, so it is counted and written once per player with a bound - the first line is the evidence,
    /// the rest is the per-frame recalculation.
    /// </summary>
    internal static void NoteAccessNo(int raidId)
    {
        int count;
        bool first;
        lock (Written)
        {
            AccessNo.TryGetValue(raidId, out count);
            count++;
            AccessNo[raidId] = count;
            first = count == 1;
        }

        if (!first || !IsAuthority)
        {
            return;
        }

        Line("haveaccess-no:" + raidId,
            "card=swiped role=- state=verify applied=no side=" + Side + " raidId=" + raidId
            + " sentries=" + CountLocalSentries()
            + " -> result=the sentry of this machine still refuses this player");
    }

    /// <summary>
    /// The verdict of one calculation of the sentry rule (called by the EvaluateHostility patch). The two
    /// interesting cases are written; everything else is counted by the deduplicating writer, so a raid
    /// costs a handful of lines whatever the frame rate.
    /// </summary>
    internal static void RuleVerdict(Player player, int raidId, string verdict)
    {
        var unarmed = IsUnarmed(player);
        if (verdict == "Enemy")
        {
            Line("rule-enemy:" + raidId + ":" + (unarmed ? "unarmed" : "armed"),
                "card=unknown role=" + RoleOf(player) + " state=verify applied=no side=" + Side
                + " verdict=Enemy target=player raidId=" + raidId
                + " hands=" + (unarmed ? "empty" : "occupied")
                + " sentries=" + CountLocalSentries()
                + " -> result=this machine's sentry treats the player as an enemy"
                + (unarmed ? " and the player carries nothing (the reported case)" : string.Empty));
            return;
        }

        if (verdict == "Neutral" || verdict == "Ally")
        {
            Line("rule-ok:" + raidId + ":" + verdict,
                "card=swiped role=" + RoleOf(player) + " state=verify applied=yes side=" + Side
                + " verdict=" + verdict + " target=player raidId=" + raidId
                + " hands=" + (unarmed ? "empty" : "occupied")
                + " sentries=" + CountLocalSentries()
                + " -> result=this machine's sentry does not attack this player");
        }
    }

    private static bool Remember(string profileId)
    {
        lock (Written)
        {
            return Granted.Add(profileId);
        }
    }

    private static int SafeRaidId(Player player)
    {
        try
        {
            return player?.RaidId ?? -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string ProfileIdOf(Player player)
    {
        try
        {
            return player?.ProfileId ?? "-";
        }
        catch (Exception)
        {
            return "-";
        }
    }

    /// <summary>The native role name of a player (the RAUF sentries are bots - their role is the proof).</summary>
    /// <summary>
    /// True while the player holds nothing (the state the reported rule is about: a player who passed the
    /// card check is not attacked as long as he carries no weapon).
    /// </summary>
    internal static bool IsUnarmed(Player player)
    {
        try
        {
            var hands = player?.HandsController;
            if (hands == null)
            {
                return true;
            }

            return hands.TryCast<Player.EmptyHandsController>() != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The native role name of a player (the RAUF sentries are bots - their role is the proof).</summary>
    internal static string RoleOf(Player player)
    {
        try
        {
            if (player == null || player.Profile?.Info?.Settings == null)
            {
                return "-";
            }

            return player.Profile.Info.Settings.Role.ToString();
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string Short(string value) =>
        string.IsNullOrEmpty(value) ? "-" : value.Length > 8 ? value.Substring(0, 8) : value;

    /// <summary>
    /// The family writer: bounded, deduplicated, never throws into a native path. It is independent of
    /// 'Coop / Diagnostics' because it is the evidence of the repair itself, and it writes through the
    /// normal Fika logger - so one grep in LogOutput.log and one in Fika-diagnostics.log both work.
    /// </summary>
    internal static void Line(string key, string text)
    {
        try
        {
            lock (Written)
            {
                if (Written.Count >= MaxLines || !Written.Add(key))
                {
                    _suppressed++;
                    return;
                }
            }

            FikaGlobals.LogInfo("[SentryDiag] " + text, "FikaSentryAccess");
        }
        catch (Exception)
        {
            // A diagnostic must never be the reason a raid breaks.
        }
    }
}
