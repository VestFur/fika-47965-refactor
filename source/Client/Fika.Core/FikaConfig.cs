using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;


#if GOLDMASTER
using Fika.Core.UI; 
#endif
using Fika.Core.UI.Patches;
using static Fika.Core.FikaPlugin;
using static Fika.Core.Networking.IFikaNetworkManager;

namespace Fika.Core;

public sealed class FikaConfig(ConfigFile config)
{
    private readonly ConfigFile _config = config;

    #region config values

    // Hidden
    public ConfigEntry<string> LastVersion { get; set; }

    //Advanced
    public ConfigEntry<bool> OfficialVersion { get; set; }
    public ConfigEntry<bool> DevMode { get; set; }
    public ConfigEntry<bool> NoAI { get; set; }
    public ConfigEntry<bool> NoLoot { get; set; }
    public ConfigEntry<ELoadPriority> LoadPriority { get; set; }
    public ConfigEntry<int> MaxBundleLock { get; set; }

    // Coop
    public ConfigEntry<bool> UseHeadlessIfAvailable { get; set; }
    public ConfigEntry<bool> ShowNotifications { get; set; }
    public ConfigEntry<bool> AutoExtract { get; set; }
    public ConfigEntry<bool> ShowExtractMessage { get; set; }
    public ConfigEntry<KeyboardShortcut> ExtractKey { get; set; }
    public ConfigEntry<bool> ShowPlayerList { get; set; }
    public ConfigEntry<bool> EnableChat { get; set; }
    public ConfigEntry<KeyboardShortcut> ChatKey { get; set; }
    public ConfigEntry<bool> EnableOnlinePlayers { get; set; }

    // Coop | flare exfiltration points (the exits that need a green signal flare).
    // On (default) preserves the vanilla party channel: one flare opens the point for the whole
    // squad that fired it (EZoneEventType.PlayerByPartyAddedInShotList). Off binds the activation
    // to the shooter alone, so every player has to fire their own flare.
    public ConfigEntry<bool> FlareExtractSquadSharesActivation { get; set; }

    public ConfigEntry<bool> FlareReplicateExitVisual { get; set; }

    // Coop | display time of the flare-success banner ("Flare signal received. You can extract
    // safely!" / CN "道路已开放"). The native collector takes its own copy of that notification down
    // after its own wait; a banner Fika shows belongs to no controller, so it is hidden here instead
    // of staying on screen until the raid ends. Read per popup, so the switch is hot.
    public ConfigEntry<float> FlareExfilFeedbackSeconds { get; set; }

    // Coop | maps whose clock time the game itself pins (analysis/map-fixed-time). The native
    // matchmaker panel carries one static "<map>FixedTime" value per such map - Icebreaker is the
    // installed 5.0 case - and the live game therefore offers no time choice there. Fika never runs
    // that panel, so the raid used the player's raid settings instead (usually the -1 "follow the
    // world clock" the raid settings screen leaves behind). On (default): the raid starts at the
    // map's own time. Off: the player's own setting, i.e. the behaviour before the fix. Read when a
    // raid is created, so a change applies to the next raid without a restart.
    public ConfigEntry<bool> HonorMapFixedTime { get; set; }

    // Coop | transit start gate. On (default, requested by the host): the countdown only starts when every
    // teammate has interacted with the point and is standing inside it. Off: the host starts it as soon as
    // its own player interacted and every alive human player is inside the point.
    public ConfigEntry<bool> TransitRequireAllTeammates { get; set; }
    public ConfigEntry<int> TransitCountdownFallbackSeconds { get; set; }

    // Coop | the 2026-10-12 Factory case: a transit point that shares its trigger with an exfiltration point can
    // have its native transfer leg torn down while Fika's countdown keeps running, so the countdown finishes and
    // nothing happens. On (default): the co-located exits are suppressed from the moment the transit interaction
    // is accepted until it finishes or is aborted. Off: log only, no action (one flip = the old behaviour).
    public ConfigEntry<bool> TransitSuppressCoLocatedExits { get; set; }

    // Coop | what the countdown does when the set of participants changes (2026-10-12, requested by the host):
    // someone stepping out of the point cancels the countdown and the whole transfer. A death drops that
    // player and lets the rest continue (the native shape), unless the opt-in variant below is on.
    public ConfigEntry<bool> TransitAbortWhenSomeoneLeaves { get; set; }

    // (analysis/config-audit-2, 2026-10-13) 'Transit: Abort When Someone Dies' was retired: its default
    // (false) is the native shape - a death drops the dead from the group and the survivors carry on - and
    // the opt-in "a death cancels too" variant was never used. TransitCountdownGuard.AbortWhenSomeoneDies is
    // the static that keeps that behaviour, so only the knob is gone, not the rule.

    // Coop | raid-load sync gates. The 2026-10-11 deadlock: the client gave up on a fixed 40 s location
    // ack while the host was still loading the transit reload, and the host then waited for that peer
    // forever. The waits are now bounded by these values (plus the transport layer, which fails at once
    // when the connection drops), so they have to be tunable in the field.
    public ConfigEntry<int> LoadSyncHostPeerTimeout { get; set; }
    public ConfigEntry<int> LoadSyncClientHostWaitCeiling { get; set; }
    public ConfigEntry<int> LoadSyncClientTransitCeiling { get; set; }
    public ConfigEntry<int> LoadSyncSilentWarnSeconds { get; set; }

    // Coop | how long a client that already ended its own raid may hold its map load while it waits for the
    // host's reload-start signal. The signal makes both machines enter the level load within one round trip;
    // this ceiling only covers a host that never sends it (old build, dead connection, headless without the
    // announcement). Keep it above the host's real lead (measured 44 s) and below the client's (130 s).
    public ConfigEntry<int> TransitReloadHoldSeconds { get; set; }

    // Coop | one switch for the network-timeout judgement line (analysis/transit-timeout-crash). Every
    // timeout path that already logs something also writes one "[NetTimeout] ..." line carrying what
    // timed out, how long it waited, how much machine commit was left at that moment and how much of
    // this session's own queue state existed; one grep of a run then answers "was a network timeout
    // next to the death" for every run instead of for one. Off = the call sites behave exactly as
    // before the line existed.

    public ConfigEntry<float> OnlinePlayersScale { get; set; }

    // Coop | how often the main menu panel asks the server for the online list (analysis/online-players-refresh).
    // The list is a live roster, so it is polled on a short cycle while the panel is visible and on the ten
    // second cycle it always used while it is not. 'Online Players Fast Refresh' off = the previous behaviour:
    // one fixed ten second cycle and a full rebuild of the rows on every cycle (one flip = the old path).
    public ConfigEntry<bool> OnlinePlayersFastRefresh { get; set; }
    public ConfigEntry<int> OnlinePlayersRefreshSeconds { get; set; }
    public ConfigEntry<int> OnlinePlayersIdleRefreshSeconds { get; set; }

    // Coop | main menu decorations. Each decoration has its own switch so the next crash can
    // be bisected from the config file: the panel is 'Enable Online Players', the counter
    // text is 'Write Online Player Counter' and the taskbar button is 'Download Profile
    // Button'. 'Main Menu Ref Trace' turns the raw pointer logging on and off.
    public ConfigEntry<bool> WriteOnlinePlayerCounter { get; set; }
    public ConfigEntry<bool> DownloadProfileButton { get; set; }

    // Coop | performance. One switch for the whole diagnostics channel: off (the default) keeps failures,
    // warnings, the crash trail and the low-frequency session lines and silences the per-call families
    // (InvokerTrace, WarmTable, PumpTrace, NativeEntryWarmup, MainMenuUI.trace). See FikaDiagnostics.Verbose.

    // Coop | performance (analysis/perf-diagnostics-cleanup). The master switch of every *probe* of this
    // port: the per-frame and per-second samplers and the probe tags ([CutsceneDiag], [LootDiag], [NvgDiag],
    // [RallyDiag], [ExtractDiag], [PlantDiag], [MapMarker], [TransitDiag], [MemDiag], [BotOwner], [Inert],
    // [LoadCost]) plus the write-ahead half of the crash trail. Off (the default) makes all of them free:
    // no string, no allocation, no il2cpp sample, no disk write. On = the pre-cleanup behaviour, one flip
    // away, so a run that has to be judged again can still print its judgement lines.
    // See FikaDiagnostics.ProbesEnabled.

    // Coop | crash forensics (analysis/crash-dump-20261013). Independent of 'Diagnostics' and also off by
    // default: it re-opens exactly the three instruments that can still testify when the runtime kills the
    // process through RaiseFailFastException (which no vectored exception handler ever sees) - the crash
    // trail's write-ahead half, its per-boundary ambient label and the managed first-chance logger. It
    // deliberately does NOT re-open the per-frame probe families; those stay behind 'Diagnostics'.
    // See FikaDiagnostics.ForensicsEnabled.

    // Coop | memory guard (analysis/commit-watch). The 2026-10-12 19:59:56 death was a CLR fail fast
    // (0x80131506, coreclr.dll) with sysCommitAvail at 820.7MiB - the commit of the *machine*, not this
    // process's heap; the page file is a user-side setting (only C: was active). The relief arms at
    // 1024 MiB rather than the 768 MiB used as an example, because the death line is at ~820 MiB. These
    // five switches decide
    // how loud Fika is about it (observation) and how much of its own low-priority work it gives up before
    // the runtime dies (relief). See FikaRaidMemory for the thresholds and the levels.

    // Coop | the raid-start countdown, converged by analysis/config-audit-2 (2026-10-13). What used to be
    // twelve bisect switches (analysis/countdown-bisect, -round2, -round3) and five opening-countdown step
    // switches is four entries now:
    //   * 'Skip Countdown Body' and 'Skip Post Wait Block' - the two masters that keep the raid startable;
    //   * 'Countdown: Play Opening Countdown'  - the countdown the player sees (the steps merged into it);
    //   * 'Countdown: Restore In-Raid HUD'     - the HUD the skipped statements would have switched on.
    // The retired entries' statements are unreachable in the shipped configuration anyway (both masters are
    // ON), and the two deaths the bisect hunted were traced to a native -> managed callout while the routine
    // was parked in the deploy wait (analysis/countdown-bisect-round3 section 1.2), not to a statement of the
    // body. CountdownBisect.Read()/CountdownRestore.Read() carry the retired defaults as constants, so
    // re-binding a key is all a rollback needs.
    public ConfigEntry<bool> CountdownRestoreHud { get; set; }
    public ConfigEntry<bool> CountdownPlayOpening { get; set; }

    // Coop | Name Plates
    public ConfigEntry<bool> UseNamePlates { get; set; }
    public ConfigEntry<bool> HideHealthBar { get; set; }
    public ConfigEntry<bool> UseHealthNumber { get; set; }
    public ConfigEntry<bool> UsePlateFactionSide { get; set; }
    public ConfigEntry<bool> HideNamePlateInOptic { get; set; }
    public ConfigEntry<bool> NamePlateUseOpticZoom { get; set; }
    public ConfigEntry<bool> DecreaseOpacityNotLookingAt { get; set; }
    public ConfigEntry<float> NamePlateScale { get; set; }
    public ConfigEntry<float> OpacityInADS { get; set; }
    public ConfigEntry<float> MaxDistanceToShow { get; set; }
    public ConfigEntry<float> MinimumOpacity { get; set; }
    public ConfigEntry<bool> ShowEffects { get; set; }
    public ConfigEntry<bool> UseOcclusion { get; set; }
    public ConfigEntry<Color> FullHealthColor { get; set; }
    public ConfigEntry<Color> LowHealthColor { get; set; }
    public ConfigEntry<Color> NamePlateTextColor { get; set; }
    public ConfigEntry<bool> ShowBrokenLimbs { get; set; }

    // Coop | Quest Sharing
    public ConfigEntry<EQuestSharingTypes> QuestTypesToShareAndReceive { get; set; }
    public ConfigEntry<bool> QuestSharingNotifications { get; set; }
    public ConfigEntry<bool> EasyKillConditions { get; set; }
    public ConfigEntry<bool> SharedKillExperience { get; set; }
    public ConfigEntry<bool> SharedBossExperience { get; set; }

    // Coop | Pinging
    public ConfigEntry<bool> UsePingSystem { get; set; }
    public ConfigEntry<KeyboardShortcut> PingButton { get; set; }
    public ConfigEntry<Color> PingColor { get; set; }
    public ConfigEntry<float> PingSize { get; set; }
    public ConfigEntry<int> PingTime { get; set; }
    public ConfigEntry<bool> PlayPingAnimation { get; set; }
    public ConfigEntry<bool> ShowPingDuringOptics { get; set; }
    public ConfigEntry<bool> PingUseOpticZoom { get; set; }
    public ConfigEntry<bool> PingScaleWithDistance { get; set; }
    public ConfigEntry<float> PingMinimumOpacity { get; set; }
    public ConfigEntry<bool> ShowPingRange { get; set; }
    public ConfigEntry<EPingSound> PingSound { get; set; }

    // Coop | Revival
    public ConfigEntry<KeyboardShortcut> GiveUpKey { get; set; }

    // Coop | Debug
    public ConfigEntry<KeyboardShortcut> FreeCamButton { get; set; }
    public ConfigEntry<bool> AZERTYMode { get; set; }
    public ConfigEntry<bool> DroneMode { get; set; }
    public ConfigEntry<bool> KeybindOverlay { get; set; }

    // Network
    public ConfigEntry<string> ForceIP { get; set; }
    public ConfigEntry<string> ForceBindIP { get; set; }
    public ConfigEntry<ushort> UDPPort { get; set; }
    public ConfigEntry<bool> UseUPnP { get; set; }
    public ConfigEntry<bool> UseNATPunching { get; set; }
    public ConfigEntry<int> ConnectionTimeout { get; set; }
    public ConfigEntry<ESendRate> SendRate { get; set; }
    public ConfigEntry<bool> UseFikaNATPunchServer { get; set; }
    public ConfigEntry<bool> AllowVOIP { get; set; }
    public ConfigEntry<bool> UseIPv6 { get; set; }
    public ConfigEntry<bool> PreferIPv6 { get; set; }
    public ConfigEntry<int> SimulatedLatencyMs { get; set; }
    public ConfigEntry<int> SimulatedPacketLossPercent { get; set; }

    // Gameplay
    public ConfigEntry<bool> DisableBotMetabolism { get; set; }
    public ConfigEntry<bool> StrictInventorySync { get; set; }
    public ConfigEntry<bool> GroundLootPickupBypass { get; set; }
    public ConfigEntry<bool> GroundLootPickupAutoFallback { get; set; }
    public ConfigEntry<bool> PlantInteractionFix { get; set; }

    // Gameplay | analysis/plant-item-consumption: the native plant removal
    // (EFT.InventoryLogic.ItemManipulator.Discard -> RemoveOperation) that takes the planted item
    // out of the inventory is never executed for this port's plant path. On (default) the plant
    // result is completed with exactly that native step on the planting machine's own controller.
    public ConfigEntry<bool> PlantConsumesItem { get; set; }

    // AY (analysis/plant-client-host-crash): the plant gate fires twice natively and the removal it
    // triggers is dispatched asynchronously, so a client used to send the same removal twice; the
    // second one was refused by the host and the refusal drove an authoritative inventory resync that
    // released the plant interaction mid-animation. On (default) a client still relays its own plant
    // removal exactly once (the host applies it - proven by
    // logs-coop2-20261013-110333/LogOutput.log:2931-2933); off keeps the removal strictly on the host,
    // i.e. a client's plant consumes nothing locally and never sends a removal to the host.
    public ConfigEntry<bool> PlantConsumeOnClient { get; set; }

    // AY: the host-side half of the same repair. On (default) a removal-shaped operation the host can
    // already tell it cannot complete (a duplicate of one it just accepted, a malformed descriptor, a
    // pre-flight that cannot be read) is refused in managed code with one [PlantGuard] warning and the
    // usual Failed + resync answer, before the native rebuild is entered.
    public ConfigEntry<bool> PlantRelayGuard { get; set; }

    // analysis/icebreaker-keypad-door: the client's passcode submission is refused by the game's own guard
    // ("Tried to enter code on location without passcodes") before any Fika packet, so the host never learns
    // about the attempt. These two switches carry the relay that fixes it and its own verdict lines.
    public ConfigEntry<bool> KeypadRelayClientSubmit { get; set; }

    // analysis/rally-gate-deadlock, converged by analysis/config-audit-2: the rally trigger of a story map
    // (Icebreaker) waits for the players this raid really has, and a viewer that never signalled stops holding
    // the skip vote - both are the shipped behaviour now (FikaRallyGate.UseFikaPlayerCount = false when the
    // count is native, AllowSkipWithoutAllViewers = true). The one knob that is left is the timeout that
    // decides when a silent viewer counts as "not in this cutscene" (0 = wait for every viewer, the old
    // deadlock).

    // AP (analysis/nonfika-raid-inertness): the master switch of the raid-kind gate. On (default) every
    // in-raid hook that asks FikaRaidGate.Skip stays out of a raid that is not a Fika raid (a raid started
    // from the map selection screen without Fika). Off: the pre-fix behaviour, i.e. the port acts inside
    // the native single-player raid as well - one line to revert everything this round changed.
    public ConfigEntry<bool> InertOutsideFikaRaids { get; set; }

    // (analysis/config-audit-2, 2026-10-13) The cutscene family converged to one path and one switch. What the
    // user asked for is a single feature - the host plays the cutscene, the client plays it too, the story
    // triggers - so the repairs that make that happen (the local scene takeover, the bridge playback repair,
    // the skip-prompt dismissal, the dispatch/seat repair, the client's in-place trigger, the real-time tick
    // source) are now the shipped behaviour of the components and no longer carry a switch each. Only
    // 'Cutscene: Native Tick Hook' is left: it is the one switch that decides which path drives the two
    // controllers. The statics in CutsceneBoundary / FikaLocalCutsceneTrigger / FikaRallyGate keep the target
    // default, so re-binding a key is all a rollback needs.
    // analysis/cutscene-native-registration: the registration the port never had - the two cutscene controllers
    // are handed their per-frame caller where the native raid-start coroutine hands it to them, instead of being
    // ticked by the bridge. One key switches it back to the bridge-only state.

    // Coop | Boss & Spawns - the three switches of the host's raid preparation
    // (Main.GameMode.HostGameController). All three are host-side and read out of static fields, so
    // they are applied in SetupConfig below and stay hot. See analysis per switch in the report
    // analysis/config-grouping-i18n/REPORT.md.

    // (retired) 'Boss: One Boss Per Trigger Point' was removed on the user's request: the collapse kept a
    // single boss wave per trigger point, but in live co-op raids the boss then failed to spawn at all, so
    // the vanilla mechanic is used again (HostGameController.CollapseBossWavesPerTrigger is false and the
    // old key is dropped from the file by RetireRemovedEntries).

    /// <summary>
    /// On (default): mirrors the native offline preparation (OfflineOldSpawn/OfflineNewSpawn and the
    /// WAVE_COEF_* scaling) so the skip/allow decision matches the headless host and the native game.
    /// Off: the shipped flags are used untouched. Diagnostics; leave it on unless a spawn decision has
    /// to be judged.
    /// </summary>
    public ConfigEntry<bool> MirrorOfflineSpawnFlags { get; set; }

    /// <summary>
    /// Off (default): the map's own BotMax decides, so a map that ships 0 (Icebreaker) spawns no
    /// ambient scavengers - the user's rule of 2026-10-13. On: a map that configures an ambient window
    /// yet ships cap 0 gets a fallback budget (SPT's own 18, still bounded by the raid's bot budget).
    /// </summary>
    public ConfigEntry<bool> EnableAmbientBotCapFallback { get; set; }

    /// <summary>
    /// Off (default): the frame-cost probe stays silent on its own. On: one [FrameCost] line every 5 s with
    /// the frame-time distribution, the hitch count and how much of the window Fika's own steps held
    /// (Main.Utils.FrameCostProbe.On ORs this switch with 'Diagnostics', so the master switch runs the
    /// probe as well - this entry is the "only this one line" switch). Diagnostics; zero cost while off.
    /// </summary>

    /// <summary>
    /// (analysis/stutter-pose-reset) 'Coop / Pose Write Probe': counts this port's pose/movement/animator writes
    /// per write site and per frame, and prints one [PoseWrite] line under the [FrameCost] line of the same
    /// window. Default off. It answers 「是不是我方写入造成的」 for the 「动作被重置」 shape, and it turns the frame
    /// cost probe on as well, because the counts are only readable next to the frame distribution and the stall
    /// count of the same frames.
    /// </summary>

    /// <summary>
    /// (analysis/stutter-pose-reset) 'Coop / Pose Write Guards': the 「值未变就不写」 guards of the observed
    /// player's pose/movement/animator writes, on by default. Off restores the pre-guard behaviour without a
    /// rebuild, which is both the rollback and the other half of the in-raid A/B.
    /// </summary>
    public ConfigEntry<bool> PoseWriteGuards { get; set; }


    /// <summary>
    /// (analysis/stutter-frame-skip) 'Coop / Stutter Probe': the frame-skip journal of the 「被抽帧」 shape -
    /// the runs of consecutive long frames, the rhythm between two runs, whether a managed collection ran
    /// inside them, and the measured cost of this port's own main-thread file IO. Default off. It turns the
    /// frame-cost probe on as well, because a run can only be read next to the frame distribution of the same
    /// frames (Main.Utils.StutterJournal.Enabled OR 'Diagnostics').
    /// </summary>

    /// <summary>
    /// (analysis/stutter-frame-skip) 'Coop / Log Archive Async': performs the session log copy on the
    /// archive's own thread instead of inside the frame, on by default. Off is both the rollback and the
    /// in-raid A/B: the copy then happens on the frame boundary exactly as before this round.
    /// </summary>

    /// <summary>
    /// (analysis/stutter-frame-skip) 'Coop / Net Poll Budget': upper bound of the arrived network events a
    /// single frame delivers. 0 (the default) keeps the unbounded drain this port always had; a value such as
    /// 96 defers the rest of a burst to the next frame, in order, without dropping anything. The switch is the
    /// in-raid A/B of the one candidate the 20:01 probe run pointed at (clientUpdate holds the worst frame of
    /// 101 of 103 in-raid windows, and the whole network delivery of a frame happens inside it).
    /// </summary>
    public ConfigEntry<int> NetPollBudget { get; set; }
    public ConfigEntry<bool> FinalMissionEndingRepair { get; set; }
    public ConfigEntry<bool> SentrySyncCardAccess { get; set; }
    public ConfigEntry<bool> TransitClientPressFallback { get; set; }

    // (analysis/config-audit-2) 'Transit: Press Fallback Limit' was retired (TransitRecovery
    // .PressFallbackLimit stays 8, the shipped bound) and 'Transit: Client Transfer Stash' was merged into
    // 'Transit: Per-Player Transfer Stash': one switch now drives both halves of the container repair
    // (TransitRecovery.EnsureTransferStash and .ClientEnsureTransferStash).

    // (analysis/config-audit-2) The client's in-place trigger (Main.Components.FikaLocalCutsceneTrigger) is
    // part of the one cutscene feature, so its three switches are retired: the component's own statics are the
    // shipped behaviour (trigger locally = true, single viewer = true, hold = 2 s).

    // Coop | Transit - the four switches of the transit recovery round (Main.Utils.TransitRecovery).
    // All four ship true: they are the fix, not the diagnosis, except for the verdict log line.

    /// <summary>
    /// On (default): every transit verdict writes one [TransitDiag] line (who is inside the point, whether
    /// everyone interacted, why it was refused). Few lines per raid. Off: the verdict family is a no-op.
    /// </summary>

    /// <summary>
    /// On (default): a client that receives the host's refusal clears its local press latch, so the player
    /// can press again at once (before the repair the prompt stayed and no second press was possible, which
    /// read as "stuck in the interaction"). Off: the pre-repair behaviour.
    /// </summary>
    public ConfigEntry<bool> TransitRearmAfterRefusal { get; set; }

    /// <summary>
    /// On (default): a refused client re-asserts its "I am inside the point" presence once, so the host
    /// does not treat it as having left the point. Off: the pre-repair behaviour.
    /// </summary>

    /// <summary>
    /// On (default): entering the destination map gives every player (clients included) a transfer stash,
    /// created only when it is missing or zero-sized - never cleared. This is what the native OnPlayerEnter
    /// does. Off: the pre-repair behaviour (a client gets no transfer stash).
    /// </summary>
    /// <summary>
    /// On (default): every player of the raid - clients included - gets the per-player transfer container
    /// when it is missing (this is what the native OnPlayerEnter does), and a client builds its own copy.
    /// Merged with the retired 'Transit: Client Transfer Stash' (analysis/config-audit-2): both halves are
    /// the same repair on the two machines, so one switch covers them.
    /// </summary>
    public ConfigEntry<bool> TransitPerPlayerTransferStash { get; set; }

    /// <summary>
    /// (analysis/terminal-transit-mechanism) The client never saw the transit countdown because the transit panel
    /// (<c>EFT.UI.LocationTransitTimerPanel</c>) is a different <c>GameUI</c> field than the extraction one and is
    /// not part of the HUD restore routine, so nothing ever activated it; its own <c>Display()+Show()</c> run as
    /// coroutines on the panel and stay silent while it is inactive. On (default) wakes that panel up once per
    /// countdown; off leaves the pre-round behaviour and only writes the judgement line.
    /// </summary>
    public ConfigEntry<bool> TransitActivateCountdownPanel { get; set; }
    #endregion

    #region client config values
    public bool UseBTR { get; set; }
    public bool FriendlyFire { get; set; }
    public bool DynamicVExfils { get; set; }
    public bool AllowFreeCam { get; set; }
    public bool AllowSpectateFreeCam { get; set; }
    public bool AllowItemSending { get; set; }
    public string[] BlacklistedItems { get; set; }
    public bool ForceSaveOnDeath { get; set; }
    public bool UseInertia { get; set; }
    public bool SharedQuestProgression { get; set; }
    public bool CanEditRaidSettings { get; set; }
    public bool EnableTransits { get; set; }
    public bool AnyoneCanStartRaid { get; set; }
    public bool AllowNamePlates { get; set; }
    public bool RandomLabyrinthSpawns { get; set; }
    public bool PMCFoundInRaid { get; set; }
    public bool AllowSpectateBots { get; set; }
    public bool InstantLoad { get; set; }
    public bool FastLoad { get; set; }
    public ClientReviveConfig ReviveConfig { get; set; }
    #endregion

    private ConfigEntry<T> SetupSetting<T>(string section, string key, T defValue, ConfigDescription configDescription, string fallback, ref bool failed, List<string> error)
    {
        try
        {
            return _config.Bind(section, key, defValue, configDescription);
        }
        catch (Exception ex)
        {
            FikaGlobals.LogError($"Could not set up section {fallback}! Exception:\n{ex.Message}");
            failed = true;
            error.Add(fallback);

            return _config.Bind(section, fallback, defValue, configDescription);
        }
    }

    #region display categories

    // (2026-10-13, config grouping) Display-only layer. The Section and Key of every entry are untouched
    // on purpose: an existing com.fika.core.cfg keeps working and no user value is migrated. Only
    // ConfigurationManagerAttributes.Category/DispName/Order and the tooltips change, so the F12 menu
    // regroups without a single behaviour change. 'Debug | Diagnostics' is the group for everything whose
    // only purpose is to judge a raid (probes, forensics, commit watch, countdown bisect, main menu bisect);
    // it sorts last by name and carries the largest Order values inside the group, i.e. it sits at the
    // bottom of the list where an operator expects it. In-game presentation is English + Chinese.
    private const string DisplayCoop = "Coop | 联机总览";
    private const string DisplayCoopCore = "Coop | Core";
    private const string DisplayCoopTransit = "Coop | Transit";
    private const string DisplayCoopLoadSync = "Coop | Load Sync";
    private const string DisplayCoopCutscene = "Coop | Cutscene";
    private const string DisplayCoopRally = "Coop | Rally";
    private const string DisplayCoopExfil = "Coop | Exfil & Flare";
    private const string DisplayCoopNamePlates = "Coop | Name Plates";
    private const string DisplayCoopPinging = "Coop | Pinging";
    private const string DisplayCoopQuestSharing = "Coop | Quest Sharing";
    private const string DisplayCoopRevival = "Coop | Revival";
    private const string DisplayCoopDebug = "Coop | Debug";
    private const string DisplayCoopBossSpawns = "Coop | Boss & Spawns";
    private const string DisplayDebugDiagnostics = "Debug | Diagnostics";
    private const string DisplayGameplay = "Gameplay";
    private const string DisplayNetwork = "Network";
    private const string DisplayAdvanced = "Advanced";

    #endregion

    public void SetupConfig()
    {
        // (analysis/crash-dump-20261013) The begin/end pair is the only way to tell "died inside SetupConfig"
        // from "died after it": the 2026-10-13 runs logged neither "Locales and configuration are ready"
        // (FikaPlugin) nor "Fika initialization failed" (its catch), so the process died *inside* this method,
        // between two statements. One line at each end, always written ('Config' survives the quiet mode),
        // no per-frame cost.
        // The retired keys go first, so every line below - and the entry count of the 'begin' step - describes
        // the file this build really owns.
        // (2026-10-20) Fika failed to initialise with a NullReferenceException raised inside this method
        // ("Fika initialization failed; cooperative play is unavailable"). The two steps that run before any
        // binding are best-effort now: neither is allowed to take cooperative play down with it, and each one
        // names itself if it throws so the journal points at the step instead of the method.
        try { RetireRemovedEntries(); }
        catch (Exception exception) { FikaGlobals.LogError($"RetireRemovedEntries failed: {exception}"); }
        
        try { ColorConfigConverter.Register(); }
        catch (Exception exception) { FikaGlobals.LogError($"ColorConfigConverter.Register failed: {exception}"); }
        var failed = false;
        List<string> headers = [];
        
        var disabledMessage = LocaleUtils.UI_DISABLED_BY_HOST.Localized();
        

        // Hidden

        LastVersion = _config.Bind("Hidden", "Last Version", "0",
            new ConfigDescription("Last loaded version of Fika", tags: new ConfigurationManagerAttributes { Browsable = false }));

#if GOLDMASTER
        if (LastVersion.Value != FikaVersion)
        {
            Singleton<PreloaderUI>.Instance.ShowFikaMessage("FIKA", LocaleUtils.UI_TOS_LONG.Localized(), ErrorScreen.EButtonType.QuitButton, 15f,
                null, () =>
                {
                    LastVersion.Value = FikaVersion;
                });
        }
#endif

        // Advanced

        const string advancedDefaultHeader = "Advanced";

        OfficialVersion = SetupSetting(advancedDefaultHeader, "显示正式版本", false,
                new ConfigDescription(LocaleUtils.BEPINEX_OFFICIAL_VERSION_D.Localized(), tags: new ConfigurationManagerAttributes
                {
                    IsAdvanced = true,
                    Category = DisplayAdvanced,
                    DispName = LocaleUtils.BEPINEX_OFFICIAL_VERSION_T.Localized(),
                    Order = 6
                }),
                "Official Version", ref failed, headers);

        DevMode = SetupSetting(advancedDefaultHeader, "开发者模式", false,
            new ConfigDescription(
                "开：启用开发者功能。关（默认）：保持关闭。仅影响本机界面与调试入口。", tags: new ConfigurationManagerAttributes
            {
                IsAdvanced = true,
                Category = DisplayAdvanced,
                DispName = "Developer Mode | 开发者模式",
                Order = 50
            }), "开发者模式", ref failed, headers);

        NoAI = SetupSetting(advancedDefaultHeader, "No AI", false,
            new ConfigDescription(
                "开：不生成任何 AI（Scav/Boss 等）。关（默认）：正常生成。用于加快加载速度与排查 AI 相关问题。", tags: new ConfigurationManagerAttributes
            {
                IsAdvanced = true,
                Category = DisplayAdvanced,
                DispName = "No AI | 不刷 AI",
                Order = 40
            }), "No AI", ref failed, headers);

        NoLoot = SetupSetting(advancedDefaultHeader, "不生成战利品", false,
            new ConfigDescription(
                "开：不生成地面物资。关（默认）：正常生成。用于加快加载速度、排查物资相关问题时使用。", tags: new ConfigurationManagerAttributes
            {
                IsAdvanced = true,
                Category = DisplayAdvanced,
                DispName = "No Loot | 不刷物资",
                Order = 30
            }), "不生成战利品", ref failed, headers);

        LoadPriority = SetupSetting(advancedDefaultHeader, "玩家加载优先级", ELoadPriority.Low,
            new ConfigDescription(
                "加载其他玩家（作客机时含 AI）时使用的线程优先级。默认 Low。效果可能不明显，卡加载时可试 Medium/High。", tags: new ConfigurationManagerAttributes
            {
                IsAdvanced = true,
                Category = DisplayAdvanced,
                DispName = "Player Load Priority | 玩家加载优先级",
                Order = 20
            }), "玩家加载优先级", ref failed, headers);

        MaxBundleLock = SetupSetting(advancedDefaultHeader, "最大打包锁定", 5,
            new ConfigDescription(
                "同时并行加载的资源包数量上限（1-10，默认 5）。作客机加载 bot 很慢时可调高；游戏默认值是 1，这里调高是为了缓解原版资源包加载过慢的问题。",
            new AcceptableValueRange<int>(1, 10), new ConfigurationManagerAttributes
            {
                IsAdvanced = true,
                Category = DisplayAdvanced,
                DispName = "Max Bundle Lock | 并行加载包数量",
                Order = 10
            }), "最大打包锁定", ref failed, headers);

        // Coop

        const string coopDefaultHeader = "Coop";

        UseHeadlessIfAvailable = SetupSetting(coopDefaultHeader, "自动使用无头主机", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_HEADLESS_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_USE_HEADLESS_T.Localized(),
                Order = 9
            }), "自动使用无头主机", ref failed, headers);

        ShowNotifications = SetupSetting(coopDefaultHeader, "显示击杀信息", true,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOW_FEED_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_SHOW_FEED_T.Localized(),
                Order = 8
            }),
            "显示击杀信息", ref failed, headers);

        AutoExtract = SetupSetting(coopDefaultHeader, "自动撤离", false,
            new ConfigDescription(LocaleUtils.BEPINEX_AUTO_EXTRACT_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_AUTO_EXTRACT_T.Localized(),
                Order = 7
            }),
            "自动撤离", ref failed, headers);

        ShowExtractMessage = SetupSetting(coopDefaultHeader, "显示撤离提示", true,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOW_EXTRACT_MESSAGE_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_SHOW_EXTRACT_MESSAGE_T.Localized(),
                Order = 6
            }),
            "显示撤离提示", ref failed, headers);

        ExtractKey = SetupSetting(coopDefaultHeader, "撤离按键", new KeyboardShortcut(KeyCode.F8),
            new ConfigDescription(LocaleUtils.BEPINEX_EXTRACT_KEY_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_EXTRACT_KEY_T.Localized(),
                Order = 5
            }),
            "撤离按键", ref failed, headers);

        ShowPlayerList = SetupSetting(coopDefaultHeader, "显示局内玩家列表", true,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOW_PLAYER_LIST_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_SHOW_PLAYER_LIST_T.Localized(),
                Order = 4
            }),
            "显示局内玩家列表", ref failed, headers);

        EnableChat = SetupSetting(coopDefaultHeader, "启用聊天", false,
            new ConfigDescription(LocaleUtils.BEPINEX_ENABLE_CHAT_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_ENABLE_CHAT_T.Localized(),
                Order = 3
            }),
            "启用聊天", ref failed, headers);

        ChatKey = SetupSetting(coopDefaultHeader, "聊天按键", new KeyboardShortcut(KeyCode.RightControl),
            new ConfigDescription(LocaleUtils.BEPINEX_CHAT_KEY_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_CHAT_KEY_T.Localized(),
                Order = 2
            }),
            "聊天按键", ref failed, headers);

        // Shipped default is ON (2026-10-10). The main menu access violation this setting used
        // to trigger is fixed: the fault was the engine processing the coroutine wrapper Fika
        // handed it through StartCoroutine(IEnumerator) (a first-chance probe caught the wrapper
        // in the engine's own frames while it dereferenced the bogus object 0xA), and the panel
        // no longer uses engine-driven coroutines - canvas poll, ten second refresh and watchdog
        // marks run from the injected behaviour's Update. Verified with the panel enabled: main
        // menu, a full raid (LocalGameMatching completed without a fault) and the return to the
        // rebuilt main menu, with no crash and no new crash dump.
        FlareExtractSquadSharesActivation = SetupSetting(coopDefaultHeader, "信号弹撤离：小队共享激活", true,
            new ConfigDescription(
                "开（默认，原版队伍通道）：一发绿烟开启射手所属小队的撒离点，队友不用自己开枪。关：只有开枪者本人能用，每人必须自己打一发。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopExfil,
                    DispName = "Flare Extract: Squad Shares Activation | 绿烟对友共享",
                    Order = 100
                }),
            "信号弹撤离：小队共享激活", ref failed, headers);

        FlareReplicateExitVisual = SetupSetting(coopDefaultHeader, "信号弹撤离：复制撤离视觉", true,
            new ConfigDescription(
                "开（默认）：开枪者那台机器跑过的本地链（直升机落地、旋翼气流、撒离点活过来）在全队各机同步重放，队友能看到同一动画并离场。关：只有开枪机器播放动画（修复前行为）；它不会为未被激活的玩家打开撒离点。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopExfil,
                    DispName = "Flare Extract: Replicate Exit Visual | 同步撒离点动画",
                    Order = 95
                }),
            "信号弹撤离：复制撤离视觉", ref failed, headers);

        FlareExfilFeedbackSeconds = SetupSetting(coopDefaultHeader, "信号弹撤离：反馈秒数", 5f,
            new ConfigDescription(
                "绿烟把本玩家加入狙击手免疫名单后，“道路已开放”横幅在屏幕上停留的秒数（默认 5）。≤ 0 表示一直留到战局结束。每次弹出时读取，不需重启。",
                new AcceptableValueRange<float>(0f, 60f), new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopExfil,
                    DispName = "Flare Extract: Feedback Seconds | 成功提示显示秒数",
                    Order = 90
                }),
            "Flare Extract: Feedback Seconds", ref failed, headers);

        HonorMapFixedTime = SetupSetting(coopDefaultHeader, "遵从地图固定时间", true,
            new ConfigDescription(
                "开（默认）：主机开局时用地图自带的固定时间（如破冰船），忽略玩家自己设的小时；日志行 'Raid time: ... fixed=HH:mm' 会记下来源。关：回到玩家自己的设置（旧行为）。重连与转移都保持当前时钟。主机开局时读取。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopCore,
                    DispName = "Honor Map Fixed Time | 遵从地图固定时间",
                    Order = 20
                }),
            "Honor Map Fixed Time", ref failed, headers);

        TransitRequireAllTeammates = SetupSetting(coopDefaultHeader, "转移：需要所有队友就绪", true,
            new ConfigDescription(
                "Strict transit start gate (default on). The countdown only starts once every teammate of this raid " +
                "has interacted with the transit point and is standing inside it. Off: it starts as soon as the host " +
                "interacted and every alive human player is inside the point.",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Require All Teammates Ready",
                    Order = 100
                }),
            "Transit: Require All Teammates Ready", ref failed, headers);

        TransitCountdownFallbackSeconds = SetupSetting(coopDefaultHeader, "转移：倒计时兜底", 30,
            new ConfigDescription(
                "Countdown length in seconds used when the transit point itself does not define one (TransitParameters.time == 0).",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Countdown Fallback",
                    Order = 80
                }),
            "Transit: Countdown Fallback", ref failed, headers);

        // The gate reads static fields so the host and the headless controller see the value without holding the
        // config object; keep them in sync here and on every change (hot switch, no restart).
        Fika.Core.Main.HostClasses.TransitStartGate.RequireAllTeammatesInteracted = TransitRequireAllTeammates.Value;
        Fika.Core.Main.HostClasses.TransitStartGate.CountdownFallbackSeconds = TransitCountdownFallbackSeconds.Value;
        TransitRequireAllTeammates.SettingChanged += (_, _) =>
            Fika.Core.Main.HostClasses.TransitStartGate.RequireAllTeammatesInteracted = TransitRequireAllTeammates.Value;
        TransitCountdownFallbackSeconds.SettingChanged += (_, _) =>
            Fika.Core.Main.HostClasses.TransitStartGate.CountdownFallbackSeconds = TransitCountdownFallbackSeconds.Value;

        LoadSyncHostPeerTimeout = SetupSetting(coopDefaultHeader, "加载同步：主机等待同伴上限（秒）", 300,
            new ConfigDescription(
                "主机等已连接但始终未声明角色的同伴的上限（秒，默认 300）；超时后仍会放行赛前闸门并丢弃该同伴。连接已断的同伴不消耗这个预算。只影响主机。",
                new AcceptableValueRange<int>(30, 1800),
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopLoadSync,
                    DispName = "Load Sync: Host Peer Timeout | 主机等同伴上限（秒）",
                    Order = 100
                }),
            "Load Sync: Host Peer Timeout", ref failed, headers);

        LoadSyncClientHostWaitCeiling = SetupSetting(coopDefaultHeader, "加载同步：客机等待主机上限（秒）", 600,
            new ConfigDescription(
                "客机入局后等待主机握手的上限（秒，默认 600）。连接已断会立即失败；这个上限只用于“连接还在但主机永远不推进”。只影响客机。",
                new AcceptableValueRange<int>(60, 3600),
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopLoadSync,
                    DispName = "Load Sync: Client Host-Wait Ceiling | 客机等主机上限（秒）",
                    Order = 95
                }),
            "Load Sync: Client Host-Wait Ceiling", ref failed, headers);

        LoadSyncClientTransitCeiling = SetupSetting(coopDefaultHeader, "加载同步：客机转移重载上限（秒）", 900,
            new ConfigDescription(
                "同上，但用于转移重载：双方同时加载新地图，较慢的一方曾被旧的固定 40 秒上限切断（秒，默认 900）。只影响客机。",
                new AcceptableValueRange<int>(60, 3600),
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopLoadSync,
                    DispName = "Load Sync: Client Transit Reload Ceiling | 客机转移重载上限（秒）",
                    Order = 90
                }),
            "Load Sync: Client Transit Reload Ceiling", ref failed, headers);

        LoadSyncSilentWarnSeconds = SetupSetting(coopDefaultHeader, "加载同步：无包警告间隔（秒）", 30,
            new ConfigDescription(
                "多久没收到对方数据包就把 [LoadSync] 行升为 warning（秒，默认 30）。仅影响日志可读性，不会自己结束等待。",
                new AcceptableValueRange<int>(5, 300),
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopLoadSync,
                    DispName = "Load Sync: Silent Warning Seconds | 无包警告间隔（秒）",
                    Order = 85
                }),
            "Load Sync: Silent Warning Seconds", ref failed, headers);

        // Same pattern as the transit gate: the waits read static fields, so keep them in sync here and live.
        LoadSyncGate.HostPeerLoadTimeoutSeconds = LoadSyncHostPeerTimeout.Value;
        LoadSyncGate.ClientHostWaitCeilingSeconds = LoadSyncClientHostWaitCeiling.Value;
        LoadSyncGate.ClientTransitReloadCeilingSeconds = LoadSyncClientTransitCeiling.Value;
        LoadSyncGate.ClientSilentWarnSeconds = LoadSyncSilentWarnSeconds.Value;
        LoadSyncHostPeerTimeout.SettingChanged += (_, _) =>
            LoadSyncGate.HostPeerLoadTimeoutSeconds = LoadSyncHostPeerTimeout.Value;
        LoadSyncClientHostWaitCeiling.SettingChanged += (_, _) =>
            LoadSyncGate.ClientHostWaitCeilingSeconds = LoadSyncClientHostWaitCeiling.Value;
        LoadSyncClientTransitCeiling.SettingChanged += (_, _) =>
            LoadSyncGate.ClientTransitReloadCeilingSeconds = LoadSyncClientTransitCeiling.Value;
        LoadSyncSilentWarnSeconds.SettingChanged += (_, _) =>
            LoadSyncGate.ClientSilentWarnSeconds = LoadSyncSilentWarnSeconds.Value;

        TransitReloadHoldSeconds = SetupSetting(coopDefaultHeader, "转移：客机重载等待上限（秒）", 240,
            new ConfigDescription(
                "客机已结束本局后，等待主机重载信号时持住地图加载的上限（秒，默认 240）。正常情况信号一个往返就到，该上限只保护“主机永远不发信号”（旧版本、连接已断、无通知的 headless 主机）。只影响客机。",
                new AcceptableValueRange<int>(5, 600),
                tags: new ConfigurationManagerAttributes
                {
                    // (analysis/config-audit-2) A load-sync wait, not a transfer semantic: filed next to the four
                    // waits of its family, so 'Coop | Transit' carries the transfer rule and nothing else.
                    Category = DisplayCoopLoadSync,
                    DispName = "Transit: Client Reload Hold | 客机重载等待上限（秒）",
                    Order = 80
                }),
            "Transit: Client Reload Hold", ref failed, headers);

        TransitReloadGate.ClientHoldSeconds = TransitReloadHoldSeconds.Value;
        TransitReloadHoldSeconds.SettingChanged += (_, _) =>
            TransitReloadGate.ClientHoldSeconds = TransitReloadHoldSeconds.Value;

        // Coop | the network-timeout judgement line (analysis/transit-timeout-crash). On (default): every
        // timeout / wait-timeout decision this session takes also writes one greppable "[NetTimeout] ..."
        // line that carries what timed out, how long it waited, how much machine commit was left and how
        // much of this session's own queue state existed at that moment. One grep of a run then answers
        // "was a network timeout next to the death" for every run. Off: the call sites behave exactly as
        // before the line existed (one flip = the old behaviour, no rebuild).

        // (analysis/config-binding-order-regression, 2026-10-13) The wiring of 'Diagnostics', 'Diagnostics
        // Verbose' and 'Crash Forensics' USED TO SIT HERE - i.e. *before* the three entries are bound further
        // down this method ('Diagnostics Verbose' ~872, 'Diagnostics' ~889, 'Crash Forensics' ~919). Reading a
        // ConfigEntry property that is still null throws NullReferenceException, the caller's catch turns that
        // into "Fika initialization failed; cooperative play is unavailable", and - the part that mattered -
        // every single binding after this line never ran. The wiring now lives after the last of the three
        // bindings, right below the 'Crash Forensics' entry. Nothing else about the switches changed.

        EnableOnlinePlayers = SetupSetting(coopDefaultHeader, "启用在线玩家", true,
            new ConfigDescription(LocaleUtils.BEPINEX_ENABLE_ONLINE_PLAYER_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_ENABLE_ONLINE_PLAYER_T.Localized(),
                Order = 100
            }),
            "Enable Online Players", ref failed, headers);

        OnlinePlayersScale = SetupSetting(coopDefaultHeader, "在线玩家缩放", 1f,
            new ConfigDescription(LocaleUtils.BEPINEX_ONLINE_PLAYERS_SCALE_D.Localized(),
            new AcceptableValueRange<float>(0.5f, 1.5f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_ONLINE_PLAYERS_SCALE_T.Localized(),
                Order = 95
            }),
            "Online Players Scale", ref failed, headers);

        // Coop | the online players panel's cycle (analysis/online-players-refresh). The panel used to ask the
        // server for the roster on one fixed ten second cycle, so a player who joined or left only showed up on
        // the next cycle - and the server log of a whole test day shows the polls in 10 s bursts only
        // (logs-r13test-20261012-200405, spt-server-20261012.log). Three seconds keeps a join or a leave visible
        // within a few seconds, and an unchanged cycle costs one HTTP GET (~12 ms measured) because the rows are
        // only rebuilt when the roster really changed. Setting this back to 10 restores the old cadence; the
        // switch below restores the old code path as well.
        OnlinePlayersFastRefresh = SetupSetting(coopDefaultHeader, "在线玩家快速刷新", true,
            new ConfigDescription(
                "开（默认）：面板可见时按下方的短周期拉取名单，面板重开时立即刷新，名单没变则不重建行。关：回到早前行为——固定 10 秒一轮，每轮重建全部行。仅影响本机。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = "Online Players Fast Refresh | 在线玩家快速刷新",
                Order = 90
            }),
            "Online Players Fast Refresh", ref failed, headers);

        OnlinePlayersRefreshSeconds = SetupSetting(coopDefaultHeader, "可见刷新间隔（秒）", 3,
            new ConfigDescription(
                "面板可见时两次刷新的间隔（秒），默认 3。改回 10 就恢复原来的节奏；只影响本机面板。", new AcceptableValueRange<int>(1, 60), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = "Online Players Refresh Seconds | 可见刷新间隔（秒）",
                Order = 85
            }),
            "Online Players Refresh Seconds", ref failed, headers);

        OnlinePlayersIdleRefreshSeconds = SetupSetting(coopDefaultHeader, "不可见刷新间隔（秒）", 10,
            new ConfigDescription(
                "面板不可见时的刷新间隔（秒），默认 10，即面板一直在用的旧周期。战局或加载界面期间引擎不会调用面板，此时不发任何请求。", new AcceptableValueRange<int>(1, 300), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = "Online Players Idle Refresh Seconds | 不可见刷新间隔（秒）",
                Order = 80
            }),
            "Online Players Idle Refresh Seconds", ref failed, headers);

        // Coop | main menu decorations. The panel itself is 'Enable Online Players'; the two
        // switches below isolate the two writes of that panel that happen after the prefab
        // was instantiated, and the taskbar button that is registered with the live game UI.
        WriteOnlinePlayerCounter = SetupSetting(coopDefaultHeader, "写入在线玩家计数", true,
            new ConfigDescription(
                "开（默认）：把在线玩家数写进主菜单面板的文本。关：面板照常运行，但不碰动 prefab 里的 TextMeshProUGUI——主菜单崩溃时用来排除这一步。仅影响本机显示。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = "Write Online Player Counter | 写入在线玩家计数",
                Order = 40
            }),
            "Write Online Player Counter", ref failed, headers);

        DownloadProfileButton = SetupSetting(coopDefaultHeader, "下载档案按钮", true,
            new ConfigDescription(
                "开（默认）：在主菜单任务栏创建“下载档案”按钮，并向游戏 MenuTaskBar 注册其切换与提示。关：主菜单不再碰任务栏。仅影响本机。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = "Download Profile Button | 下载档案按钮",
                Order = 35
            }),
            "下载档案按钮", ref failed, headers);

        // Off by default since 3.0.3: the trace does a VirtualQuery per native reference and lines up with
        // the menu panel's 10 s refresh, which produced a ~2 s freeze every 10 s while the menu was open.
        // Turn it on only while tracking a main menu crash.


        // Coop | performance (analysis/perf-diagnostics-cleanup). The master switch of the diagnostic probes.
        // Off by default: the user's report is "掉帧严重 + 规律性卡顿" and the measurements of the 2026-10-13
        // 00:40 raid name the probes as the load - a full scene scan every second (CutsceneBoundary.Diagnose),
        // eleven counting patches on the per-frame world steps (CutsceneTickPathProbe), about 9,400 counter
        // calls per second on the two cutscene controllers, ~89,000 unbuffered crash-trail writes per raid and
        // a per-second line for the pickup watchdog, the memory meter and the night-vision sampler.
        // On = every judgement line of those families comes back (same shape as before the cleanup), so a
        // failing raid can still be judged with one flip instead of a rebuild.

        // Coop | crash forensics (analysis/crash-dump-20261013). The full-memory dumps of the 2026-10-13
        // 08:58:52 and 08:59:52 deaths show coreclr failing fast at coreclr+0x2C7085 - "Invalid Program:
        // attempted to call a UnmanagedCallersOnly method from managed code" - and the chain on the main
        // thread is EEPolicy::HandleFatalError -> LogFatalError -> RaiseFailFastException. That last call
        // bypasses every vectored exception handler, so FirstChanceFaultProbe never runs for it and the
        // trail's [Fatal] block never gets written; the two runs left a 1 KB Fika-crash.log with nothing
        // but its table header. The only instrument that survives such a death is the write-ahead half of
        // the trail, and that half was closed by 'Diagnostics' alone. This switch is that half on its own,
        // so a repro can be judged without paying for the per-frame probes.
        // (2026-10-13, main agent: the default was temporarily raised to true for one reproduction run of the
        // 0x80131506 fail-fast. That run ended without a death - the crash was the config-binding regression
        // fixed in that build - so the shipped default is false again here.)

        // (analysis/config-binding-order-regression, 2026-10-13) Wiring of the three diagnostic switches, moved
        // here from its old place right after the 'Net Timeout Diagnostic' entry. It MUST stay below all three
        // bindings ('Diagnostics Verbose', 'Diagnostics', 'Crash Forensics'), because it dereferences all three
        // properties: while it sat above them it threw NullReferenceException, the caller's catch reported
        // "Fika initialization failed", and every binding from that line to the end of this method was skipped -
        // which is why the config file on disk stopped gaining entries after 'Net Timeout Diagnostic'.
        //
        // (analysis/perf-diagnostics-cleanup) The [NetTimeout] line is a probe and therefore also behind the
        // master switch: its own entry stays as the sub-switch, so 'Net Timeout Diagnostic' can no longer
        // turn the line on while 'Diagnostics' is off. Both switches must be on for the line to appear.

        // Coop | the master switch of every diagnostic probe (analysis/perf-diagnostics-cleanup). Both it and
        // the per-call 'Diagnostics Verbose' below are applied here, so the two switches the code reads are

        // Coop | memory guard (analysis/commit-watch). 'Commit Watch' is observation only: the [MemWatch]
        // WARN/CRITICAL lines and their thresholds, at most one line per 5 s. The relief below is a separate
        // switch because it is the only part that changes behaviour - it arms below the critical threshold
        // (1024 MiB) and gives up Fika's own low-priority work, never anything that carries inventory or
        // synchronisation state. Turning all three off is the one-click revert of this whole round.




        // (analysis/config-audit-2) 'Commit Watch Hard Release' was retired as a sub-switch: it only trimmed
        // the relief above (FikaRaidMemory.HardReleaseEnabled = true is the shipped behaviour).

        // (analysis/config-audit-2) Retired here: 'Show Final Countdown Screen' and 'Raid Start Menu Music'
        // (both only reachable while the master switch below is off) and 'Raid Start Audio Fades' - that one
        // was not gating anything any more at all: its statement runs unconditionally since the 2026-10-10 fix
        // (BaseGameController.cs:481-494, 'it runs unconditionally now; neither bisect switch gates it').
        // Coop | countdown bisect round 2 (analysis/countdown-bisect-round2). The master switch ships ON so
        // the default configuration is a raid that starts even if one of the kept statements is the aborter;
        // the eight statement switches ship ON (they are what upstream ran) and only matter while the master
        // switch is off. The re-enable order is in analysis/countdown-bisect-round2/REPORT.md section 5.

        // Coop | countdown bisect round 3 (analysis/countdown-bisect-round3): the post-wait block gets its
        // own master switch. It ships ON, i.e. the shipped default runs the deploy wait and then returns
        // straight to the session sequence (step 2/3 done -> step 3/3, the native session coroutine) with
        // none of statements 4/8-8/8. Turn it OFF to run those statements again (each of them still has its
        // own switch above). Cost while it is ON is in analysis/countdown-bisect-round3/REPORT.md.

        // Coop | in-raid HUD restore (analysis/inraid-ui). The two master switches above ('Skip Countdown
        // Body', 'Skip Post Wait Block') ship ON, so statements 1/8 (GameUi.gameObject.SetActive(true)) and
        // 2/8 (TimerPanel.ProfileId) - the two statements that switch the raid HUD hierarchy on - never run
        // in a default raid. The whole HUD hierarchy (the GameUI root, EFT.UI.ExtractionTimersPanel, the
        // EFT.UI.BattleUIScreen) then stays inactive for the entire raid: the timer key does nothing, the
        // extraction list and the extraction countdown never appear, and every coroutine the game starts on
        // those panels is refused by Unity. This entry restores the HUD after the deploy wait, on the Update
        // pump, each step in its own try/catch - it does not re-enable the skipped statements.
        CountdownRestoreHud = SetupSetting(coopDefaultHeader, "倒计时：恢复战局内 HUD", true,
            new ConfigDescription(
                "开（默认）：部署等待后，把战局 HUD（撤离点列表 + 剩余时间、撤离倒计时）重新激活并绑定到本档案。它只重复函数体里的两条 HUD 语句（1/8、2/8），不会重新打开任何被跳过的语句。关：整个战局 HUD 不可见。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayDebugDiagnostics,
                DispName = "Countdown: Restore In-Raid HUD | 恢复战局内 HUD",
                Order = 26
            }),
            "Countdown: Restore In-Raid HUD", ref failed, headers);

        // Coop | the opening countdown (analysis/countdown-restore). One umbrella switch and one switch per
        // step, all shipping ON: the player asked for the countdown back, every step names itself in the crash
        // trail before it runs, and the umbrella switch is the single line that returns the raid to exactly the
        // behaviour it had before this entry existed. Nothing here turns a master switch off - the body stays
        // as skipped as it is, this path is simply the countdown that used to be inside it.
        CountdownPlayOpening = SetupSetting(coopDefaultHeader, "倒计时：播放开局倒计时", true,
            new ConfigDescription(
                "开（默认）：在跳过倒计时函数体的同时，用一条自己的流程把玩家要看的倒计时放回来，部署等待结束再收回（倒计时时长正好等于本局部署时间）。关：完全没有倒计时遮罩。遮罩/关闭/视图恢复/再确认四步已并入本条（config-audit-2），不再各自占一个开关。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayDebugDiagnostics,
                DispName = "Countdown: Play Opening Countdown | 播放开局倒计时",
                Order = 25
            }),
            "Countdown: Play Opening Countdown", ref failed, headers);

        // Coop | Name Plates

        const string coopDefaultNamePlatesHeader = "Coop | Name Plates";

        UseNamePlates = SetupSetting(coopDefaultNamePlatesHeader, "显示玩家名牌", true,
            new ConfigDescription(AllowNamePlates ? LocaleUtils.BEPINEX_USE_NAME_PLATES_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_USE_NAME_PLATES_T.Localized(),
                Order = 16,
                ReadOnly = !AllowNamePlates
            }),
            "显示玩家名牌", ref failed, headers);

        HideHealthBar = SetupSetting(coopDefaultNamePlatesHeader, "隐藏血条", false,
            new ConfigDescription(LocaleUtils.BEPINEX_HIDE_HEALTH_BAR_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_HIDE_HEALTH_BAR_T.Localized(),
                Order = 15
            }),
            "隐藏血条", ref failed, headers);

        UseHealthNumber = SetupSetting(coopDefaultNamePlatesHeader, "显示血量百分比而非血条", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_PERCENT_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_USE_PERCENT_T.Localized(),
                Order = 14
            }),
            "显示血量百分比而非血条", ref failed, headers);

        ShowEffects = SetupSetting(coopDefaultNamePlatesHeader, "显示特效", true,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOW_EFFECTS_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_SHOW_EFFECTS_T.Localized(),
                Order = 13
            }),
            "显示特效", ref failed, headers);

        UsePlateFactionSide = SetupSetting(coopDefaultNamePlatesHeader, "显示玩家阵营图标", true,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOW_FACTION_ICON_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_SHOW_FACTION_ICON_T.Localized(),
                Order = 12
            }),
            "显示玩家阵营图标", ref failed, headers);

        HideNamePlateInOptic = SetupSetting(coopDefaultNamePlatesHeader, "开镜时隐藏名牌", true,
            new ConfigDescription(LocaleUtils.BEPINEX_HIDE_IN_OPTIC_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_HIDE_IN_OPTIC_T.Localized(),
                Order = 11
            }),
            "开镜时隐藏名牌", ref failed, headers);

        NamePlateUseOpticZoom = SetupSetting(coopDefaultNamePlatesHeader, "名牌使用开镜缩放", true,
            new ConfigDescription(LocaleUtils.BEPINEX_OPTIC_USE_ZOOM_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_OPTIC_USE_ZOOM_T.Localized(),
                Order = 10,
                IsAdvanced = true
            }),
            "名牌使用开镜缩放", ref failed, headers);

        DecreaseOpacityNotLookingAt = SetupSetting(coopDefaultNamePlatesHeader, "周边视野降低不透明度", true,
            new ConfigDescription(LocaleUtils.BEPINEX_DEC_OPAC_PERI_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_DEC_OPAC_PERI_T.Localized(),
                Order = 9
            }),
            "周边视野降低不透明度", ref failed, headers);

        NamePlateScale = SetupSetting(coopDefaultNamePlatesHeader, "名牌缩放", 1f,
            new ConfigDescription(LocaleUtils.BEPINEX_NAME_PLATE_SCALE_D.Localized(),
            new AcceptableValueRange<float>(0.5f, 1.5f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_NAME_PLATE_SCALE_T.Localized(),
                Order = 8
            }),
            "名牌缩放", ref failed, headers);

        OpacityInADS = SetupSetting(coopDefaultNamePlatesHeader, "开镜时不透明度", 0.75f,
            new ConfigDescription(LocaleUtils.BEPINEX_ADS_OPAC_D.Localized(),
            new AcceptableValueRange<float>(0.1f, 1f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_ADS_OPAC_T.Localized(),
                Order = 7
            }),
            "开镜时不透明度", ref failed, headers);

        MaxDistanceToShow = SetupSetting(coopDefaultNamePlatesHeader, "显示的最大距离", 500f,
            new ConfigDescription(LocaleUtils.BEPINEX_MAX_DISTANCE_D.Localized(),
            new AcceptableValueRange<float>(10f, 1000f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_MAX_DISTANCE_T.Localized(),
                Order = 6
            }),
            "显示的最大距离", ref failed, headers);

        MinimumOpacity = SetupSetting(coopDefaultNamePlatesHeader, "最小不透明度", 0.1f,
            new ConfigDescription(LocaleUtils.BEPINEX_MIN_OPAC_D.Localized(),
            new AcceptableValueRange<float>(0.0f, 1f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_MIN_OPAC_T.Localized(),
                Order = 5
            }),
            "最小不透明度", ref failed, headers);

        UseOcclusion = SetupSetting(coopDefaultNamePlatesHeader, "使用遮挡剔除", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_OCCLUSION_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_USE_OCCLUSION_T.Localized(),
                Order = 4
            }),
            "使用遮挡剔除", ref failed, headers);

        FullHealthColor = SetupSetting(coopDefaultNamePlatesHeader, "满血颜色", Color.green,
            new ConfigDescription(LocaleUtils.BEPINEX_HEALTHCOLOR_FULL_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_HEALTHCOLOR_FULL_T.Localized(),
                Order = 3
            }),
            "满血颜色", ref failed, headers);

        LowHealthColor = SetupSetting(coopDefaultNamePlatesHeader, "低血颜色", Color.red,
            new ConfigDescription(LocaleUtils.BEPINEX_HEALTHCOLOR_LOW_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_HEALTHCOLOR_LOW_T.Localized(),
                Order = 2
            }),
            "低血颜色", ref failed, headers);

        NamePlateTextColor = SetupSetting(coopDefaultNamePlatesHeader, "名牌文字颜色", Color.white,
            new ConfigDescription(LocaleUtils.BEPINEX_NAMEPLATECOLOR_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopNamePlates,
                DispName = LocaleUtils.BEPINEX_NAMEPLATECOLOR_T.Localized(),
                Order = 1
            }),
            "名牌文字颜色", ref failed, headers);

        ShowBrokenLimbs = SetupSetting(coopDefaultNamePlatesHeader, "显示骨折", failed,
            new ConfigDescription(LocaleUtils.BEPINEX_SHOWBROKENLIMBS_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopCore,
                DispName = LocaleUtils.BEPINEX_SHOWBROKENLIMBS_T.Localized(),
                Order = 15
            }),
            "显示骨折", ref failed, headers);

        // Coop | Quest Sharing

        const string coopDefaultQuestSharingHeader = "Coop | Quest Sharing";
        var questSharingEnabled = SharedQuestProgression;

        QuestTypesToShareAndReceive = SetupSetting(coopDefaultQuestSharingHeader, "任务类型", EQuestSharingTypes.All,
            new ConfigDescription(questSharingEnabled ? LocaleUtils.BEPINEX_QUEST_TYPES_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopQuestSharing,
                DispName = LocaleUtils.BEPINEX_QUEST_TYPES_T.Localized(),
                Order = 4,
                ReadOnly = !questSharingEnabled
            }),
            "任务类型", ref failed, headers);

        QuestSharingNotifications = SetupSetting(coopDefaultQuestSharingHeader, "显示通知", true,
            new ConfigDescription(questSharingEnabled ? LocaleUtils.BEPINEX_QS_NOTIFICATIONS_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopQuestSharing,
                DispName = LocaleUtils.BEPINEX_QS_NOTIFICATIONS_T.Localized(),
                Order = 3,
                ReadOnly = !questSharingEnabled
            }),
            "显示通知", ref failed, headers);

        EasyKillConditions = SetupSetting(coopDefaultQuestSharingHeader, "简化击杀条件", false,
            new ConfigDescription(questSharingEnabled ? LocaleUtils.BEPINEX_EASY_KILL_CONDITIONS_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopQuestSharing,
                DispName = LocaleUtils.BEPINEX_EASY_KILL_CONDITIONS_T.Localized(),
                Order = 2,
                ReadOnly = !questSharingEnabled
            }),
            "简化击杀条件", ref failed, headers);

        SharedKillExperience = SetupSetting(coopDefaultQuestSharingHeader, "共享击杀经验", false,
            new ConfigDescription(questSharingEnabled ? LocaleUtils.BEPINEX_SHARED_KILL_XP_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopQuestSharing,
                DispName = LocaleUtils.BEPINEX_SHARED_KILL_XP_T.Localized(),
                Order = 1,
                ReadOnly = !questSharingEnabled
            }),
            "共享击杀经验", ref failed, headers);

        SharedBossExperience = SetupSetting(coopDefaultQuestSharingHeader, "共享首领经验", false,
            new ConfigDescription(questSharingEnabled ? LocaleUtils.BEPINEX_SHARED_BOSS_XP_D.Localized() : disabledMessage, tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopQuestSharing,
                DispName = LocaleUtils.BEPINEX_SHARED_BOSS_XP_T.Localized(),
                Order = 0,
                ReadOnly = !questSharingEnabled
            }),
            "共享首领经验", ref failed, headers);

        // Coop | Pinging

        const string coopDefaultPingingHeader = "Coop | Pinging";

        UsePingSystem = SetupSetting(coopDefaultPingingHeader, "标记系统", true,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_SYSTEM_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_SYSTEM_T.Localized(),
                Order = 11
            }),
            "标记系统", ref failed, headers);

        PingButton = SetupSetting(coopDefaultPingingHeader, "标记按键", new KeyboardShortcut(KeyCode.Semicolon),
            new ConfigDescription(LocaleUtils.BEPINEX_PING_BUTTON_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_BUTTON_T.Localized(),
                Order = 10
            }),
            "标记按键", ref failed, headers);

        PingColor = SetupSetting(coopDefaultPingingHeader, "标记颜色", Color.white,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_COLOR_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_COLOR_T.Localized(),
                Order = 9
            }),
            "标记颜色", ref failed, headers);

        PingSize = SetupSetting(coopDefaultPingingHeader, "标记大小", 1f,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_SIZE_D.Localized(),
            new AcceptableValueRange<float>(0.1f, 2f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_SIZE_T.Localized(),
                Order = 8
            }),
            "标记大小", ref failed, headers);

        PingTime = SetupSetting(coopDefaultPingingHeader, "标记持续时间", 3,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_TIME_D.Localized(),
            new AcceptableValueRange<int>(2, 10), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_TIME_T.Localized(),
                Order = 7
            }),
            "标记持续时间", ref failed, headers);

        PlayPingAnimation = SetupSetting(coopDefaultPingingHeader, "播放标记动画", false,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_ANIMATION_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_ANIMATION_T.Localized(),
                Order = 6
            }),
            "播放标记动画", ref failed, headers);

        ShowPingDuringOptics = SetupSetting(coopDefaultPingingHeader, "开镜时显示标记", false,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_OPTICS_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_OPTICS_T.Localized(),
                Order = 5
            }),
            "开镜时显示标记", ref failed, headers);

        PingUseOpticZoom = SetupSetting(coopDefaultPingingHeader, "标记使用开镜缩放", true,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_OPTIC_ZOOM_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_OPTIC_ZOOM_T.Localized(),
                Order = 4,
                IsAdvanced = true
            }),
            "标记使用开镜缩放", ref failed, headers);

        PingScaleWithDistance = SetupSetting(coopDefaultPingingHeader, "标记随距离缩放", true,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_SCALE_DISTANCE_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_SCALE_DISTANCE_T.Localized(),
                Order = 3,
                IsAdvanced = true
            }),
            "标记随距离缩放", ref failed, headers);

        PingMinimumOpacity = SetupSetting(coopDefaultPingingHeader, "标记最小不透明度", 0.05f,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_MIN_OPAC_D.Localized(),
            new AcceptableValueRange<float>(0f, 0.5f), new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_MIN_OPAC_T.Localized(),
                Order = 2,
                IsAdvanced = true
            }),
            "标记最小不透明度", ref failed, headers);

        ShowPingRange = SetupSetting(coopDefaultPingingHeader, "显示标记距离", false,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_RANGE_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_RANGE_T.Localized(),
                Order = 1
            }),
            "显示标记距离", ref failed, headers);

        PingSound = SetupSetting(coopDefaultPingingHeader, "标记音效", EPingSound.SubQuestComplete,
            new ConfigDescription(LocaleUtils.BEPINEX_PING_SOUND_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopPinging,
                DispName = LocaleUtils.BEPINEX_PING_SOUND_T.Localized(),
                Order = 0
            }),
            "标记音效", ref failed, headers);

        // Coop | Revival

        const string coopDefaultRevivalHeader = "Coop | Revival";

        GiveUpKey = SetupSetting(coopDefaultRevivalHeader, "放弃按键", new KeyboardShortcut(KeyCode.End),
            new ConfigDescription(CleanConfigString(LocaleUtils.BEPINEX_REVIVE_GIVE_UP_KEY_D.Localized()), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopRevival,
                DispName = LocaleUtils.BEPINEX_REVIVE_GIVE_UP_KEY_T.Localized(),
                Order = 0
            }),
            "放弃按键", ref failed, headers);

        // Coop | Debug

        const string coopDefaultDebugHeader = "Coop | Debug";

        FreeCamButton = SetupSetting(coopDefaultDebugHeader, "自由视角按键", new KeyboardShortcut(KeyCode.F9),
            new ConfigDescription(CleanConfigString(LocaleUtils.BEPINEX_FREE_CAM_BUTTON_D.Localized()), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopDebug,
                DispName = LocaleUtils.BEPINEX_FREE_CAM_BUTTON_T.Localized(),
                Order = 4
            }),
            "自由视角按键", ref failed, headers);

        AZERTYMode = SetupSetting(coopDefaultDebugHeader, "AZERTY 键位", false,
            new ConfigDescription(CleanConfigString(LocaleUtils.BEPINEX_AZERTY_MODE_D.Localized()), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopDebug,
                DispName = LocaleUtils.BEPINEX_AZERTY_MODE_T.Localized(),
                Order = 2
            }),
            "AZERTY 键位", ref failed, headers);

        DroneMode = SetupSetting(coopDefaultDebugHeader, "无人机模式", false,
            new ConfigDescription(LocaleUtils.BEPINEX_DRONE_MODE_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopDebug,
                DispName = LocaleUtils.BEPINEX_DRONE_MODE_T.Localized(),
                Order = 1
            }),
            "无人机模式", ref failed, headers);

        KeybindOverlay = SetupSetting(coopDefaultDebugHeader, "按键提示层", true,
            new ConfigDescription(LocaleUtils.BEPINEX_KEYBIND_OVERLAY_T.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayCoopDebug,
                DispName = LocaleUtils.BEPINEX_KEYBIND_OVERLAY_T.Localized(),
                Order = 0
            }),
            "按键提示层", ref failed, headers);

        // Network

        const string networkDefaultHeader = "Network";

        ForceIP = SetupSetting(networkDefaultHeader, "强制 IP", "",
            new ConfigDescription(LocaleUtils.BEPINEX_FORCE_IP_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_FORCE_IP_T.Localized(),
                Order = 8
            }),
            "强制 IP", ref failed, headers);

        ForceBindIP = SetupSetting(networkDefaultHeader, "强制绑定 IP", "Disabled",
            new ConfigDescription(LocaleUtils.BEPINEX_FORCE_BIND_IP_D.Localized(),
            new AcceptableValueList<string>(FikaPlugin.Instance.GetLocalAddresses()), new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_FORCE_BIND_IP_T.Localized(),
                Order = 7
            }),
            "强制绑定 IP", ref failed, headers);

        UseIPv6 = SetupSetting(networkDefaultHeader, "启用 IPv6", true,
            new ConfigDescription(
                "开（默认）：主机同时提供 IPv4+IPv6 双栈并接受 IPv6 加入地址。关：回到仅 IPv4 的旧行为。影响联机连通方式，主机侧决定。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = "Enable IPv6 | 启用 IPv6",
                Order = 50
            }),
            "启用 IPv6", ref failed, headers);

        PreferIPv6 = SetupSetting(networkDefaultHeader, "优先 IPv6", true,
            new ConfigDescription(
                "开（默认）：主机同时通告两族地址时优先选 IPv6。IPv6 没有 NAT，直连通常延迟更低。仅影响本机选路。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = "Prefer IPv6 | 优先 IPv6",
                Order = 45
            }),
            "优先 IPv6", ref failed, headers);

        SimulatedLatencyMs = SetupSetting(networkDefaultHeader, "模拟延迟（毫秒）", 0,
            new ConfigDescription(
                "给每个数据包人为加上的延迟（毫秒），0 表示关闭模拟（默认 0）。用于在本机复现依赖延迟的问题，平时保持 0。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = "Simulated Latency (ms) | 模拟延迟（毫秒）",
                Order = 20
            }),
            "模拟延迟（毫秒）", ref failed, headers);

        SimulatedPacketLossPercent = SetupSetting(networkDefaultHeader, "模拟丢包（%）", 0,
            new ConfigDescription(
                "人为丢包百分比，0 表示关闭模拟（默认 0）。用于本机复现丢包相关问题，平时保持 0。", tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = "Simulated Packet Loss (%) | 模拟丢包（%）",
                Order = 15
            }),
            "模拟丢包（%）", ref failed, headers);

        UDPPort = SetupSetting(networkDefaultHeader, "UDP 端口", (ushort)25565,
            new ConfigDescription(LocaleUtils.BEPINEX_UDP_PORT_D.Localized(), new AcceptableValueRange<ushort>(ushort.MinValue, ushort.MaxValue),
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_UDP_PORT_T.Localized(),
                Order = 6
            }),
            "UDP 端口", ref failed, headers);

        UseUPnP = SetupSetting(networkDefaultHeader, "使用端口映射", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_UPNP_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_USE_UPNP_T.Localized(),
                Order = 5,
                IsAdvanced = true
            }),
            "Use UPnP", ref failed, headers);

        UseNATPunching = SetupSetting(networkDefaultHeader, "使用 NAT 穿透", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_NAT_PUNCH_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_USE_NAT_PUNCH_T.Localized(),
                Order = 4,
                IsAdvanced = true
            }),
            "使用 NAT 穿透", ref failed, headers);

        UseFikaNATPunchServer = SetupSetting(networkDefaultHeader, "使用 Fika NAT 穿透服务器", false,
            new ConfigDescription(LocaleUtils.BEPINEX_USE_FIKA_NAT_PUNCH_SERVER_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_USE_FIKA_NAT_PUNCH_SERVER_T.Localized(),
                Order = 3,
                IsAdvanced = true
            }),
            "使用 Fika NAT 穿透服务器", ref failed, headers);

        ConnectionTimeout = SetupSetting(networkDefaultHeader, "连接超时", 30,
            new ConfigDescription(LocaleUtils.BEPINEX_CONNECTION_TIMEOUT_D.Localized(),
            new AcceptableValueRange<int>(5, 60), new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_CONNECTION_TIMEOUT_T.Localized(),
                Order = 2
            }),
            "Connection Timeout", ref failed, headers);

        SendRate = SetupSetting(networkDefaultHeader, "发送频率", ESendRate.Medium,
            new ConfigDescription(LocaleUtils.BEPINEX_SEND_RATE_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_SEND_RATE_T.Localized(),
                Order = 1
            }),
            "发送频率", ref failed, headers);

        AllowVOIP = SetupSetting(networkDefaultHeader, "允许语音", false,
            new ConfigDescription(LocaleUtils.BEPINEX_NET_VOIP_D.Localized(), tags: new ConfigurationManagerAttributes
            {
                Category = DisplayNetwork,
                DispName = LocaleUtils.BEPINEX_NET_VOIP_T.Localized(),
                Order = 0
            }),
            "允许语音", ref failed, headers);

        // Gameplay

        DisableBotMetabolism = SetupSetting("Gameplay", "Disable Bot Metabolism",
            false, new ConfigDescription(LocaleUtils.BEPINEX_DISABLE_BOT_METABOLISM_D.Localized(),
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = LocaleUtils.BEPINEX_DISABLE_BOT_METABOLISM_T.Localized(),
                Order = 1
            }),
            "Disable Bot Metabolism", ref failed, headers);

        StrictInventorySync = SetupSetting("Gameplay", "Strict Inventory Sync",
            true, new ConfigDescription(LocaleUtils.BEPINEX_STRICT_INVENTORY_SYNC_D.Localized(),
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = LocaleUtils.BEPINEX_STRICT_INVENTORY_SYNC_T.Localized(),
                Order = 2
            }),
            "Strict Inventory Sync", ref failed, headers);

        GroundLootPickupBypass = SetupSetting("Gameplay", "Ground Loot Pickup Bypass",
            false, new ConfigDescription(
                "关（2026-10-12 起的默认）：地面拾取走原生交互路径，像其他背包操作一样转发给主机。开：恢复旧的 Fika 行为——跳过原生拾取状态、自己构造背包操作（analysis/pickup-stuck-again），一次翻转即可回到旧行为、无需重编。自 2026-10-12 起也覆盖主机自己的玩家。诊断标签：[LootDiag]。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Ground Loot Pickup Bypass | 地面物资绕过原生拾取",
                Order = 60
            }),
            "Ground Loot Pickup Bypass", ref failed, headers);

        // analysis/pickup-auto-fallback (Z4): the reported bug is "the player stays in EFT.PickUpState
        // (state=Pickup, gate=Can not interact in Pickup) and every take has to be unlocked by the
        // watchdog", and it happens on the native path too. This switch is the automatic recovery:
        // the moment the watchdog proves a stuck pickup (5 s in EPlayerState.Pickup, while the native
        // take animates for well under two seconds), ground loot takes on THIS machine switch to the
        // bypass for the rest of the raid - no cfg edit, no restart, once per raid. Off is exactly the
        // behaviour before this switch existed: native takes only, stuck states only released by the
        // watchdog. Every switch is logged with [LootDiag] pickup-mode before/after lines.
        GroundLootPickupAutoFallback = SetupSetting("Gameplay", "Ground Loot Pickup Auto Fallback",
            true, new ConfigDescription(
                "开（默认）：一旦看门狗确认真的卡住拾取（本地玩家整个 5 秒预算都停在拾取状态，远长于原生拾取动画），本机本局剩余时间的地面拾取自动切到 Fika 绕过路径——不用改配置、不用重启，每局最多一次。绕过路径跳过原生拾取状态，所以卡住不会再发生。关：永不自动切换，拾取一直走原生路径，卡住只能等看门狗释放（2026-10-12 之前的行为）。切换不会带到下一局。诊断标签：[LootDiag] pickup-mode。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Ground Loot Pickup Auto Fallback | 卡拾取自动回退",
                Order = 55
            }),
            "Ground Loot Pickup Auto Fallback", ref failed, headers);

        // analysis/plant-explosive-door: the Icebreaker door plant answers "Obtain an explosive
        // charge to plant on the door" (Interactives/need_bomb_terminal) even though the charge is in
        // the backpack. The gate is EFT.GameTriggers.ItemTriggerRequirement.Check, which is handed an
        // inventory controller and one string id; the fix only widens that answer when the id was
        // found in the local player's own inventory - it never invents an item. Off: diagnosis only,
        // every [PlantDiag] line still appears.
        PlantInteractionFix = SetupSetting("Gameplay", "Plant Interaction Fix",
            true, new ConfigDescription(
                "开（默认）：当安放需求判定在本地背包（含背包内）确实有该物品却回答“不能安放”时，把答案强制改为“可以”，让安放能开始、门能被炸开。关：原样转发原生答案，只保留 [PlantDiag] 行（本版本之前的行为）。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Plant Interaction Fix | 安放交互修复",
                Order = 50
            }),
            "Plant Interaction Fix", ref failed, headers);

        // analysis/plant-item-consumption: the plant itself answers "yes" and the door is blown, but
        // the charge stays in the inventory. The native consumption sits on the plant result
        // (Player.PlantItemNetwork -> PlantItem -> the plant operation, whose cached DiscardResult is
        // executed through ItemManipulator.Discard -> RemoveOperation), and this port never runs that
        // operation for the planting machine's own controller. On (default) Fika completes that one
        // native step after a successful plant: the same ItemManipulator.Discard the native code uses,
        // wrapped in the same RemoveOperation, executed through the planting controller (the client
        // relays it to the host like every other inventory operation). Off: the inventory stays
        // untouched and only the [PlantDiag] consume lines appear.
        PlantConsumesItem = SetupSetting("Gameplay", "Plant Consumes Item",
            true, new ConfigDescription(
                "开（默认）：安放成功后执行原生消耗步骤（与游戏自己同样的移除操作），让被安放的炸药离开背包。关：背包不变，只保留 [PlantDiag] consume 行。一次翻转即可回退（analysis/plant-item-consumption）。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Plant Consumes Item | 安放消耗物品",
                Order = 45
            }),
            "Plant Consumes Item", ref failed, headers);

        // AY (analysis/plant-client-host-crash): the plant gate (EFT.GameTriggers.ItemTriggerRequirement
        // .Check / .TryRedeem) is answered twice by the map handler for one plant - proven in the host
        // run (analysis/plant-item-consumption quotes logs-cgok-20261013-004053/LogOutput.log:3319-3324,
        // "op=TryRedeem -> result=True (x2)") and again by the two identical sends in
        // logs-coop2-20261013-110333/LogOutput.log:2931-2932. The consume repair dispatches its removal
        // through the controller, which is asynchronous, so both calls used to find the charge still in
        // the inventory and both sent it: the host applied the first and refused the second, and that
        // refusal resynced the client's whole item graph and aborted the plant mid-animation. On
        // (default) a client keeps relaying its own removal - exactly once, through the ordinary
        // inventory-operation relay that the host is proven to apply - so a client's plant consumes its
        // charge again. Off: the removal stays strictly on the host (this machine's own plant); a client
        // never sends one, which is the way to keep the host's relay traffic at zero even if a future
        // build turns out to be unable to handle a relayed removal.
        PlantConsumeOnClient = SetupSetting("Gameplay", "Plant Consume On Client",
            true, new ConfigDescription(
                "开（默认）：客机安放成功后，把自己的消耗移除精确转发一次给主机，炸药离开背包、两端一致。关：消耗只在主机自己做；客机安放后炸药留在背包、也不发移除（种植修复前的行为）。证据：[PlantDiag] consume / [PlantGuard] host-accept-removal。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Plant Consume On Client | 客机转发消耗",
                Order = 44
            }),
            "Plant Consume On Client", ref failed, headers);

        // AY: the host side of the same repair - a removal this host can already tell it cannot
        // complete is refused in managed code with one [PlantGuard] warning and the ordinary
        // Failed + resync answer, before the native rebuild of the operation is entered.
        PlantRelayGuard = SetupSetting("Gameplay", "Plant Relay Guard",
            true, new ConfigDescription(
                "开（默认）：主机在重建客机发来的移除操作前先自检能否完成（描述符可读、且不是刚接受过的重复），失败则按普通拒绝回答（Failed + 权威背包）并写一行 [PlantGuard]。关：所有描述符原样进入原生重建（本版本之前的行为）。证据：[PlantGuard] 行，analysis/plant-client-host-crash。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Plant Relay Guard | 主机中继守卫",
                Order = 43
            }),
            "Plant Relay Guard", ref failed, headers);

        KeypadRelayClientSubmit = SetupSetting("Gameplay", "Keypad Relay Client Submit",
            true, new ConfigDescription(
                "开（默认）。统管整条密码门链：① 由主机按原生方式在战局内生成码表并推给客机（主机自己没有控制器时也按原生构造一个，否则主机自己也会报 Tried to enter code on location without passcodes）；② 客机装一个本地原生控制器、用主机那套码在本地判定对错（不再让 Fika 决定对错）；③ 万一表还没到、客机就先提交了，才用既有的密码尝试包转给主机兜底。关：整条链停（等同于改动前：客机无法输入密码，主机输码也可能报错）。判据：[KeypadDiag] 的 stage=codes/keys/submit/broadcast 行，analysis/icebreaker-keypad-3。",
            tags: new ConfigurationManagerAttributes
            {
                Category = DisplayGameplay,
                DispName = "Keypad Relay Client Submit | 密码门提交中继",
                Order = 41 // (config-audit-2) was 44, the slot Plant Consume On Client already claims
            }),
            "Keypad Relay Client Submit", ref failed, headers);


        Fika.Core.Main.Utils.KeypadDiag.RepairEnabled = KeypadRelayClientSubmit.Value;
        KeypadRelayClientSubmit.SettingChanged += (_, _) =>
            Fika.Core.Main.Utils.KeypadDiag.RepairEnabled = KeypadRelayClientSubmit.Value;

        TransitSuppressCoLocatedExits = SetupSetting(coopDefaultHeader, "转移：抑制同位撤离点", true,
            new ConfigDescription(
                "开（默认）：当转移点与撒离点重叠在同一触发器上时，从转移交互被接受到它完成或取消期间屏蔽该撒离点，避免倒计时跑完却什么都没发生（Factory 案例）。关：只写日志，不做任何处理。主客机都生效，改完立即生效。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Suppress Co-Located Exfiltration | 镇压同位撒离点",
                    Order = 95
                }),
            "Transit: Suppress Co-Located Exfiltration", ref failed, headers);

        // Static field, same pattern as the other transit switches: sync at startup and on every change.
        Fika.Core.Main.HostClasses.TransitCoLocatedExits.SuppressCoLocatedExits = TransitSuppressCoLocatedExits.Value;
        TransitSuppressCoLocatedExits.SettingChanged += (_, _) =>
            Fika.Core.Main.HostClasses.TransitCoLocatedExits.SuppressCoLocatedExits = TransitSuppressCoLocatedExits.Value;

        TransitAbortWhenSomeoneLeaves = SetupSetting(coopDefaultHeader, "转移：有人离开则取消", true,
            new ConfigDescription(
                "开（默认）：倒计时期间已登记的队友走出转移点，则取消倒计时并终止转移（原生行为）。关：倒计时继续跑，与修复前一致。作用于战局内转移逻辑，主机侧生效。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Abort When Someone Leaves | 有人离开则取消",
                    Order = 90
                }),
            "Transit: Abort When Someone Leaves", ref failed, headers);

        Fika.Core.Main.HostClasses.TransitCountdownGuard.AbortWhenSomeoneLeaves = TransitAbortWhenSomeoneLeaves.Value;
        TransitAbortWhenSomeoneLeaves.SettingChanged += (_, _) =>
            Fika.Core.Main.HostClasses.TransitCountdownGuard.AbortWhenSomeoneLeaves = TransitAbortWhenSomeoneLeaves.Value;
        // (analysis/config-audit-2) 'Transit: Abort When Someone Dies' retired: the field's own default
        // (false = the native shape, the dead drop out and the survivors carry on) is what runs below.
        Fika.Core.Main.HostClasses.TransitCountdownGuard.AbortWhenSomeoneDies = false;

        // (analysis/config-audit-2) 'Rally: Use Fika Player Count' is retired: the real player count is the
        // shipped behaviour (FikaRallyGate.UseFikaPlayerCount = true), because a story trigger that waits for
        // the native bookkeeping number deadlocks the party (analysis/rally-gate-deadlock).

        // (analysis/config-audit-2) The cutscene family converged: the entries below are retired, the
        // behaviour stays with the components (see the note on the property declarations).

        // (analysis/config-audit-2) The converged cutscene path as one static block. Every value below is the
        // shipped default of the entry this round retired, so the components behave exactly as before while the
        // F12 list carries a single cutscene switch. Rollback of any single line = re-bind that key.
        // The retired experiment 'Cutscene: Forward AfterPlayerTick' stays off: its own note says the native body
        // it forwards to re-runs work this port already took over (airdrop points, synchronizable objects).

        // (analysis/config-audit-2) 'Rally: Allow Skip Without All Viewers' is retired too: a viewer that never
        // signalled must not hold the skip vote forever (that is the host deadlock of the same analysis), so the
        // shipped behaviour is on and the timeout below is the single knob that decides when it applies.



        InertOutsideFikaRaids = SetupSetting(coopDefaultHeader, "非 Fika 战局中不生效", true,
            new ConfigDescription(
                "开（默认）：本端口所有战局内钩子（物资掉落、弹匣卸载、世界与集合点交互、过场诊断、逐帧采样）在非 Fika 战局（从地图选择界面直接开的单人战局）里完全不介入，每个被挡回的钩子写一行 [Inert] skip=... reason=not-a-fika-raid side=single。关：回到修复前的行为——补丁在原生单人战局里也会动作（2026-10-12 23:52 那次崩溃就是在这种状态下打的）。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopCore,
                    DispName = "Inert Outside Fika Raids | 非 Fika 战局内静默",
                    Order = 50
                }),
            "非 Fika 战局中不生效", ref failed, headers);

        Fika.Core.Main.Utils.FikaRaidGate.InertOutsideFikaRaids = InertOutsideFikaRaids.Value;
        InertOutsideFikaRaids.SettingChanged += (_, _) =>
            Fika.Core.Main.Utils.FikaRaidGate.InertOutsideFikaRaids = InertOutsideFikaRaids.Value;

        // Coop | Boss & Spawns. The three switches the host's raid preparation reads
        // (Main.GameMode.HostGameController); they are static fields, so they are kept in sync here and on
        // every change, the same pattern the transit gate uses. All three are decided when a raid is
        // prepared, i.e. a change applies to the next raid; nothing here is read per frame.
        // (retired, analysis/boss-wedge-vanilla) 'Boss: One Boss Per Trigger Point' is gone: the user asked
        // for the vanilla mechanic again after the boss failed to spawn in co-op. The host now reports the
        // shipped wave list unchanged (HostGameController.CollapseBossWavesPerTrigger = false).

        MirrorOfflineSpawnFlags = SetupSetting(coopDefaultHeader, "出生：镜像离线标志", true,
            new ConfigDescription(
                "开：按原生离线准备镜像 OfflineOldSpawn/OfflineNewSpawn 与 WAVE_COEF_* 缩放，让“是否放行刷新”的判断与无头主机及原版游戏一致（它本身不决定刷不刷）。关：使用未镜像的原始标志。排障用，平时保持开启。主机侧开关，准备战局时读取。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopBossSpawns,
                    DispName = "Spawn: Mirror Offline Flags | 镜像离线刷新标志",
                    Order = 90
                }),
            "Spawn: Mirror Offline Flags", ref failed, headers);

        EnableAmbientBotCapFallback = SetupSetting(coopDefaultHeader, "出生：环境杂兵兜底上限", false,
            new ConfigDescription(
                "关（默认）：地图数据的 BotMax 保持原值，破冰船 BotMax=0 因此不刷常规杂兵（符合“不动原版刷新”）。开：仅当 BotMax<=0 且 BotStop>0 时兜底 SPT 风格的上限（18，仍受本局机器人预算限制），该图会开始刷环境杂兵；一般不要开。排障用，主机侧开关。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopBossSpawns,
                    DispName = "Spawn: Ambient Bot Cap Fallback | 环境杂兵兜底上限",
                    Order = 80
                }),
            "Spawn: Ambient Bot Cap Fallback", ref failed, headers);

        Fika.Core.Main.GameMode.HostGameController.CollapseBossWavesPerTrigger = false;
        Fika.Core.Main.GameMode.HostGameController.MirrorOfflineSpawnFlags = MirrorOfflineSpawnFlags.Value;
        Fika.Core.Main.GameMode.HostGameController.EnableAmbientBotCapFallback = EnableAmbientBotCapFallback.Value;
        MirrorOfflineSpawnFlags.SettingChanged += (_, _) =>
            Fika.Core.Main.GameMode.HostGameController.MirrorOfflineSpawnFlags = MirrorOfflineSpawnFlags.Value;
        EnableAmbientBotCapFallback.SettingChanged += (_, _) =>
            Fika.Core.Main.GameMode.HostGameController.EnableAmbientBotCapFallback = EnableAmbientBotCapFallback.Value;

        // Debug | Diagnostics (analysis/coop-perf). FrameCostProbe measured Fika's own frame work; the
        // probe is an OR of this switch and the 'Diagnostics' master switch (FrameCostProbe.On), so this
        // entry is the "only this one line, without the whole probe batch" switch. The field is read on
        // every entry point, so applying it here and on every change keeps the config and the probe in
        // step without a restart.


        // (analysis/stutter-pose-reset) The write-side probe of the same investigation. Same contract as the
        // frame-cost probe: its own switch, or the 'Diagnostics' master switch, and live on change - so it can be
        // flipped in the middle of a raid without a restart and the next window already carries the line.


        // (analysis/stutter-frame-skip) The one knob of the network-delivery candidate: 0 keeps the unbounded
        // drain (default, unchanged behaviour), a positive value bounds how many arrived events one frame
        // delivers and defers the rest to the next frame, in order. It is read when a client starts, so it
        // takes effect with the next raid.
        NetPollBudget = SetupSetting(coopDefaultHeader, "每帧最大网络事件数", 96,
            new ConfigDescription(
                "默认 96（实测一帧曾一次投递 185 条事件）。一帧最多投递这么多条已到达的网络事件（含每包的解析与回调），这一帧没处理完的留在队列里，下一帧按原顺序继续投递——不丢包、不乱序，只是把「一次网络突发全压在一帧」摊到多帧，去掉那个 100ms 级的大帧。判据：开「Stutter Probe」看 clientUpdate 的 max（应从 ~164ms 降到 <60ms）与 [Stutter] 串时长、[NetPoll] 行的 bursts/max/deferredTotal。**设为 0 = 完全回到改动前行为**（一键回退）；觉得还卡可以再降到 48 试。下一个战局生效。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayDebugDiagnostics,
                    DispName = "Net Maximum Events Per Frame | 单帧网络事件上限",
                    Order = 12
                }),
            "每帧最大网络事件数", ref failed, headers);

        // (analysis/stutter-frame-skip) The journal of the 「被抽帧」 shape: runs of consecutive long frames,
        // their rhythm, the collections inside them and the measured price of our own main-thread file IO.
        // Same contract as the other probes: own switch, or the 'Diagnostics' master switch.


        // (analysis/stutter-pose-reset) The guards themselves, on by default. This is the one-click rollback
        // the user asked for: switching it off restores the pre-guard behaviour exactly (every site writes on
        // every frame again) without a rebuild, and it makes the A/B measurable in a single raid - guards off,
        // guards on, same map, same bot count, same [PoseWrite] line.





        FinalMissionEndingRepair = SetupSetting(coopDefaultHeader, "终局任务：结局修复", true,
            new ConfigDescription(
                "默认开。终点站结局（救世主结局等）的修复：完成信号两种 id 都接受（原生送的是场景名，不是 cutsceneId）、结局场景的采样/救援/收尾看门狗、完成确认重发，以及把 CoopGame.Stop 的 DeferStop 在 90 秒后强制放行——否则结局界面会永久卡住、不结算。关：回到改动前行为（仍会写 [FinalDiag] 判据）。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayDebugDiagnostics,
                    DispName = "Final Mission: Ending Repair | 结局结算修复",
                    Order = 21
                }),
            "终局任务：结局修复", ref failed, headers);

        // (analysis/stutter-frame-skip) The session log copy is the only periodic file IO this port performs
        // on the game's own thread; on (default) it runs on the archive's own thread and the frame only
        // raises a flag. Off is the one-click rollback and the in-raid A/B of that change.

        // (analysis/config-audit-2) This probe now needs the master switch as well, exactly like the
        // [NetTimeout] line: 'Diagnostics' off means every probe family is silent (the contract the perf cleanup
        // wrote down), instead of one family writing a line per second regardless.
        // The retired 'Cutscene: Real Time Ticks' keeps the behaviour of its default (the BF slow-motion fix).
        Fika.Core.Main.Components.FikaFinalMissionDirector.EndingRepair = FinalMissionEndingRepair.Value;
        FinalMissionEndingRepair.SettingChanged += (_, _) => Fika.Core.Main.Components.FikaFinalMissionDirector.EndingRepair = FinalMissionEndingRepair.Value;

        // (2026-10-26 W15) Coop / Pose Write Guards 接回（上游 3.0.4 同款开关）：关掉即完全恢复"每帧都写"的改前行为，
        // 方便同一局里做 A/B；默认开。开关只写本机静态字段，不参与任何联机协议。
        PoseWriteGuards = SetupSetting(coopDefaultHeader, "Pose Write Guards", true,
            new ConfigDescription(
                "默认开。开：观察者玩家的姿态/移动/动画写入加「值未变就不写」守卫，避免每帧重复推 animator。关：完全恢复改前行为（每个写入点每帧都写），只多一次静态 bool 读取。本机生效。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayDebugDiagnostics,
                    DispName = "Pose Write Guards | 姿态写入守卫",
                    Order = 15
                }),
            "Pose Write Guards", ref failed, headers);

        Fika.Core.Main.Utils.PoseWriteGuards.Enabled = PoseWriteGuards.Value;
        PoseWriteGuards.SettingChanged += (_, _) => Fika.Core.Main.Utils.PoseWriteGuards.Enabled = PoseWriteGuards.Value;

        SentrySyncCardAccess = SetupSetting(coopDefaultHeader, "哨兵：同步刷卡放行", true,
            new ConfigDescription(
                "默认开。终点站哨兵（RAUF）的「刷卡放行」原本只存在于主机那台机器的哨兵集合里，客机永远被当成敌人——本条把这条事实按原生形状同步给每台机器（对每台机器的哨兵调用原生 OnTerminalAccessGained，并用原生 HaveAccess 复核）。关：只写 [SentryDiag] 判据，不做同步（改动前行为）。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayDebugDiagnostics,
                    DispName = "Sentry: Sync Card Access | 哨兵刷卡放行同步",
                    Order = 22
                }),
            "Sentry: Sync Card Access", ref failed, headers);

        Fika.Core.Main.Components.FikaSentryAccess.RepairEnabled = SentrySyncCardAccess.Value;
        SentrySyncCardAccess.SettingChanged += (_, _) =>
            Fika.Core.Main.Components.FikaSentryAccess.RepairEnabled = SentrySyncCardAccess.Value;

        // (analysis/client-transit-terminal) The co-op transit stall: the client's press produced no request on its
        // own machine, so the host never received it and its "everyone interacted" gate rejected the whole squad.
        TransitClientPressFallback = SetupSetting(coopDefaultHeader, "转移：客机按下兜底", true,
            new ConfigDescription(
                "默认开。客机在转移点按下后 2.5 秒内本机没有产出、主机也没有受理时，由客机用同一个原生构造器重发一次等价请求（前提与原生一致：在点内、有访问权、满足入场条件、处于 idle 状态），每次按下最多一次、每局最多 8 次；主机的全部门照旧，不做任何绕过。关：只写判据行（改动前行为）。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Client Press Fallback | 客机转移请求补发",
                    Order = 56
                }),
            "转移：客机按下兜底", ref failed, headers);

        // (analysis/config-audit-2) The retired 'Transit: Press Fallback Limit' keeps its shipped bound
        // (TransitRecovery.PressFallbackLimit = 8). The merged client stash switch is wired where
        // 'Transit: Per-Player Transfer Stash' is bound below - never here, the entry is still null at this
        // point of SetupConfig (the binding-order regression of analysis/config-binding-order-regression).
        TransitClientPressFallback.SettingChanged += (_, _) =>

        // Coop | Cutscene. The client's in-place trigger (Main.Components.FikaLocalCutsceneTrigger) is the
        // client half of "the client plays the cutscene too": it plays the dropped native start locally, and it
        // stands down for any cutscene the host dispatched (that packet is recorded on arrival, before the local
        // request is looked at). The hold-back delay is 0 on purpose (2026-10-27 T5): a client that waits does
        // not avoid a double start, it only delays the cutscenes that exist on this machine alone.
        // Retired entries (analysis/config-audit-2), shipped behaviour as one static block.
        Fika.Core.Main.Components.FikaLocalCutsceneTrigger.UseNativeClientTrigger = true;
        Fika.Core.Main.Components.FikaLocalCutsceneTrigger.SingleLocalViewer = true;
        Fika.Core.Main.Components.FikaLocalCutsceneTrigger.LocalTriggerDelaySeconds = 0f;

        // Coop | Transit, transit recovery round. The three client-side switches are read where a refusal is
        // handled and the stash switch where a player enters the destination, so applying them here and on
        // every change keeps a running session in step.

        TransitRearmAfterRefusal = SetupSetting(coopDefaultHeader, "转移：被拒后重新就绪", true,
            new ConfigDescription(
                "开（默认）：客机被主机拒绝后，把这一轮被拒的后果一次收拾干净——复位本机按键闩锁（可以立刻再按一次；修复前提示不清也无法再按，观感就是「卡在互动」），并重报一次「我在点内」，避免主机把客机当成已经离开转移点。两件事都由这一个开关控制（config-audit-2 把重复的「Reassert Presence After Refusal」并了进来）。关：两件事都不做（修复前行为）。客机侧开关。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Rearm After Refusal | 被拒后恢复（按键 + 在点内重报）",
                    Order = 54
                }),
            "Transit: Rearm After Refusal", ref failed, headers);

        TransitPerPlayerTransferStash = SetupSetting(coopDefaultHeader, "转移：按玩家转移容器", true,
            new ConfigDescription(
                "开（默认）：进入终点站时给每个玩家（含客机）补建“装备转移容器”——仅在缺失或尺寸为 0×0 时补建，绝不清空已有内容，也就是原生 OnPlayerEnter 该做的事；关：回到旧行为（客机拿不到转移容器）。主机侧开关。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Per-Player Transfer Stash | 每人补建转移容器",
                    Order = 52
                }),
            "Transit: Per-Player Transfer Stash", ref failed, headers);

        TransitActivateCountdownPanel = SetupSetting(coopDefaultHeader, "转移：唤醒倒计时面板", true,
            new ConfigDescription(
                "默认开。转移倒计时开始时，主动把转移计时面板（LocationTransitTimerPanel）自己打开一次（只开面板本身；祖先没开就只写判据、不强行打开），让客机也能看到倒计时。关：只写判据行（改动前行为）。离开点仍然隐藏并暂停，回来继续。",
                tags: new ConfigurationManagerAttributes
                {
                    Category = DisplayCoopTransit,
                    DispName = "Transit: Activate Countdown Panel | 唤醒转移倒计时面板",
                    Order = 59
                }),
            "Transit: Activate Countdown Panel", ref failed, headers);


        // (analysis/config-audit-2) The retired 'Transit: Reassert Presence After Refusal' was the second half
        // of the same "clean up after a refusal" repair, so it follows this one switch.
        // (analysis/config-audit-2) The retired 'Transit: Client Transfer Stash' was the same container repair
        // on the client machine, so both halves follow this one switch now (hot, like the rest of this block).

        if (failed)
        {
            var headerString = string.Join(", ", headers);
            Singleton<PreloaderUI>.Instance.ShowErrorScreen(LocaleUtils.UI_LOCALE_ERROR_HEADER.Localized(),
                string.Format(LocaleUtils.UI_LOCALE_ERROR_DESCRIPTION.Localized(), headerString));
            FikaGlobals.LogError("SetupConfig: Headers failed: " + headerString);
        }

        // (analysis/crash-dump-20261013) The other half of the begin/end pair. Missing 'end' with a present
        // 'begin' = the process died inside the bindings; 
        SetupConfigEventHandlers();
    }

    /// <summary>
    /// (analysis/config-audit-2, 2026-10-13) The settings this round retired: each one either duplicated
    /// another key, had no read point left that could change a raid, or was only reachable in a bisect
    /// configuration. ConfigFile.Remove also drops the key from an already existing com.fika.core.cfg -
    /// without that step the file (and the F12 menu, which lists every entry of the file) would keep an
    /// orphan that still looks like a live switch. The behaviour of every retired key survives as the static
    /// default of the component that read it, so nothing a raid does changes; re-binding a key is the
    /// rollback. build: see analysis/config-audit-2/REPORT.md for the old-key -> new-key table.
    /// </summary>
    private void RetireRemovedEntries()
    {
        string[][] retired =
        [
            ["Coop", "Show Final Countdown Screen"],
            ["Coop", "Raid Start Audio Fades"],
            ["Coop", "Raid Start Menu Music"],
            ["Coop", "Countdown: Activate Game UI"],
            ["Coop", "Countdown: Bind Extraction Timer Panel"],
            ["Coop", "Countdown: Deploy Wait"],
            ["Coop", "Countdown: Restore Raid View"],
            ["Coop", "Countdown: Start Raid View Routine"],
            ["Coop", "Countdown: Sync Transit Controllers"],
            ["Coop", "Countdown: Dispatch Raid Started Event"],
            ["Coop", "Countdown: VOIP Audio Fix"],
            ["Coop", "Countdown: Opening Countdown Overlay"],
            ["Coop", "Countdown: Opening Countdown Close"],
            ["Coop", "Countdown: Opening Countdown View Restore"],
            ["Coop", "Countdown: Opening Countdown Reassert"],
            ["Coop", "Cutscene: Rescue Local Scene"],
            ["Coop", "Cutscene: Forward AfterPlayerTick"],
            ["Coop", "Cutscene: Repair Bridge Playback"],
            ["Coop", "Cutscene: Dismiss Skip Overlay"],
            ["Coop", "Cutscene: Join Cutscenes Started By The Host"],
            ["Coop", "Cutscene: Accept Signals From Unlisted Viewers"],
            ["Coop", "Cutscene: Every Participant Joins The Cutscene"],
            ["Coop", "Cutscene: Client Triggers Locally"],
            ["Coop", "Cutscene: Client Trigger Single Viewer"],
            ["Coop", "Cutscene: Client Trigger Delay Seconds"],
            ["Coop", "Cutscene: Real Time Ticks"],
            ["Coop", "Rally: Use Fika Player Count"],
            ["Coop", "Rally: Allow Skip Without All Viewers"],
            ["Coop", "Transit: Abort When Someone Dies"],
            ["Coop", "Transit: Press Fallback Limit"],
            ["Coop", "Transit: Client Transfer Stash"],
            ["Coop", "Transit: Reassert Presence After Refusal"],
            ["Coop", "Commit Watch Hard Release"],
            // (analysis/remove-reconnect, 2026-10-14) The disconnect-reconnect feature is gone: its two
            // keys are dropped from existing configuration files so no orphan switch stays in the F12 menu.
            ["Coop", "Reconnect: Rejoin After Disconnect"],
            ["Coop", "Reconnect: Settle Expired Rejoin"],
            // (analysis/boss-wedge-vanilla) The user asked for the vanilla boss mechanic again: with the
            // collapse on, the boss did not spawn at all in co-op, so the shipped wave list is used as-is.
            ["Coop", "Boss: One Boss Per Trigger Point"],
            // (analysis/stutter-frame-skip) The key was renamed so the new default (96) reaches every existing
            // configuration file: the old one stored 0, which is the "no budget" behaviour the frame dumps showed
            // to be the cause of the 100 ms+ clientUpdate spikes.
            ["Coop", "Net Poll Budget"],
            // (2026-10-14, diagnostics removal) Every diagnostics/forensics switch of this port is gone:
            // the port writes no Fika-diagnostics.log and no crash trail any more, so the keys are dropped from
            // existing configuration files too (the F12 menu and every future cfg stay free of dead switches).
            ["Coop", "Net Timeout Diagnostic"],
            ["Coop", "Main Menu Ref Trace"],
            ["Coop", "Diagnostics Verbose"],
            ["Coop", "Diagnostics"],
            ["Coop", "Crash Forensics"],
            ["Coop", "Commit Watch"],
            ["Coop", "Commit Watch Warn MiB"],
            ["Coop", "Commit Watch Critical MiB"],
            ["Coop", "Commit Watch Relief"],
            ["Coop", "Skip Countdown Body"],
            ["Coop", "Skip Post Wait Block"],
            ["Coop", "Frame Cost Probe"],
            ["Coop", "Pose Write Probe"],
            ["Coop", "Stutter Probe"],
            ["Coop", "Log Archive"],
            ["Coop", "Mirror Diagnostics"],
            ["Coop", "Cutscene: Sync Probe"],
            ["Coop", "Log Archive Async"],
            ["Coop", "Transit: Verdict Log Lines"],
            ["Gameplay", "Keypad Diagnostics"],
        ];

        var removed = 0;
        foreach (var entry in retired)
        {
            try
            {
                if (_config.Remove(new ConfigDefinition(entry[0], entry[1])))
                {
                    removed++;
                }
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"Could not retire the setting {entry[0]} / {entry[1]}: {exception.Message}");
            }
        }

        if (removed > 0)
        {
            
        }
    }

    private void SetupConfigEventHandlers()
    {
        OfficialVersion.SettingChanged += OfficialVersion_SettingChanged;
    }

    private void OfficialVersion_SettingChanged(object sender, EventArgs e)
    {
        FikaVersionLabel_Patch.UpdateVersionLabel();
    }

    private string CleanConfigString(string header)
    {
        var original = string.Copy(header);
        var foundForbidden = false;
        foreach (var character in (char[])['\n', '\t', '\\', '\"', '\'', '[', ']'])
        {
            if (header.Contains(character))
            {
                FikaGlobals.LogWarning($"Header '{original}' contains an illegal character: {character}\nReport this to the developers!");
                header = header.Replace(character, char.MinValue);
                foundForbidden = true;
            }
        }

        if (foundForbidden)
        {
            FikaGlobals.LogWarning($"Header '{original}' was changed to '{header}'");
        }
        return header;
    }

    internal void GetClientConfig()
    {
        var clientConfig = FikaRequestHandler.GetClientConfig();

        UseBTR = clientConfig.UseBTR;
        FriendlyFire = clientConfig.FriendlyFire;
        DynamicVExfils = clientConfig.DynamicVExfils;
        AllowFreeCam = clientConfig.AllowFreeCam;
        AllowSpectateFreeCam = clientConfig.AllowSpectateFreeCam;
        AllowItemSending = clientConfig.AllowItemSending;
        BlacklistedItems = clientConfig.BlacklistedItems;
        ForceSaveOnDeath = clientConfig.ForceSaveOnDeath;
        UseInertia = clientConfig.UseInertia;
        SharedQuestProgression = clientConfig.SharedQuestProgression;
        CanEditRaidSettings = clientConfig.CanEditRaidSettings;
        EnableTransits = clientConfig.EnableTransits;
        AnyoneCanStartRaid = clientConfig.AnyoneCanStartRaid;
        AllowNamePlates = clientConfig.AllowNamePlates;
        RandomLabyrinthSpawns = clientConfig.RandomLabyrinthSpawns;
        PMCFoundInRaid = clientConfig.PMCFoundInRaid;
        AllowSpectateBots = clientConfig.AllowSpectateBots;
        InstantLoad = clientConfig.InstantLoad;
        FastLoad = clientConfig.FastLoad && !InstantLoad; // can only use one, prioritize InstantLoad
        ReviveConfig = clientConfig.ReviveConfig;

        // (2026-10-20) The revive patches do not exist in this tree; the config block is gone with them.

        clientConfig.LogValues();
    }
}
