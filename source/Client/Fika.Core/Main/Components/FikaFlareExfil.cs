using CommonAssets.Scripts.Game;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.GameTriggers;
using EFT.GlobalEvents;
using EFT.Interactive;
using EFT.Interactive.SecretExfiltrations;
using EFT.InventoryLogic;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;
using System;
using System.Collections.Generic;

namespace Fika.Core.Main.Components;

/// <summary>
/// Activation of the green ("ExitActivate") flare exfiltration points, carried over the wire
/// together with the shooter.
/// <para>
/// Native model (verified against the 47888 interop member dump, see
/// analysis/flare-exfil-verification): a green flare is <c>FlareEventType.ExitActivate</c>; its
/// success raises <c>FlareSuccessEvent</c> locally, and every
/// <see cref="FlareShootDetectorZone"/> the shooter stands in records the firing player's
/// profile id in <c>_shotPlayerProfiles</c>. The point then answers per player through
/// <c>ExfiltrationRequirement.Met(Player, ExfiltrationPoint)</c>. Vanilla also has a squad
/// channel for the same bookkeeping (<c>EZoneEventType.PlayerByPartyAddedInShotList</c>), which
/// is the default here.
/// </para>
/// <para>
/// What Fika was missing: a client's flare never replayed that local work on the host, so the
/// client's flare activated nothing, while the point-level state Fika did replicate made the
/// point usable for peers that never fired. This class carries the activation with the shooter
/// (and, by default, the squad), keeps the host authoritative allow list and refuses extraction
/// for every player the grant does not cover.
/// </para>
/// <para>
/// A green flare has two vanilla jobs and they are kept apart here: opening an exit
/// ("exit-gate", the Customs flare exits) and disabling the map sniper ("immunity", Ground Zero's
/// <c>Sniper_exit</c>, whose exit carries <c>PassageRequirement: None</c> and whose whole
/// <c>/EXITS/FlareFireCollector_zone</c> area is blocked by sniper fire instead). An immunity zone
/// never touches the exit status: the covered players' raid ids go into the native
/// <c>PlayersWithImmuneToSniperFire</c> set on every machine, which is the set the sniper zones
/// read through <c>IsPlayerImmuneForFire(Int32)</c>.
/// </para>
/// <para>
/// Switch: <c>Flare Extract: Squad Shares Activation</c> (default on = vanilla party channel).
/// Off binds the activation to the shooter only.
/// </para>
/// </summary>
public static class FikaFlareExfil
{
    /// <summary>Farther than this from the reported flare position the host does not accept the point.</summary>
    private const float PointRange = 120f;

    /// <summary>Without a collider on the detector zone the flare position is compared to its origin.</summary>
    private const float ZoneRange = 150f;

    /// <summary>Late joiners and lost packets pick the current grants up on the next tick.</summary>
    private const float RepublishInterval = 10f;

    /// <summary>Signature written with every status change this class makes, so the native side can attribute it.</summary>
    private const string NativeSignature = "FikaFlareExfil";

    private static readonly object Sync = new();

    /// <summary>Point name -&gt; profiles allowed to use it. Written on the host, mirrored on clients from grants.</summary>
    private static readonly Dictionary<string, HashSet<string>> AllowedProfilesByPoint = new(StringComparer.Ordinal);

    /// <summary>Points that received at least one flare activation in this raid. Only those are gated.</summary>
    private static readonly HashSet<string> GatedPoints = new(StringComparer.Ordinal);

    /// <summary>Grants as the host issued them, kept for republication.</summary>
    private static readonly List<GrantRecord> Grants = [];

    /// <summary>Per-line diagnostics dedup: one line per player, point and event.</summary>
    private static readonly HashSet<string> LoggedLines = new(StringComparer.Ordinal);

    /// <summary>Status of every point when the raid bound it, before any Fika replication ran.</summary>
    private static readonly Dictionary<string, EExfiltrationStatus> InitialStatuses = new(StringComparer.Ordinal);

    /// <summary>Points that a green flare detector zone covers and that start out awaiting manual activation.</summary>
    private static readonly HashSet<string> ClassifiedGated = new(StringComparer.Ordinal);

    /// <summary>Exit name -> the green flare (ExitActivate) detector zone that activates it.</summary>
    private static readonly Dictionary<string, string> FlareZoneByPoint = new(StringComparer.Ordinal);

    /// <summary>Green flare detector zone id -> the exit it activates, resolved when the raid binds the exits.</summary>
    private static readonly Dictionary<string, string> PointByFlareZone = new(StringComparer.Ordinal);

    /// <summary>Exit name -> distance to its green flare detector zone, for the once per raid table.</summary>
    private static readonly Dictionary<string, float> FlareZoneDistanceByPoint = new(StringComparer.Ordinal);

    /// <summary>
    /// Green flare detector zone id -> the work its flare does (open an exit or disable the sniper).
    /// Filled once per raid by the classification pass, so the grant path does not have to re-derive
    /// the verdict from the scene.
    /// </summary>
    private static readonly Dictionary<string, ZoneKind> ZoneKinds = new(StringComparer.Ordinal);

    /// <summary>
    /// Per key state of a failure, so that its start and its end are both reported.
    /// <para>
    /// <see cref="LogOnce"/> is right for a one-shot hint but it would swallow "failed first, then
    /// recovered": the raid would keep showing the old line and nobody could tell whether the next
    /// attempt worked. Every failure path that can heal goes through <see cref="LogFailure"/> instead.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, bool> Failing = new(StringComparer.Ordinal);

    /// <summary>
    /// Zone id + profile id pairs the success feedback was already shown for in this raid. A grant is
    /// republished every <see cref="RepublishInterval"/> seconds, so without this the notification
    /// would pop again on every republication.
    /// </summary>
    private static readonly HashSet<string> FeedbackShown = new(StringComparer.Ordinal);

    /// <summary>
    /// Seconds a success popup stays on screen before this class takes it down again. Read live from
    /// the switch 'Flare Extract: Feedback Seconds', so a change in the config manager applies to the
    /// next popup without a restart. 0 or less turns the automatic take-down off and leaves the banner
    /// on screen, which is how every build before this one behaved.
    /// </summary>
    internal static float FeedbackSeconds
    {
        get
        {
            try
            {
                var entry = FikaPlugin.Instance?.Settings?.FlareExfilFeedbackSeconds;
                return entry == null ? DefaultFeedbackSeconds : entry.Value;
            }
            catch (Exception)
            {
                return DefaultFeedbackSeconds;
            }
        }
    }

    /// <summary>
    /// Popups this class put on screen and still has to take off again, each with the game time it
    /// goes away at.
    /// <para>
    /// A list rather than a single slot: a second activation can pop a banner while the first one is
    /// still up, and the older popup must neither be left on screen by the newer one nor cut short by
    /// the newer one's deadline. Every entry carries its own notification object, so taking one down
    /// can never touch another.
    /// </para>
    /// </summary>
    private static readonly List<FeedbackPopup> FeedbackPopups = new();

    /// <summary>Display time used when the switch cannot be read (missing entry, no plugin instance).</summary>
    private const float DefaultFeedbackSeconds = 5f;

    private static bool _classificationLogged;

    private static System.IntPtr _stateWorld;
    private static float _nextRepublish;

    /// <summary>
    /// Vanilla party channel switch. On (the default) one flare opens the point for the whole
    /// squad of the shooter, which is what <c>EZoneEventType.PlayerByPartyAddedInShotList</c>
    /// describes. Off binds the activation to the shooter alone, so every player has to fire
    /// their own flare.
    /// </summary>
    internal static bool SquadSharesActivation
    {
        get
        {
            try
            {
                var settings = FikaPlugin.Instance?.Settings;
                var entry = settings?.FlareExtractSquadSharesActivation;
                return entry == null || entry.Value;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Called by the shooter's own firearm controller right after it fired a flare. Detects
    /// ExitActivate (green) flares, resolves the detector zone and the point, and either sends
    /// the report (client) or grants locally (host).
    /// </summary>
    internal static void OnLocalFlareShot(Ammo flareItem, Vector3 shotPosition)
    {
        try
        {
            if (flareItem == null)
            {
                return;
            }

            var template = flareItem.AmmoTemplate;
            if (template == null || !template.ContainsType(FlareEventType.ExitActivate))
            {
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            var localPlayer = world?.MainPlayer;
            if (localPlayer == null)
            {
                return;
            }

            var profileId = localPlayer.ProfileId;
            if (string.IsNullOrEmpty(profileId))
            {
                Reject(string.Empty, "the local player has no profile id");
                return;
            }

            if (!TryResolveTarget(shotPosition, localPlayer.Position, out var zoneId, out var pointName, out var exitZone))
            {
                LogOnce("notarget:" + profileId,
                    $"[FlareExfil] fired zone= profile={profileId} -> no detector zone or exit point nearby, ignored");
                return;
            }

            if (FikaBackendUtils.IsClient)
            {
                if (FikaSingleton<IFikaNetworkManager>.Instance is not FikaClient client)
                {
                    Reject(profileId, "no FikaClient to report to");
                    return;
                }

                FlareExfilActivationPacket packet = new()
                {
                    Kind = FlareExfilActivationPacket.EKind.Report,
                    ProfileId = profileId,
                    PointName = pointName,
                    ZoneId = zoneId,
                    SquadId = string.Empty,
                    RaidId = localPlayer.RaidId,
                    FlarePosition = shotPosition
                };
                client.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                // The machine that fired the shot already ran the native scene chain when the flare burned;
                // this only covers the case where it did not (the flare burned before our postfix ran).
                QueueLocalVisual(flareItem, pointName, profileId, shotPosition);
                FikaGlobals.LogWarning($"[FlareExfil] fired zone={zoneId} profile={profileId} -> report sent");
                return;
            }

            QueueLocalVisual(flareItem, pointName, profileId, shotPosition);

            // Host: the local flare is authoritative right away, and every peer still has to be
            // told who it was for - the point must not open for players outside the grant.
            GrantForShooter(profileId, pointName, zoneId, exitZone, "host local flare", shotPosition);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] local flare handling failed: {exception}");
        }
    }

    /// <summary>
    /// Host side of a client report. The sending peer decides which profile the packet may
    /// speak for; every other field is validated against this raid before a grant is issued.
    /// </summary>
    internal static void HostHandleReport(FlareExfilActivationPacket packet, NetPeer peer)
    {
        try
        {
            if (packet.Kind != FlareExfilActivationPacket.EKind.Report)
            {
                return;
            }

            var sender = peer?.Player?.ProfileId;
            if (string.IsNullOrEmpty(sender) || !string.Equals(sender, packet.ProfileId, StringComparison.Ordinal))
            {
                Reject(packet.ProfileId, $"peer='{sender ?? "unknown"}' does not own the claimed profile");
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                Reject(packet.ProfileId, "no game world");
                return;
            }

            var player = world.GetAlivePlayerByProfileID(packet.ProfileId);
            if (player == null)
            {
                Reject(packet.ProfileId, "unknown player");
                return;
            }

            if (packet.RaidId <= 0)
            {
                Reject(packet.ProfileId, $"invalid flare raid id '{packet.RaidId}'");
                return;
            }

            if (IsPlayerBanned(player.RaidId))
            {
                Reject(packet.ProfileId, "the player is banned from exfiltration");
                return;
            }

            var point = FikaExfilManager.FindExfiltrationPoint(packet.PointName);
            if (point == null)
            {
                Reject(packet.ProfileId, $"unknown exit point '{packet.PointName}'");
                return;
            }

            // The scene-trigger reports carry no zone id: that map has no detector zone by construction, so the
            // host accepts the point when it is the exit the reported position lies at and that exit carries the
            // manual activation evidence a green flare satisfies.
            if (string.IsNullOrEmpty(packet.ZoneId))
            {
                var sceneDistance = float.MaxValue;
                try
                {
                    sceneDistance = Vector3.Distance(point.transform.position, packet.FlarePosition);
                }
                catch (Exception)
                {
                    // Reported as out of range below.
                }

                if (sceneDistance > PointRange)
                {
                    Reject(packet.ProfileId, $"the flare was fired farther than {PointRange} m from point '{packet.PointName}'");
                    return;
                }

                GrantForShooter(packet.ProfileId, packet.PointName, packet.ZoneId, true, "client report (scene trigger)", packet.FlarePosition);
                return;
            }

            // The host does not take the client's word for which zone it flared in: the reported
            // position has to be inside the detector zone the report names.
            if (!TryFindZone(packet.FlarePosition, out var reportedZone, out var reportedExitZone)
                || !string.Equals(ZoneName(reportedZone), packet.ZoneId, StringComparison.Ordinal))
            {
                Reject(packet.ProfileId, $"the flare position is not inside detector zone '{packet.ZoneId}'");
                return;
            }

            if (!reportedExitZone)
            {
                LogOnce("nonexitzone:" + packet.ZoneId,
                    $"[FlareExfil] report zone='{packet.ZoneId}' flareType={DescribeFlareType(reportedZone)} is not a green flare (ExitActivate) zone, so no exit can be opened by it");
            }

            // And it derives the exit from that zone itself, so a report can only name the exit the
            // zone activates, not any exit within reach.
            var derivedPointName = ResolvePointForZone(packet.ZoneId) ?? FindNearestPointName(packet.FlarePosition);
            if (string.IsNullOrEmpty(derivedPointName))
            {
                Reject(packet.ProfileId, $"no exit point within {PointRange} m of the reported flare position");
                return;
            }

            if (!string.Equals(derivedPointName, packet.PointName, StringComparison.Ordinal))
            {
                Reject(packet.ProfileId, $"the report names '{packet.PointName}' but its flare position belongs to '{derivedPointName}'");
                return;
            }

            // The exit point itself has to be the one that waits for a manual activation, or one a pass
            // already classified: otherwise a report built by hand could open an ordinary exit.
            if (HasManualActivationEvidence(packet.PointName, point) == null && !IsGatedPoint(packet.PointName))
            {
                Reject(packet.ProfileId, $"the exit '{packet.PointName}' does not wait for a manual activation, so a flare does not open it");
                return;
            }

            if (Vector3.Distance(point.transform.position, packet.FlarePosition) > PointRange)
            {
                Reject(packet.ProfileId, $"the flare was fired farther than {PointRange} m from point '{packet.PointName}'");
                return;
            }

            GrantForShooter(packet.ProfileId, packet.PointName, packet.ZoneId, reportedExitZone, "client report", packet.FlarePosition);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] report handling failed: {exception}");
        }
    }

    /// <summary>
    /// Client side of a host grant. The shooter and - with the squad switch on - its squad
    /// members apply it; any other squad records the point as gated and logs the ignored line.
    /// </summary>
    internal static void ClientHandlePacket(FlareExfilActivationPacket packet)
    {
        try
        {
            if (packet.Kind != FlareExfilActivationPacket.EKind.Grant)
            {
                return;
            }

            var localPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
            var ownProfileId = localPlayer?.ProfileId;
            if (string.IsNullOrEmpty(ownProfileId))
            {
                // Dropping the grant here is invisible: this machine writes no immunity, gates nothing
                // and prints no ignored line - while the host believes the grant arrived.
                LogFailure("client-no-profile", true,
                    $"[FlareExfil] grant dropped: the local profile id is unknown on this machine " +
                    $"(world={(Singleton<GameWorld>.Instance == null ? "missing" : "present")}, " +
                    $"mainPlayer={(localPlayer == null ? "missing" : "present")}) - zone='{packet.ZoneId}' " +
                    $"point='{packet.PointName}' firedBy='{packet.ProfileId}'",
                    string.Empty);
                return;
            }

            LogFailure("client-no-profile", false, string.Empty,
                $"[FlareExfil] grant handling resumed: the local profile id is '{ownProfileId}' again");

            // The host decides who the grant covers, but what it does is decided from this machine's
            // scene: only a green flare zone (or an exit this raid already classified as gated) gates
            // anything here, and a zone that feeds the native sniper immunity registry grants
            // immunity instead - on every machine, because every machine simulates its own sniper.
            var exitZone = IsExitZoneById(packet.ZoneId);
            var kind = KindOf(packet.ZoneId, exitZone, packet.PointName);
            if (kind == ZoneKind.Other && !exitZone && !IsGatedPoint(packet.PointName) && CountImmunityRegistries() > 0)
            {
                // This machine has no detector zone with that id (or it was never classified), the
                // grant gates nothing here and the raid does have the native registry: the host
                // resolved an immunity zone, so honour the immunity instead of dropping the grant.
                kind = ZoneKind.Immunity;
                LogOnce("immunity-inferred:" + packet.ZoneId,
                    $"[FlareExfil] immunity zone='{packet.ZoneId}' is not in this scene, treating the grant as immunity " +
                    $"(the raid carries {CountImmunityRegistries()} PlayersWithImmuneToSniperFire registr(ies))");
            }

            var mine = string.Equals(packet.ProfileId, ownProfileId, StringComparison.Ordinal);
            var squadWide = !string.IsNullOrEmpty(packet.SquadId)
                && string.Equals(LocalSquadId(localPlayer), packet.SquadId, StringComparison.Ordinal);

            if (kind == ZoneKind.Immunity)
            {
                // Every peer writes the covered raid ids, not only the covered players: a peer that
                // is not covered still runs a sniper zone that can fire at them.
                ApplyImmunity(packet.ZoneId, CoveredProfiles(packet, ownProfileId, mine || squadWide),
                    $"grant from host (fired by {packet.ProfileId})");
                // The exit animation is not part of the immunity path, but the flare that was fired here is a
                // scene event every machine has to play to see what happened - covered or not.
                QueueFlareVisual(packet);
                LogOnce($"visual-immunity:{packet.ZoneId}:{packet.ProfileId}",
                    $"[FlareExfil] heli: point='{packet.PointName}' open=n/a visual=broadcast kind=immunity " +
                    $"firedBy='{packet.ProfileId}' zone='{packet.ZoneId}' mine={mine} squadWide={squadWide}");
                LogOnce($"immunity-applied:{packet.ZoneId}:{ownProfileId}",
                    $"[FlareExfil] immunity applied on this machine zone='{packet.ZoneId}' (fired by {packet.ProfileId} " +
                    $"mine={mine} squadWide={squadWide})");
                return;
            }

            var gate = kind == ZoneKind.ExitGate;
            if (gate)
            {
                AddGatedPoint(packet.PointName);
            }

            // Before the covered/not-covered decision below: the exit animation of a flare exit belongs to
            // every machine in the raid, whether or not the grant covers this player, because the native
            // chain that plays it is started by the flare event and not by the exit opening.
            QueueFlareVisual(packet);

            if (!mine && !squadWide)
            {
                LogOnce($"ignored:{packet.PointName}:{packet.ProfileId}",
                    $"[FlareExfil] ignored (report profile={packet.ProfileId} != me) point={packet.PointName}");
                return;
            }

            AllowProfile(packet.PointName, ownProfileId, gate);

            var profiles = new List<string> { ownProfileId };
            ApplyGrant(packet.PointName, packet.ZoneId, profiles, out var transition, out var native);

            if (!gate)
            {
                LogOnce($"noop:{packet.PointName}:{ownProfileId}",
                    $"[FlareExfil] no-op point='{packet.PointName}' status={transition} for 1 players - the flare did not " +
                    $"resolve to a green flare exit, so nothing was opened (zone='{packet.ZoneId}' exitZone={exitZone})");
            }
            else if (!mine)
            {
                LogOnce($"applied:{packet.PointName}:{packet.ProfileId}:{ownProfileId}",
                    $"[FlareExfil] applied (fired by {packet.ProfileId}, squad-wide) point='{packet.PointName}' " +
                    $"status={transition} zone='{packet.ZoneId}' native=[{native}]");
            }
            else
            {
                LogOnce($"applied-self:{packet.PointName}:{ownProfileId}",
                    $"[FlareExfil] applied (fired by {packet.ProfileId}) point='{packet.PointName}' " +
                    $"status={transition} zone='{packet.ZoneId}' native=[{native}]");
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] grant handling failed: {exception}");
        }
    }

    /// <summary>
    /// Records the status each point has when the raid binds it. The flare exits are the points
    /// that (a) start out awaiting manual activation and (b) sit in a green flare detector zone;
    /// both facts come from the native raid setup, not from a name list.
    /// </summary>
    internal static void CaptureInitialStatus(ExfiltrationPoint[] regular, SecretExfiltrationPoint[] secret)
    {
        try
        {
            // Drop the previous raid's tables before this one is recorded. The reporting below runs
            // through LogOnce, whose world change branch clears the same tables, and a late clear
            // would wipe the classification this raid just produced.
            SyncRaidState();

            lock (Sync)
            {
                RememberStatus(regular);
                if (secret != null)
                {
                    for (var i = 0; i < secret.Length; i++)
                    {
                        var point = secret[i];
                        if (point?.Settings == null)
                        {
                            continue;
                        }

                        InitialStatuses.TryAdd(point.Settings.Name, point.Status);
                    }
                }
            }

            ClassifyDetectorPoints();
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] initial status capture failed: {exception}");
        }
    }

    private static void RememberStatus(ExfiltrationPoint[] regular)
    {
        if (regular == null)
        {
            return;
        }

        for (var i = 0; i < regular.Length; i++)
        {
            var point = regular[i];
            if (point?.Settings == null)
            {
                continue;
            }

            InitialStatuses.TryAdd(point.Settings.Name, point.Status);
        }
    }

    /// <summary>
    /// Marks the exits that need a green flare before any player fires one, so a raid where nobody
    /// fires cannot use them, and prints the evidence the verdict rests on: every detector zone with
    /// its flare type and the exit closest to it, and every exit with its status, its requirements
    /// and the zone that was associated with it.
    /// <para>
    /// Two things have to hold before an exit is gated: a green flare
    /// (<see cref="FlareEventType.ExitActivate"/>) detector zone must be associated with it, and it
    /// must carry manual activation evidence - either the status it had when the raid bound it, the
    /// live status, or an <see cref="AwaitsManualActivationRequirement"/> in its own requirement
    /// list. The status snapshot alone is not trusted, because the raid setup, the server side exit
    /// settings and a status the point already received can all leave it at something other than
    /// <see cref="EExfiltrationStatus.AwaitsManualActivation"/>.
    /// </para>
    /// <para>
    /// An exit that carries a requirement a green flare cannot satisfy (a paid exit, a cooperation
    /// exit, a world event exit) is never gated: gating one would lock an ordinary exit for every
    /// player that never fires a flare.
    /// </para>
    /// </summary>
    private static void ClassifyDetectorPoints()
    {
        FlareShootDetectorZone[] zones;
        try
        {
            zones = UnityEngine.Object.FindObjectsOfType<FlareShootDetectorZone>(true);
        }
        catch (Exception exception)
        {
            LogOnce("classify", $"[FlareExfil] detector zone lookup failed: {exception.Message}");
            return;
        }

        // A destroyed entry would otherwise vanish without a trace: the zone table would simply be
        // one line shorter than the scene has zones.
        var destroyedZones = CountDestroyed(zones);
        if (destroyedZones > 0)
        {
            LogOnce("zones-destroyed",
                $"[FlareExfil] detector zones: {destroyedZones} of {zones.Length} entry(ies) are destroyed and were skipped");
        }

        if (zones == null || zones.Length == 0)
        {
            LogOnce("zones", "[FlareExfil] detector zones: none in this raid");
            return;
        }

        var controller = ExfiltrationController.Instance;
        if (controller == null)
        {
            // Without the controller nothing can be classified: no gated table, no immunity table and
            // no "flare gated points" line. Reporting it is what keeps an empty classification from
            // reading as "this map has no flare exit".
            LogFailure("classify-nocontroller", true,
                "[FlareExfil] classification skipped: ExfiltrationController is not available on this machine, " +
                "so no exit is gated and no immunity zone is named (a fired flare is still granted from the scene alone)",
                string.Empty);
            return;
        }

        LogFailure("classify-nocontroller", false, string.Empty,
            "[FlareExfil] classification resumed: ExfiltrationController is available again");

        // Secret exits are deliberately not classified: theirs is the group/team shared discovery
        // path (SecretExfiltrationPointFoundShareEvent), and a misclassification would skip their
        // status sync for the whole squad.
        var points = controller.ExfiltrationPoints;

        // Same for exits: a point without settings is neither gated nor listed below, and a table
        // that came out empty must say whether that is the map or a torn down scene.
        var skippedPoints = DescribeSkippedPoints(points);
        if (skippedPoints != null)
        {
            LogOnce("points-skipped",
                $"[FlareExfil] classification skipped exit point(s) that are destroyed or not initialised - {skippedPoints} - " +
                "they are neither gated nor listed in the point table");
        }

        // The zone pass resolves what each flare opens, the exit pass decides which exits are gated,
        // and only then can a zone be named: an immunity verdict needs the exit verdict.
        var infos = CollectDetectorZones(zones, points);
        ClassifyPoints(points);
        ReportZoneKinds(infos);
        LogClassification();
    }

    /// <summary>
    /// Collects every detector zone of this raid, resolves the exit a green flare zone activates and
    /// returns the snapshot the reporting pass prints. Nothing is logged here: what a zone is needs
    /// the exit verdict, which <see cref="ClassifyPoints"/> produces afterwards.
    /// </summary>
    private static List<ZoneInfo> CollectDetectorZones(FlareShootDetectorZone[] zones, ExfiltrationPoint[] points)
    {
        var infos = new List<ZoneInfo>();
        for (var i = 0; i < zones.Length; i++)
        {
            var zone = zones[i];
            if (zone == null)
            {
                continue;
            }

            var zoneId = ZoneName(zone);
            var exitZone = IsExitZone(zone);
            var nearestName = null as string;
            var nearestDistance = -1f;
            try
            {
                nearestName = NearestPointTo(points, zone.transform.position, out nearestDistance);
            }
            catch (Exception)
            {
                // Reported with the placeholders of the zone line.
            }

            var inside = PointInsideZone(zone, points, out var insideName);

            // The zone is bound to an exit only when it physically contains one, or when the closest
            // exit is inside the accepted range. Binding a far away exit would attach the gate to a
            // point the flare has nothing to do with. Only a green flare (ExitActivate) zone opens an
            // exit, so only those are bound.
            var associated = inside
                ? insideName
                : (nearestName != null && nearestDistance >= 0f && nearestDistance <= PointRange ? nearestName : null);

            if (exitZone && !string.IsNullOrEmpty(zoneId) && !string.IsNullOrEmpty(associated))
            {
                lock (Sync)
                {
                    PointByFlareZone[zoneId] = associated;
                    FlareZoneByPoint[associated] = zoneId;
                    FlareZoneDistanceByPoint[associated] = inside ? 0f : nearestDistance;
                }
            }

            infos.Add(new ZoneInfo
            {
                Zone = zone,
                ZoneId = zoneId,
                ExitZone = exitZone,
                FlareType = DescribeFlareType(zone),
                Collider = DescribeZoneCollider(zone),
                NearestName = nearestName,
                NearestDistance = nearestDistance,
                InsideName = insideName,
                AssociatedName = associated
            });
        }

        return infos;
    }

    /// <summary>
    /// One line per detector zone: the flare type it handles, the exit closest to it, the exit inside
    /// its collider and the class of work its flare does.
    /// <para>
    /// <c>kind=exit-gate</c> is a green (<c>ExitActivate</c>) zone, so the flare activates the exit it
    /// is associated with; whether that exit is actually gated is the verdict of the exit line below.
    /// <c>kind=immunity</c> is a zone that feeds the native sniper immunity registry instead; the
    /// evidence for that verdict is printed after <c>immune=</c>. <c>kind=other</c> is an airdrop,
    /// quest or sniper fire zone that no exfiltration logic may touch.
    /// </para>
    /// </summary>
    private static void ReportZoneKinds(List<ZoneInfo> infos)
    {
        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            var exitGated = !string.IsNullOrEmpty(info.AssociatedName) && IsGatedPoint(info.AssociatedName);
            var kind = ClassifyKind(info, exitGated, out var evidence);

            if (kind == ZoneKind.Immunity && exitGated)
            {
                // The one boundary where the two classes overlap: a green flare zone that both feeds
                // the immunity registry and sits over an exit that waits for manual activation.
                // Immunity wins (an immunity zone must never touch an exit status), so the gate it
                // would have opened is lost - flag it instead of deciding silently.
                LogFailure("kind-conflict:" + info.ZoneId, true,
                    $"[FlareExfil] zone '{info.ZoneId}' matches BOTH immunity and exit-gate (associatedExit='{info.AssociatedName}' " +
                    $"exitGated=True immune=[{evidence}]) - treated as immunity, so that exit is not gated: confirm this zone by hand",
                    $"[FlareExfil] zone '{info.ZoneId}' no longer matches both immunity and exit-gate (exitGated={exitGated}, " +
                    $"kind={DescribeKind(kind)})");
            }
            else
            {
                LogFailure("kind-conflict:" + info.ZoneId, false, string.Empty,
                    $"[FlareExfil] zone '{info.ZoneId}' no longer matches both immunity and exit-gate " +
                    $"(exitGated={exitGated}, kind={DescribeKind(kind)}) - the verdict is unambiguous again");
            }

            if (!string.IsNullOrEmpty(info.ZoneId))
            {
                lock (Sync)
                {
                    ZoneKinds[info.ZoneId] = kind;
                }
            }

            LogOnce("zone:" + info.ZoneId,
                $"[FlareExfil] detector zone '{info.ZoneId}' flareType={info.FlareType} exitZone={info.ExitZone} " +
                $"collider={info.Collider} nearestExit='{info.NearestName ?? "none"}' " +
                $"distance={(info.NearestDistance < 0f ? "n/a" : info.NearestDistance.ToString("F1") + "m")} " +
                $"exitInsideZone='{info.InsideName ?? "none"}' associatedExit='{info.AssociatedName ?? "none"}' " +
                $"exitGated={exitGated} kind={DescribeKind(kind)} immune=[{evidence ?? "none"}]");
        }
    }

    /// <summary>
    /// The work a zone's flare does on this machine. Immunity wins over the gate: a zone that feeds
    /// the sniper immunity registry is never allowed to touch an exit status, because the exit of
    /// such a map (Ground Zero's <c>Sniper_exit</c>) is open by requirement and only the sniper
    /// blocks it.
    /// </summary>
    private static ZoneKind ClassifyKind(ZoneInfo info, bool exitGated, out string evidence)
    {
        evidence = info.Zone != null ? ImmunityEvidence(info.Zone, exitGated) : null;
        if (evidence != null)
        {
            return ZoneKind.Immunity;
        }

        if (info.ExitZone)
        {
            evidence = "green flare (ExitActivate) zone";
            return ZoneKind.ExitGate;
        }

        return ZoneKind.Other;
    }

    private static string DescribeKind(ZoneKind kind)
    {
        return kind switch
        {
            ZoneKind.ExitGate => "exit-gate",
            ZoneKind.Immunity => "immunity",
            _ => "other"
        };
    }

    /// <summary>
    /// One line per exit with the verdict of the gate, so a raid can be read back without a debugger:
    /// status, recorded initial status, exit type, requirements, associated zone, distance.
    /// </summary>
    private static void ClassifyPoints(ExfiltrationPoint[] points)
    {
        if (points == null)
        {
            return;
        }

        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point?.Settings == null || string.IsNullOrEmpty(point.Settings.Name))
            {
                continue;
            }

            var name = point.Settings.Name;
            var status = "unreadable";
            try
            {
                status = DescribeStatus(point.Status);
            }
            catch (Exception)
            {
                // Kept as unreadable.
            }

            var initial = "unrecorded";
            lock (Sync)
            {
                if (InitialStatuses.TryGetValue(name, out var recorded))
                {
                    initial = DescribeStatus(recorded);
                }
            }

            var zoneId = "none";
            var zoneDistance = "n/a";
            lock (Sync)
            {
                if (FlareZoneByPoint.TryGetValue(name, out var associated))
                {
                    zoneId = associated;
                }

                if (FlareZoneDistanceByPoint.TryGetValue(name, out var distance))
                {
                    zoneDistance = distance <= 0f ? "inside zone" : distance.ToString("F1") + "m";
                }
            }

            var evidence = HasManualActivationEvidence(name, point);
            var blocker = DescribeBlockingRequirement(point);

            var gated = false;
            string reason;
            if (zoneId == "none")
            {
                reason = "no green flare (ExitActivate) detector zone is associated with this exit";
            }
            else if (blocker != null)
            {
                reason = blocker;
            }
            else if (evidence == null)
            {
                reason = "no manual activation evidence: the status is neither recorded nor read as AwaitsManualActivation and the exit carries no AwaitsManualActivationRequirement";
            }
            else
            {
                gated = true;
                reason = evidence;
            }

            if (gated)
            {
                lock (Sync)
                {
                    ClassifiedGated.Add(name);
                }
            }

            LogOnce("point:" + name,
                $"[FlareExfil] point '{name}' status={status} initial={initial} type={DescribePointType(point)} " +
                $"requirements=[{DescribeRequirements(point)}] zone='{zoneId}' distance={zoneDistance} " +
                $"verdict={(gated ? "gated" : "not-gated")} reason='{reason}'");
        }
    }

    /// <summary>True for a detector zone that activates an exit: only a green flare does that.</summary>
    private static bool IsExitZone(FlareShootDetectorZone zone)
    {
        try
        {
            return zone != null && zone.flareTypeForHandle == FlareEventType.ExitActivate;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string DescribeFlareType(FlareShootDetectorZone zone)
    {
        try
        {
            return DescribeFlareType(zone.flareTypeForHandle);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string DescribeFlareType(FlareEventType flareType)
    {
        return $"{flareType}({(int)flareType})";
    }

    private static string DescribeZoneCollider(FlareShootDetectorZone zone)
    {
        try
        {
            var collider = GetZoneCollider(zone);
            if (collider == null)
            {
                return "none";
            }

            var size = collider.bounds.size;
            return $"bounds=({size.x:F1},{size.y:F1},{size.z:F1})";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string DescribeStatus(EExfiltrationStatus status)
    {
        return $"{status}({(int)status})";
    }

    private static string DescribePointType(ExfiltrationPoint point)
    {
        try
        {
            return point.Settings != null ? point.Settings.ExfiltrationType.ToString() : "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// The native type name of a requirement. The runtime type is read from the IL2CPP type, because
    /// the declared element type of the requirement array is always the base class.
    /// </summary>
    private static string RequirementTypeName(ExfiltrationRequirement requirement)
    {
        try
        {
            var type = requirement.GetIl2CppType();
            if (type != null && !string.IsNullOrEmpty(type.Name))
            {
                return type.Name;
            }
        }
        catch (Exception)
        {
            // Falls back to the managed proxy type below.
        }

        try
        {
            return requirement.GetType().Name;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string DescribeRequirements(ExfiltrationPoint point)
    {
        try
        {
            var requirements = point.Requirements;
            if (requirements == null || requirements.Length == 0)
            {
                return "none";
            }

            var text = string.Empty;
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                if (requirement == null)
                {
                    continue;
                }

                if (text.Length > 0)
                {
                    text += ", ";
                }

                text += RequirementTypeName(requirement);
                try
                {
                    text += "(" + requirement.Requirement + ")";
                }
                catch (Exception)
                {
                    text += "(unreadable)";
                }
            }

            return text.Length == 0 ? "none" : text;
        }
        catch (Exception exception)
        {
            return "unreadable: " + exception.Message;
        }
    }

    /// <summary>
    /// Names the first requirement a green flare cannot satisfy, or null when the exit only carries
    /// the manual activation requirement and empty requirement slots.
    /// </summary>
    private static string DescribeBlockingRequirement(ExfiltrationPoint point)
    {
        try
        {
            if (point.TransferItemRequirement != null)
            {
                return "the exit requires an item transfer, so it is a paid exit";
            }

            var requirements = point.Requirements;
            if (requirements == null)
            {
                return null;
            }

            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                if (requirement == null)
                {
                    continue;
                }

                var typeName = RequirementTypeName(requirement);
                if (typeName.IndexOf("AwaitsManualActivation", StringComparison.Ordinal) >= 0)
                {
                    continue;
                }

                ERequirementState state;
                try
                {
                    state = requirement.Requirement;
                }
                catch (Exception)
                {
                    continue;
                }

                if (state == ERequirementState.None)
                {
                    continue;
                }

                return $"the exit carries the requirement {typeName}({state}), which a flare does not satisfy";
            }
        }
        catch (Exception exception)
        {
            return "the requirements of the exit are unreadable: " + exception.Message;
        }

        return null;
    }

    /// <summary>
    /// Manual activation evidence for an exit: the status recorded when the raid bound it, the live
    /// status, or the exit's own <c>AwaitsManualActivationRequirement</c>. Returns the evidence text
    /// or null when none of them applies.
    /// </summary>
    private static string HasManualActivationEvidence(string name, ExfiltrationPoint point)
    {
        lock (Sync)
        {
            if (InitialStatuses.TryGetValue(name, out var recorded) && recorded == EExfiltrationStatus.AwaitsManualActivation)
            {
                return "recorded status AwaitsManualActivation";
            }
        }

        try
        {
            if (point.Status == EExfiltrationStatus.AwaitsManualActivation)
            {
                return "live status AwaitsManualActivation";
            }
        }
        catch (Exception)
        {
            // Falls through to the requirement check.
        }

        try
        {
            var requirements = point.Requirements;
            if (requirements != null)
            {
                for (var i = 0; i < requirements.Length; i++)
                {
                    var requirement = requirements[i];
                    if (requirement != null
                        && RequirementTypeName(requirement).IndexOf("AwaitsManualActivation", StringComparison.Ordinal) >= 0)
                    {
                        return "AwaitsManualActivationRequirement";
                    }
                }
            }
        }
        catch (Exception)
        {
            // No requirement evidence.
        }

        return null;
    }

    private static string NearestPointTo(ExfiltrationPoint[] points, Vector3 position, out float distance)
    {
        distance = -1f;
        var bestName = null as string;
        var best = float.MaxValue;

        if (points == null)
        {
            return null;
        }

        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point?.Settings == null || string.IsNullOrEmpty(point.Settings.Name))
            {
                continue;
            }

            float current;
            try
            {
                current = Vector3.Distance(point.transform.position, position);
            }
            catch (Exception)
            {
                continue;
            }

            if (current < best)
            {
                best = current;
                bestName = point.Settings.Name;
            }
        }

        if (bestName != null)
        {
            distance = best;
        }

        return bestName;
    }

    /// <summary>The exit whose trigger sits inside the zone, if any.</summary>
    private static bool PointInsideZone(FlareShootDetectorZone zone, ExfiltrationPoint[] points, out string name)
    {
        name = null;

        try
        {
            var collider = GetZoneCollider(zone);
            if (collider == null || points == null)
            {
                return false;
            }

            var bounds = collider.bounds;
            var best = float.MaxValue;
            for (var i = 0; i < points.Length; i++)
            {
                var point = points[i];
                if (point?.Settings == null || string.IsNullOrEmpty(point.Settings.Name))
                {
                    continue;
                }

                var position = point.transform.position;
                if (!bounds.Contains(position))
                {
                    continue;
                }

                var distance = Vector3.Distance(position, zone.transform.position);
                if (distance < best)
                {
                    best = distance;
                    name = point.Settings.Name;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return !string.IsNullOrEmpty(name);
    }

    /// <summary>True when the named zone exists locally and handles exit activation.</summary>
    private static bool IsExitZoneById(string zoneId)
    {
        var zone = FindZoneById(zoneId);
        return zone != null && IsExitZone(zone);
    }

    /// <summary>The detector zone with this id on this machine, or null when this scene has none.</summary>
    private static FlareShootDetectorZone FindZoneById(string zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
        {
            return null;
        }

        try
        {
            var zones = UnityEngine.Object.FindObjectsOfType<FlareShootDetectorZone>(true);
            if (zones == null)
            {
                return null;
            }

            for (var i = 0; i < zones.Length; i++)
            {
                var zone = zones[i];
                if (zone != null && string.Equals(ZoneName(zone), zoneId, StringComparison.Ordinal))
                {
                    return zone;
                }
            }
        }
        catch (Exception exception)
        {
            LogOnce("zonecheck", $"[FlareExfil] detector zone lookup failed: {exception.Message}");
        }

        return null;
    }

    private static void LogClassification()
    {
        if (_classificationLogged)
        {
            return;
        }

        _classificationLogged = true;
        var names = new List<string>();
        lock (Sync)
        {
            foreach (var name in ClassifiedGated)
            {
                names.Add(name);
            }
        }

        // The scene-trigger chain is the second, detector-zone-free flavour of a flare exit (the icebreaker
        // helicopter exit): a map that has one but no detector zone cannot be classified by geometry, so the
        // count is what tells the two flavours apart in a single log line.
        var triggers = CountSceneFlareTriggers();

        if (names.Count == 0)
        {
            FikaGlobals.LogWarning($"[FlareExfil] flare gated points: none in this raid (no detector zone over an " +
                $"awaiting-manual-activation exit) sceneTriggers={triggers}");
            return;
        }

        FikaGlobals.LogWarning($"[FlareExfil] flare gated points: {string.Join(", ", names)} sceneTriggers={triggers}");
    }

    /// <summary>
    /// True when this player may use the point. Points that never saw a flare activation keep
    /// their vanilla behaviour, so nothing but the flare exits is affected.
    /// </summary>
    public static bool MayExtract(string profileId, string pointName)
    {
        try
        {
            if (string.IsNullOrEmpty(pointName))
            {
                return true;
            }

            lock (Sync)
            {
                if (!GatedPoints.Contains(pointName) && !ClassifiedGated.Contains(pointName))
                {
                    return true;
                }

                if (AllowedProfilesByPoint.TryGetValue(pointName, out var allowed) && allowed != null
                    && !string.IsNullOrEmpty(profileId) && allowed.Contains(profileId))
                {
                    return true;
                }
            }

            LogOnce($"refused:{pointName}:{profileId}",
                $"[FlareExfil] rejected profile={profileId} reason=no flare activation for point '{pointName}'");
            return false;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] extraction check failed: {exception}");
            return true;
        }
    }

    /// <summary>True once any player activated this point with a flare in this raid.</summary>
    public static bool IsGatedPoint(string pointName)
    {
        if (string.IsNullOrEmpty(pointName))
        {
            return false;
        }

        lock (Sync)
        {
            return GatedPoints.Contains(pointName) || ClassifiedGated.Contains(pointName);
        }
    }

    /// <summary>True when the local player is on the allow list of the point.</summary>
    public static bool LocalPlayerAllowed(string pointName)
    {
        var profileId = Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
        if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(pointName))
        {
            return false;
        }

        lock (Sync)
        {
            return AllowedProfilesByPoint.TryGetValue(pointName, out var allowed) && allowed != null && allowed.Contains(profileId);
        }
    }

    /// <summary>
    /// True when a received point status must not be written locally: the point is flare gated
    /// and this player is not on the allow list, so the point-level state of the sender says
    /// nothing about this player.
    /// </summary>
    public static bool ShouldSkipPointStatus(string pointName)
    {
        return IsGatedPoint(pointName) && !LocalPlayerAllowed(pointName);
    }

    /// <summary>
    /// Host: hands the current grants to the peers again. Runs on a timer so a late joining
    /// squad member sees an activation that happened before it connected.
    /// </summary>
    internal static void HostTick()
    {
        if (FikaBackendUtils.IsClient)
        {
            return;
        }

        try
        {
            if (Time.time < _nextRepublish)
            {
                return;
            }

            _nextRepublish = Time.time + RepublishInterval;

            GrantRecord[] snapshot;
            lock (Sync)
            {
                if (Grants.Count == 0)
                {
                    return;
                }

                snapshot = [.. Grants];
            }

            foreach (var grant in snapshot)
            {
                BroadcastGrant(grant);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] grant republication failed: {exception}");
        }
    }

    /// <summary>
    /// Logged when a gated point refuses to replicate its point level countdown: the activation
    /// travels as a per-player grant, never as an identity-less broadcast.
    /// </summary>
    public static void LogCountdownSuppressed(string pointName)
    {
        LogOnce("countdown-suppressed:" + pointName,
            $"[FlareExfil] point {pointName} countdown not broadcast (flare gated, activation travels per player)");
    }

    /// <summary>Drops the raid state. Called when a new world is seen and safe to call at any time.</summary>
    /// <summary>
    /// Kept as the explicit entry point for callers that want to drop the raid tables (the world
    /// change branch of <see cref="LogOnce"/> clears the same tables on its own, so nothing depends
    /// on this being called; it exists so a future hook does not have to reach into the tables).
    /// </summary>
    internal static void ResetRaidState()
    {
        lock (Sync)
        {
            ClearRaidStateLocked();
        }
    }

    /// <summary>
    /// Clears every per-raid table. Called on a world change (a new raid, a transit that reloads
    /// the level) and by <see cref="ResetRaidState"/>; the lock is reentrant, so callers may hold
    /// it already.
    /// </summary>
    private static void ClearRaidStateLocked()
    {
        AllowedProfilesByPoint.Clear();
        GatedPoints.Clear();
        Grants.Clear();
        LoggedLines.Clear();
        InitialStatuses.Clear();
        ClassifiedGated.Clear();
        FlareZoneByPoint.Clear();
        PointByFlareZone.Clear();
        FlareZoneDistanceByPoint.Clear();
        ZoneKinds.Clear();
        Failing.Clear();
        FeedbackShown.Clear();
        FeedbackPopups.Clear();
        PendingVisuals.Clear();
        AppliedVisuals.Clear();
        LastFlareByProfile.Clear();
        _classificationLogged = false;
    }

    /// <summary>
    /// Drops the raid tables when the world pointer changed since the last call. Every entry point
    /// runs it before reading state, so a late world change cannot wipe a table that was just filled
    /// and a new raid can never read the previous raid's activation.
    /// </summary>
    private static void SyncRaidState()
    {
        lock (Sync)
        {
            var world = Singleton<GameWorld>.Instance;
            var worldPointer = world == null ? System.IntPtr.Zero : world.Pointer;
            if (worldPointer != _stateWorld)
            {
                // New world: a fresh raid, or a transit that reloaded the level.
                _stateWorld = worldPointer;
                ClearRaidStateLocked();
            }
        }
    }

    /// <summary>
    /// Issues the activation the shooter's flare earned: the covered profiles are written to the allow list,
    /// the point is opened, and the grant travels to every peer. <paramref name="flarePosition"/> is the spot
    /// the flare burned at and travels with the grant so each peer can run the same exit chain the shooter's
    /// machine ran; <see cref="Vector3.zero"/> means no visual is carried.
    /// </summary>
    private static void GrantForShooter(string profileId, string pointName, string zoneId, bool exitZone, string source, Vector3 flarePosition)
    {
        // What the flare does here decides everything below. A grant only gates an exit that is a
        // green flare exit: the flare zone has to be an exit activation zone, or the exit has to be
        // one this raid already classified as gated. A flare that resolved to an ordinary exit must
        // not lock that exit for everyone outside the grant, and a flare fired in an immunity zone
        // (the Ground Zero sniper area) must not touch the exit at all: there it only disables the
        // sniper, for exactly the players the grant covers.
        var kind = KindOf(zoneId, exitZone, pointName);
        var gate = kind == ZoneKind.ExitGate;

        var squadId = string.Empty;
        var extraProfiles = null as List<string>;
        if (SquadSharesActivation)
        {
            squadId = FindSquadId(profileId);
            if (!string.IsNullOrEmpty(squadId))
            {
                extraProfiles = CollectGroupProfiles(squadId, profileId);
            }
        }

        AllowProfile(pointName, profileId, gate);

        var granted = new List<string> { profileId };
        var extra = 0;
        if (extraProfiles != null)
        {
            foreach (var groupProfile in extraProfiles)
            {
                if (string.IsNullOrEmpty(groupProfile) || string.Equals(groupProfile, profileId, StringComparison.Ordinal))
                {
                    continue;
                }

                AllowProfile(pointName, groupProfile, gate);
                granted.Add(groupProfile);
                extra++;
            }
        }

        string transition;
        string native;
        if (kind == ZoneKind.Immunity)
        {
            // The exit of an immunity zone keeps the status the server gave it; the flare writes the
            // native sniper immunity set for the covered players on this machine instead.
            ApplyImmunity(zoneId, granted, source);
            transition = "point-status-untouched (immunity zone)";
            native = "immunity";
        }
        else
        {
            ApplyGrant(pointName, zoneId, granted, out transition, out native);
        }

        var record = new GrantRecord(pointName, profileId, squadId, zoneId, flarePosition);
        if (gate || kind == ZoneKind.Immunity)
        {
            lock (Sync)
            {
                if (gate)
                {
                    GatedPoints.Add(pointName);
                }

                // Immunity grants are republished as well: a peer that connects later still has to
                // write the raid ids into its own registry.
                Grants.RemoveAll(grant => string.Equals(grant.PointName, pointName, StringComparison.Ordinal)
                    && string.Equals(grant.ProfileId, profileId, StringComparison.Ordinal));
                Grants.Add(record);
            }
        }

        BroadcastGrant(record);

        var members = extra + 1;
        if (kind == ZoneKind.Immunity)
        {
            LogOnce("applied:" + pointName,
                $"[FlareExfil] applied zone='{zoneId}' kind=immunity for {members} players - the flare disables the " +
                $"sniper for them and no exit is gated (point='{pointName}' exitZone={exitZone} status={transition} " +
                $"native=[{native}] source={source})");
        }
        else if (gate)
        {
            LogOnce("applied:" + pointName,
                $"[FlareExfil] applied point='{pointName}' status={transition} for {members} players " +
                $"(zone='{zoneId}' kind=exit-gate exitZone={exitZone} native=[{native}] source={source})");
        }
        else
        {
            LogOnce("noop:" + pointName,
                $"[FlareExfil] no-op point='{pointName}' status={transition} for {members} players - the flare did not " +
                $"resolve to a green flare exit, so nothing was opened and nothing was gated (zone='{zoneId}' exitZone={exitZone} source={source})");
        }

        if (extra > 0)
        {
            LogOnce("grant:" + pointName,
                $"[FlareExfil] report profile={profileId} zone={zoneId} -> granted to party ({members} members) ({source})");
            return;
        }

        LogOnce("grant:" + pointName,
            $"[FlareExfil] report profile={profileId} zone={zoneId} -> granted for {profileId} only ({source})");
    }

    private static void BroadcastGrant(GrantRecord grant)
    {
        if (FikaBackendUtils.IsClient)
        {
            // Only the host hands out grants; reaching this on a client means the grant is never
            // announced to anybody.
            LogFailure("broadcast:client", true,
                $"[FlareExfil] grant not broadcast: this machine is a client, so no peer learns about " +
                $"zone='{grant.ZoneId}' point='{grant.PointName}' firedBy='{grant.ProfileId}'",
                string.Empty);
            return;
        }

        LogFailure("broadcast:client", false, string.Empty,
            "[FlareExfil] grant broadcasting resumed: this machine has the host role again");

        if (FikaSingleton<IFikaNetworkManager>.Instance is not FikaServer server)
        {
            // The classic "the host thinks it sent it": the grant was decided locally and every peer
            // stays without the immunity or the gate it carries.
            LogFailure("broadcast:no-server", true,
                $"[FlareExfil] grant not broadcast: the local manager is not a FikaServer " +
                $"(manager='{FikaSingleton<IFikaNetworkManager>.Instance?.GetType().Name ?? "none"}'), " +
                $"so no peer learns about zone='{grant.ZoneId}' point='{grant.PointName}' firedBy='{grant.ProfileId}'",
                string.Empty);
            return;
        }

        LogFailure("broadcast:no-server", false, string.Empty,
            "[FlareExfil] grant broadcasting resumed: the local manager is a FikaServer");

        FlareExfilActivationPacket packet = new()
        {
            Kind = FlareExfilActivationPacket.EKind.Grant,
            ProfileId = grant.ProfileId,
            PointName = grant.PointName,
            ZoneId = grant.ZoneId,
            SquadId = grant.SquadId,
            FlarePosition = default,
            // The spot the flare burned at. It is what the peers need to run the same exit chain the
            // shooter's machine ran (the icebreaker helicopter animation); Vector3.zero means the grant
            // carries no visual, which is the case only for a grant built without a flare position.
            FlareDirection = grant.FlarePosition
        };

        // One packet per grant, sent to every peer: they decide from ProfileId/SquadId whether it
        // covers them, and the ones it does not cover still learn that the point is gated.
        server.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private static void AllowProfile(string pointName, string profileId, bool gate)
    {
        if (string.IsNullOrEmpty(pointName) || string.IsNullOrEmpty(profileId))
        {
            return;
        }

        lock (Sync)
        {
            if (gate)
            {
                GatedPoints.Add(pointName);
            }

            if (!AllowedProfilesByPoint.TryGetValue(pointName, out var allowed) || allowed == null)
            {
                allowed = new HashSet<string>(StringComparer.Ordinal);
                AllowedProfilesByPoint[pointName] = allowed;
            }

            allowed.Add(profileId);
        }
    }

    private static void AddGatedPoint(string pointName)
    {
        if (string.IsNullOrEmpty(pointName))
        {
            return;
        }

        lock (Sync)
        {
            GatedPoints.Add(pointName);
        }
    }

    /// <summary>
    /// Mirrors what the native side does on the firing machine for every profile the grant covers:
    /// the exit leaves the manual activation state and the native per player records are written.
    /// The exit object exists on every machine, and this only runs on the machines the grant covers.
    /// <paramref name="transition"/> and <paramref name="native"/> carry the observable outcome for
    /// the acceptance line of the caller.
    /// </summary>
    private static void ApplyGrant(string pointName, string zoneId, List<string> profiles, out string transition, out string native)
    {
        transition = "point-not-found";
        native = "none";

        var point = FikaExfilManager.FindExfiltrationPoint(pointName);
        if (point == null)
        {
            return;
        }

        EExfiltrationStatus old;
        try
        {
            old = point.Status;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] reading the status of point '{pointName}' failed: {exception}");
            transition = "status-unreadable";
            return;
        }

        if (old == EExfiltrationStatus.AwaitsManualActivation)
        {
            try
            {
                // Native activation path: the external setter raises OnStatusChanged, so the exit UI
                // and every native subscriber follow the flip.
                point.ExternalSetStatus(EExfiltrationStatus.RegularMode, NativeSignature);
            }
            catch (Exception exception)
            {
                FikaGlobals.LogWarning($"[FlareExfil] external activation of point '{pointName}' failed ({exception.Message}), writing the status directly");
                try
                {
                    point.Status = EExfiltrationStatus.RegularMode;
                }
                catch (Exception inner)
                {
                    FikaGlobals.LogError($"[FlareExfil] activating point '{pointName}' failed: {inner}");
                    transition = $"{DescribeStatus(old)} (activation failed)";
                    return;
                }
            }
        }

        var current = old;
        try
        {
            current = point.Status;
        }
        catch (Exception)
        {
            // Reported as the old status.
        }

        transition = $"{DescribeStatus(old)}->{DescribeStatus(current)}";

        var shots = 0;
        var met = 0;
        var count = profiles?.Count ?? 0;
        if (profiles != null)
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                if (RecordNativeShotProfile(zoneId, profiles[i]))
                {
                    shots++;
                }

                if (RecordMetRequirement(point, profiles[i]))
                {
                    met++;
                }
            }
        }

        native = $"shotList+{shots}/{count},metRequirements+{met}/{count}";
    }

    /// <summary>
    /// Writes the profile into the native per profile list the exit evaluates
    /// (<c>ExfiltrationPoint._playersMetAllRequirements</c>), which is the player level record a
    /// manual activation requirement reads. A runtime that does not expose the list keeps working:
    /// the allow list of this class still decides who may extract.
    /// </summary>
    private static bool RecordMetRequirement(ExfiltrationPoint point, string profileId)
    {
        if (point == null || string.IsNullOrEmpty(profileId))
        {
            return false;
        }

        try
        {
            var met = point._playersMetAllRequirements;
            if (met == null || met.Contains(profileId))
            {
                return false;
            }

            met.Add(profileId);
            return true;
        }
        catch (Exception exception)
        {
            LogOnce("metwrite:" + profileId,
                $"[FlareExfil] native met requirement list could not be written for '{profileId}': {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Best effort copy of the native per-profile record
    /// (<c>FlareShootDetectorZone._shotPlayerProfiles</c>), which the per-player requirement of
    /// the point is built on. A runtime that does not expose the field keeps working: the allow
    /// list of this class still decides who may extract.
    /// </summary>
    private static bool RecordNativeShotProfile(string zoneId, string profileId)
    {
        if (string.IsNullOrEmpty(zoneId) || string.IsNullOrEmpty(profileId))
        {
            return false;
        }

        try
        {
            var zones = UnityEngine.Object.FindObjectsOfType<FlareShootDetectorZone>(true);
            if (zones == null)
            {
                return false;
            }

            var written = false;
            foreach (var zone in zones)
            {
                if (zone == null || !string.Equals(ZoneName(zone), zoneId, StringComparison.Ordinal))
                {
                    continue;
                }

                var shotProfiles = zone._shotPlayerProfiles;
                if (shotProfiles != null && !shotProfiles.Contains(profileId))
                {
                    shotProfiles.Add(profileId);
                    written = true;
                }
            }

            return written;
        }
        catch (Exception exception)
        {
            LogOnce("nativewrite:" + zoneId,
                $"[FlareExfil] native shot list of zone '{zoneId}' could not be written: {exception.Message}");
            return false;
        }
    }

    private static bool TryResolveTarget(Vector3 shotPosition, Vector3 playerPosition, out string zoneId, out string pointName, out bool exitZone)
    {
        zoneId = null;
        pointName = null;
        exitZone = false;

        if (!TryFindZone(shotPosition, out var zone, out exitZone))
        {
            // Maps whose flare exit is driven by a scene trigger instead of a detector zone have no
            // FlareShootDetectorZone to resolve against at all. The flare's own template already proved the
            // shooter fired a green (ExitActivate) flare; the exit is the awaiting-manual-activation point
            // the flare burned next to, and the activation is still only issued when that point carries
            // the manual activation evidence or a pass already classified it. Without this branch a flare
            // on such a map activates nothing at all on this machine - it cannot even be reported.
            if (!TryResolveSceneTriggerPoint(shotPosition, out pointName, out var evidence))
            {
                return false;
            }

            zoneId = string.Empty;
            exitZone = true;
            LogOnce("scene-trigger:" + pointName,
                $"[FlareExfil] flare resolved point='{pointName}' via=scene-trigger (no detector zone in this scene, " +
                $"evidence='{evidence}') - the flare event is the only evidence this map carries, so the point is " +
                "accepted from the shot position");
            return true;
        }

        zoneId = ZoneName(zone);
        if (string.IsNullOrEmpty(zoneId))
        {
            return false;
        }

        // The exit that belongs to the zone wins over the exit that happens to be closest to the
        // player: the association was resolved from the scene geometry when the raid bound the exits.
        var origin = "zone-association";
        pointName = ResolvePointForZone(zoneId);
        if (string.IsNullOrEmpty(pointName))
        {
            origin = "nearest-exit-to-flare";
            pointName = FindNearestPointName(shotPosition);
        }

        if (string.IsNullOrEmpty(pointName))
        {
            origin = "nearest-exit-to-player";
            pointName = FindNearestPointName(playerPosition);
        }

        if (!string.IsNullOrEmpty(pointName))
        {
            LogOnce($"resolved:{zoneId}:{pointName}",
                $"[FlareExfil] flare resolved zone='{zoneId}' flareType={DescribeFlareType(zone)} exitZone={exitZone} -> point='{pointName}' (via={origin})");
        }

        return !string.IsNullOrEmpty(zoneId) && !string.IsNullOrEmpty(pointName);
    }

    /// <summary>The exit the given detector zone activates, when the raid bind resolved one.</summary>
    private static string ResolvePointForZone(string zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
        {
            return null;
        }

        lock (Sync)
        {
            if (PointByFlareZone.TryGetValue(zoneId, out var associated) && !string.IsNullOrEmpty(associated))
            {
                return associated;
            }
        }

        return null;
    }

    private static bool TryFindZone(Vector3 position, out FlareShootDetectorZone zone, out bool exitZone)
    {
        zone = null;
        exitZone = false;

        var zones = UnityEngine.Object.FindObjectsOfType<FlareShootDetectorZone>(true);
        if (zones == null || zones.Length == 0)
        {
            return false;
        }

        FlareShootDetectorZone containingExit = null;
        FlareShootDetectorZone containingAny = null;
        FlareShootDetectorZone nearest = null;
        var nearestIsExit = false;
        var nearestDistance = float.MaxValue;
        foreach (var candidate in zones)
        {
            if (candidate == null)
            {
                continue;
            }

            var candidateIsExit = IsExitZone(candidate);

            var contains = false;
            try
            {
                var collider = GetZoneCollider(candidate);
                contains = collider != null && collider.bounds.Contains(position);
            }
            catch (Exception)
            {
                // A zone without a usable collider falls back to the distance check below.
            }

            if (contains)
            {
                containingAny ??= candidate;
                if (candidateIsExit)
                {
                    containingExit = candidate;
                    break;
                }
            }

            float distance;
            try
            {
                distance = Vector3.Distance(candidate.transform.position, position);
            }
            catch (Exception)
            {
                continue;
            }

            var better = nearest == null
                || (candidateIsExit && !nearestIsExit)
                || (candidateIsExit == nearestIsExit && distance < nearestDistance);
            if (distance <= ZoneRange && better)
            {
                nearestDistance = distance;
                nearest = candidate;
                nearestIsExit = candidateIsExit;
            }
        }

        // A green flare (ExitActivate) zone always wins over an airdrop, quest or sniper flare zone
        // that happens to cover the same patch of ground.
        zone = containingExit ?? containingAny ?? nearest;
        exitZone = zone != null && IsExitZone(zone);
        return zone != null;
    }

    private static Collider GetZoneCollider(FlareShootDetectorZone zone)
    {
        var collider = zone.GetComponent<Collider>();
        if (collider != null)
        {
            return collider;
        }

        collider = zone.GetComponentInParent<Collider>();
        if (collider != null)
        {
            return collider;
        }

        return zone.GetComponentInChildren<Collider>(true);
    }

    private static string ZoneName(FlareShootDetectorZone zone)
    {
        try
        {
            var id = zone.zoneID;
            if (!string.IsNullOrEmpty(id))
            {
                return id;
            }

            return zone.gameObject != null ? zone.gameObject.name : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Nearest regular exit point to the position, within <see cref="PointRange"/>. The flare
    /// area of a green flare exit sits at the exit, so the closest point is the one the flare
    /// belongs to. Secret exits are left out: their activation is not flare driven.
    /// </summary>
    private static string FindNearestPointName(Vector3 position)
    {
        var controller = ExfiltrationController.Instance;
        if (controller == null)
        {
            return null;
        }

        var bestName = null as string;
        var bestDistance = float.MaxValue;

        bestName = ConsiderPoints(controller.ExfiltrationPoints, position, bestName, ref bestDistance);
        return bestName;
    }

    private static string ConsiderPoints(ExfiltrationPoint[] points, Vector3 position, string bestName, ref float bestDistance)
    {
        if (points == null)
        {
            return bestName;
        }

        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point == null)
            {
                continue;
            }

            float distance;
            try
            {
                distance = Vector3.Distance(point.transform.position, position);
            }
            catch (Exception)
            {
                continue;
            }

            if (distance <= PointRange && distance < bestDistance)
            {
                bestDistance = distance;
                bestName = point.Settings != null ? point.Settings.Name : null;
            }
        }

        return bestName;
    }

    private static string LocalSquadId(Player player)
    {
        if (player == null)
        {
            return string.Empty;
        }

        return string.IsNullOrEmpty(player.TeamId) ? player.GroupId : player.TeamId;
    }

    private static string FindSquadId(string profileId)
    {
        var shooter = FindCoopPlayer(profileId);
        return shooter == null ? string.Empty : LocalSquadId(shooter);
    }

    private static FikaPlayer FindCoopPlayer(string profileId)
    {
        try
        {
            var handler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
            if (handler?.Players == null || handler.Players.Count == 0)
            {
                return null;
            }

            foreach (var candidate in handler.Players.Values)
            {
                if (candidate != null && string.Equals(candidate.ProfileId, profileId, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] coop player lookup failed: {exception}");
        }

        return null;
    }

    /// <summary>Profiles of the squad (everyone except the shooter).</summary>
    private static List<string> CollectGroupProfiles(string squadId, string shooterProfileId)
    {
        if (string.IsNullOrEmpty(squadId))
        {
            return null;
        }

        try
        {
            var handler = FikaSingleton<IFikaNetworkManager>.Instance?.CoopHandler;
            if (handler?.Players == null || handler.Players.Count == 0)
            {
                return null;
            }

            List<string> profiles = [];
            foreach (var candidate in handler.Players.Values)
            {
                if (candidate == null || string.Equals(candidate.ProfileId, shooterProfileId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(LocalSquadId(candidate), squadId, StringComparison.Ordinal))
                {
                    profiles.Add(candidate.ProfileId);
                }
            }

            return profiles.Count > 0 ? profiles : null;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] squad lookup failed: {exception}");
            return null;
        }
    }

    private static bool IsPlayerBanned(int raidId)
    {
        try
        {
            var controller = ExfiltrationController.Instance;
            var banned = controller?.BannedPlayers;
            return banned != null && banned.Contains(raidId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Reject(string profileId, string reason)
    {
        FikaGlobals.LogWarning($"[FlareExfil] rejected profile={profileId ?? string.Empty} reason={reason}");
    }

    /// <summary>
    /// What a detector zone feeds on this machine: the once per raid table first, the live scene
    /// second, so a grant that arrives before (or without) the classification still resolves.
    /// Immunity is sticky; everything else keeps the pre-existing gate rule exactly
    /// (<c>exitZone || IsGatedPoint</c>), so no zone that used to open an exit loses that.
    /// </summary>
    private static ZoneKind KindOf(string zoneId, bool exitZone, string pointName)
    {
        if (!string.IsNullOrEmpty(zoneId))
        {
            lock (Sync)
            {
                if (ZoneKinds.TryGetValue(zoneId, out var known) && known == ZoneKind.Immunity)
                {
                    return ZoneKind.Immunity;
                }
            }

            var zone = FindZoneById(zoneId);
            if (zone != null && ImmunityEvidence(zone, IsGatedPoint(pointName)) != null)
            {
                return ZoneKind.Immunity;
            }
        }

        return exitZone || IsGatedPoint(pointName) ? ZoneKind.ExitGate : ZoneKind.Other;
    }

    /// <summary>
    /// Evidence that this zone feeds the native sniper immunity registry instead of an exit, or null
    /// when it does not.
    /// <para>
    /// Primary criterion, taken from the 47888 interop member list
    /// (<c>analysis/flare-exfil-verification/interop-47888-full.txt:407404</c> for the class and
    /// <c>:407423</c> for the collector): the zone object, one of its parents or one of its children
    /// carries a <c>EFT.Interactive.PlayersWithImmuneToSniperFire</c> component. That class owns
    /// <c>playersWithImmune : HashSet&lt;Int32&gt;</c> with <c>AddImmunePlayer(Int32)</c> /
    /// <c>IsPlayerImmuneForFire(Int32)</c>, which is the set the sniper zones read; its
    /// <c>PlayersWithImmuneToSniperFireFlareCollector</c> subclass adds the firing player's raid id
    /// when it consumes <c>FlareShootZoneEvent</c> and is the Ground Zero object named
    /// <c>FlareFireCollector_zone</c>. The collector's own <c>flareTypeForImmune</c> is printed as
    /// part of the evidence, so the line shows both objects handle the same flare type.
    /// </para>
    /// <para>
    /// Fallback when a scene keeps the collector on a sibling object: a green (<c>ExitActivate</c>)
    /// zone that gates nothing, in a raid that does have such a registry, and whose object name says
    /// what it is.
    /// </para>
    /// </summary>
    private static string ImmunityEvidence(FlareShootDetectorZone zone, bool pointGated)
    {
        if (zone == null)
        {
            return null;
        }

        try
        {
            var own = zone.GetComponent<PlayersWithImmuneToSniperFire>();
            if (own != null)
            {
                return DescribeRegistry("self", own);
            }

            var parent = zone.GetComponentInParent<PlayersWithImmuneToSniperFire>();
            if (parent != null)
            {
                return DescribeRegistry("parent", parent);
            }

            var child = zone.GetComponentInChildren<PlayersWithImmuneToSniperFire>(true);
            if (child != null)
            {
                return DescribeRegistry("child", child);
            }
        }
        catch (Exception exception)
        {
            LogOnce("immuneprobe:" + ZoneName(zone),
                $"[FlareExfil] immunity registry lookup on zone '{ZoneName(zone)}' failed: {exception.Message}");
        }

        if (pointGated || !IsExitZone(zone))
        {
            return null;
        }

        var zoneId = ZoneName(zone);
        if (!LooksLikeImmunityZone(zoneId))
        {
            return null;
        }

        var registries = CountImmunityRegistries();
        return registries > 0 ? $"name:{ObjectNameOf(zoneId)}(registries={registries})" : null;
    }

    private static string DescribeRegistry(string where, PlayersWithImmuneToSniperFire registry)
    {
        var type = RegistryTypeName(registry);
        var text = where + ":" + type;
        try
        {
            var collector = registry.TryCast<PlayersWithImmuneToSniperFireFlareCollector>();
            if (collector == null)
            {
                // A plain registry (the sniper manager) is evidence enough for the verdict, but the
                // flare type cannot be read from it. Say so once per type instead of silently
                // dropping the half of the evidence the verdict often rests on.
                LogOnce("immuneprobe-cast:" + type,
                    $"[FlareExfil] immunity registry '{type}' is not a PlayersWithImmuneToSniperFireFlareCollector (where={where}), " +
                    "so its flareTypeForImmune is missing from the zone evidence");
                return text;
            }

            text += $"(flareTypeForImmune={DescribeFlareType(collector.flareTypeForImmune)})";
        }
        catch (Exception exception)
        {
            LogOnce("immuneprobe-flaretype:" + type,
                $"[FlareExfil] immunity registry '{type}' could not report flareTypeForImmune ({exception.Message}); " +
                "the zone evidence keeps the registry type only");
        }

        return text;
    }

    private static string RegistryTypeName(PlayersWithImmuneToSniperFire registry)
    {
        try
        {
            var type = registry.GetIl2CppType();
            if (type != null && !string.IsNullOrEmpty(type.Name))
            {
                return type.Name;
            }
        }
        catch (Exception)
        {
            // Falls back to the managed proxy type below.
        }

        try
        {
            return registry.GetType().Name;
        }
        catch (Exception)
        {
            return nameof(PlayersWithImmuneToSniperFire);
        }
    }

    /// <summary>The last path segment of a zone id: <c>Sandbox_Design_Main:/EXITS/FlareFireCollector_zone</c> -&gt; <c>FlareFireCollector_zone</c>.</summary>
    private static string ObjectNameOf(string zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
        {
            return string.Empty;
        }

        var cut = zoneId.LastIndexOf('/');
        if (cut >= 0 && cut + 1 < zoneId.Length)
        {
            return zoneId[(cut + 1)..];
        }

        cut = zoneId.LastIndexOf(':');
        return cut >= 0 && cut + 1 < zoneId.Length ? zoneId[(cut + 1)..] : zoneId;
    }

    /// <summary>
    /// The object name Ground Zero gives the sniper immunity zone. Only used as the fallback half of
    /// <see cref="ImmunityEvidence"/>, never on its own.
    /// </summary>
    private static bool LooksLikeImmunityZone(string zoneId)
    {
        return !string.IsNullOrEmpty(zoneId)
            && zoneId.IndexOf("FlareFireCollector", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static PlayersWithImmuneToSniperFire[] FindImmunityRegistries()
    {
        try
        {
            // FindObjectsOfType returns the Il2Cpp array wrapper; the managed array keeps the call
            // sites free of the wrapper's type.
            PlayersWithImmuneToSniperFire[] registries = UnityEngine.Object.FindObjectsOfType<PlayersWithImmuneToSniperFire>(true);
            return registries ?? [];
        }
        catch (Exception exception)
        {
            LogOnce("immuneregistry", $"[FlareExfil] sniper immunity registry lookup failed: {exception.Message}");
            return [];
        }
    }

    private static int CountImmunityRegistries()
    {
        return FindImmunityRegistries().Length;
    }

    /// <summary>
    /// The numeric raid id of a profile. It is the key of the native flare bookkeeping
    /// (<c>FlareShootZoneEvent.PlayerRaidID</c>) and of the sniper immunity set
    /// (<c>playersWithImmune : HashSet&lt;Int32&gt;</c>).
    /// </summary>
    private static bool TryResolveRaidId(string profileId, out int raidId, out string reason)
    {
        raidId = 0;
        reason = null;
        if (string.IsNullOrEmpty(profileId))
        {
            reason = "empty profile id";
            return false;
        }

        try
        {
            var player = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(profileId);
            if (player == null)
            {
                reason = "unknown player";
                return false;
            }

            raidId = player.RaidId;
            if (raidId <= 0)
            {
                reason = $"raid id {raidId}";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Writes the native sniper immunity the flare grants, on this machine, for every profile the
    /// host decided the activation covers: their raid ids go into each
    /// <c>PlayersWithImmuneToSniperFire</c> registry, which is the set
    /// (<c>playersWithImmune : HashSet&lt;Int32&gt;</c>) the sniper zones read through
    /// <c>IsPlayerImmuneForFire(Int32)</c> and which the <c>HasSniperImmunityTriggerRequirement</c>
    /// game trigger reads as well.
    /// <para>
    /// No duration is invented here: the native set has no expiry field and <c>AddImmunePlayer</c>
    /// is a <c>HashSet.Add</c>, so a repeated flare renews the entry, and the only way out is the
    /// native <c>RemoveImmunePlayer</c> that the map triggers call. The read back through
    /// <c>IsPlayerImmuneForFire</c> is what the log line reports.
    /// </para>
    /// <para>
    /// Every machine writes the ids itself, because the vanilla flare only ever reaches the machine
    /// that fired it (the zone consumes a local <c>FlareSuccessEvent</c> and raises
    /// <c>FlareShootZoneEvent</c> locally), while each machine simulates its own sniper zone.
    /// </para>
    /// <para>
    /// The success path also ends with the player-facing half vanilla shows
    /// (<see cref="ShowFlareSuccessFeedback"/>): the covered player's own machine pops the same
    /// <c>SniperFlareZoneDisabledNotification</c> vanilla pops, because nothing in this activation
    /// ever enters the native event branch that owns that notification.
    /// </para>
    /// </summary>
    private static void ApplyImmunity(string zoneId, List<string> profiles, string source)
    {
        var total = profiles?.Count ?? 0;
        var registries = FindImmunityRegistries();
        if (registries.Length == 0)
        {
            // State, not a one-shot line: a registry that appears later has to be visible as the
            // recovery it is, otherwise the raid keeps reading the first failure as the current truth.
            LogFailure("immunity-registry:" + zoneId, true,
                $"[FlareExfil] immunity zone='{zoneId}' has no PlayersWithImmuneToSniperFire registry on this machine, " +
                $"so the sniper keeps firing at {total} player(s) (source={source})",
                string.Empty);
            return;
        }

        LogFailure("immunity-registry:" + zoneId, false, string.Empty,
            $"[FlareExfil] immunity zone='{zoneId}' can be written again: {registries.Length} PlayersWithImmuneToSniperFire " +
            $"registr(ies) are present (source={source})");

        // The feedback below belongs to exactly one player: this machine's own. Both facts it needs
        // are read before the write, because afterwards a read back cannot tell "this call put it
        // there" from "the native chain had it already".
        var localProfileId = Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
        var localCovered = IsCovered(profiles, localProfileId);
        var localRaidId = 0;
        var localWasImmune = false;
        if (localCovered && TryResolveRaidId(localProfileId, out var resolvedLocalRaidId, out _))
        {
            localRaidId = resolvedLocalRaidId;
            localWasImmune = IsImmuneInAnyRegistry(registries, resolvedLocalRaidId);
        }

        var resolved = new List<int>();
        var unresolved = new List<string>();
        if (profiles != null)
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                var profileId = profiles[i];
                if (string.IsNullOrEmpty(profileId))
                {
                    continue;
                }

                if (!TryResolveRaidId(profileId, out var raidId, out var reason))
                {
                    unresolved.Add($"{profileId}:{reason}");
                    continue;
                }

                if (resolved.Contains(raidId))
                {
                    continue;
                }

                resolved.Add(raidId);
                for (var r = 0; r < registries.Length; r++)
                {
                    try
                    {
                        registries[r].AddImmunePlayer(raidId);
                    }
                    catch (Exception exception)
                    {
                        LogOnce("immunity-write:" + zoneId,
                            $"[FlareExfil] AddImmunePlayer({raidId}) on registry '{RegistryTypeName(registries[r])}' failed: {exception.Message}");
                    }
                }
            }
        }

        var verified = 0;
        var missing = new List<int>();
        for (var i = 0; i < resolved.Count; i++)
        {
            if (IsImmuneInAnyRegistry(registries, resolved[i]))
            {
                verified++;
            }
            else
            {
                missing.Add(resolved[i]);
            }
        }

        var raidIds = resolved.Count == 0 ? "none" : string.Join(",", resolved);
        LogOnce("immunity:" + zoneId,
            $"[FlareExfil] immunity granted zone='{zoneId}' for {total} players (raidIds=[{raidIds}]) verified={verified}/{total}" +
            $" registries={registries.Length}" +
            (unresolved.Count > 0 ? $" unresolved=[{string.Join(";", unresolved)}]" : string.Empty) +
            (verified < total ? $" missing=[{(missing.Count == 0 ? "none" : string.Join(",", missing))}]" : string.Empty) +
            $" source={source}");

        LogFailure("immunity-verified:" + zoneId, verified < total,
            $"[FlareExfil] immunity partial zone='{zoneId}' verified={verified}/{total} resolved={resolved.Count} " +
            $"registries={registries.Length} - the native sniper immunity set did not take every raid id, " +
            $"so the sniper can still fire at them (source={source})",
            $"[FlareExfil] immunity complete again zone='{zoneId}' verified={verified}/{total} (source={source})");

        // Success path only, and only on the machine of a player this grant covers: the read back has
        // just proved that the native set answers for the local player, which is exactly the state
        // vanilla reports to that player with "Flare signal received. You can extract safely!".
        // A rejected report, an unwritable registry or a raid id that did not resolve never gets here.
        if (localCovered && localRaidId > 0 && IsImmuneInAnyRegistry(registries, localRaidId))
        {
            ShowFlareSuccessFeedback(zoneId, localProfileId, source, localWasImmune);
        }
    }

    /// <summary>
    /// The player-facing half of a successful activation, and the same notification vanilla shows:
    /// once a flare put the player into the native sniper immunity set, the game pops
    /// <see cref="SniperFlareZoneDisabledNotification"/> ("Flare signal received. You can extract
    /// safely!" / CN "道路已开放"). That popup lives in the native collector's own event branch
    /// (<c>PlayersWithImmuneToSniperFireFlareCollector._flareExitSucceedNotification</c>), which only
    /// runs on the machine that consumed its own local <c>FlareSuccessEvent</c> - a replicated flare
    /// never reaches it, so this shows the very same notification object through the game's own
    /// notification manager, which is the call the native branch makes as well.
    /// <para>
    /// One line per covered player and zone: only a covered player's own machine gets here with its
    /// own profile id, so host and clients each pop for themselves, and the 10 s grant republication
    /// cannot pop it twice. The display is remembered only after it succeeded, so a popup the game
    /// did not accept is retried on the next grant instead of being lost.
    /// </para>
    /// </summary>
    private static void ShowFlareSuccessFeedback(string zoneId, string profileId, string source, bool nativeAlreadyImmune)
    {
        var key = zoneId + ":" + profileId;
        lock (Sync)
        {
            if (FeedbackShown.Contains(key))
            {
                return;
            }
        }

        try
        {
            var notification = new SniperFlareZoneDisabledNotification();
            NotificationManager.DisplayNotification(notification);

            // Nothing in the game hides a notification this class displayed: the native collector
            // owns and manages its own copy (_flareExitSucceedNotification), and no controller owns
            // ours. The popup is therefore booked here and taken down again by FeedbackTick once its
            // display time is over.
            var seconds = FeedbackSeconds;
            lock (Sync)
            {
                FeedbackShown.Add(key);
                if (seconds > 0f)
                {
                    FeedbackPopups.Add(new FeedbackPopup
                    {
                        Notification = notification,
                        Key = key,
                        ZoneId = zoneId,
                        ProfileId = profileId,
                        Seconds = seconds,
                        HideAt = Time.time + seconds
                    });
                }
            }

            LogOnce("feedback:" + key,
                $"[FlareExfil] flare success feedback shown zone='{zoneId}' local='{profileId}' " +
                $"notification={NotificationTypeName(notification)} text='{DescribeNotification(notification)}' " +
                $"nativeAlreadyImmune={nativeAlreadyImmune} (source={source})");
        }
        catch (Exception exception)
        {
            LogFailure("feedback:" + key, true,
                $"[FlareExfil] flare success feedback could not be shown for zone='{zoneId}' local='{profileId}' " +
                $"({exception.Message}) - the immunity is written, but this player sees no 'can extract' message (source={source})",
                string.Empty);
        }
    }

    /// <summary>
    /// Takes every displayed success popup off the screen again once its display time is over.
    /// Driven from the exfil manager's own Update (the boundary that already pumps
    /// <see cref="HostTick"/> on both machines), so no engine coroutine and no engine -&gt; managed
    /// callback is involved, which is the shape the crash reports ruled out.
    /// <para>
    /// Idempotent by construction: an entry leaves <see cref="FeedbackPopups"/> exactly once, the
    /// class keeps one notification object per popup, and hiding an object can therefore neither
    /// repeat for the same popup nor reach a popup that is still within its display time. An empty
    /// list costs one lock and returns, so a raid without a flare pays nothing per frame.
    /// </para>
    /// </summary>
    internal static void FeedbackTick()
    {
        // Drives the exit-visual replays as well: a replay waits for the shooter's flare object or for its
        // deadline, and this is the tick both host and client already run every frame.
        if (HasPendingVisuals())
        {
            try
            {
                FlushPendingVisuals();
            }
            catch (Exception exception)
            {
                FikaGlobals.LogError($"[FlareExfil] heli visual replay failed: {exception}");
            }
        }

        if (!HasFeedbackPopups())
        {
            return;
        }

        try
        {
            var now = Time.time;
            var due = null as List<FeedbackPopup>;
            lock (Sync)
            {
                for (var i = 0; i < FeedbackPopups.Count; i++)
                {
                    var popup = FeedbackPopups[i];
                    if (now >= popup.HideAt)
                    {
                        (due ??= []).Add(popup);
                    }
                }
            }

            if (due == null)
            {
                return;
            }

            // Only a frame that really has work to do reads the raid state: a new world (fresh raid,
            // transit reload) clears the table, so the popups of the previous raid are dropped here
            // and no stale notification object is ever touched.
            SyncRaidState();

            for (var i = 0; i < due.Count; i++)
            {
                var popup = due[i];
                var claimed = false;
                lock (Sync)
                {
                    // Taking the entry out of the table is the claim: it succeeds once, so a popup is
                    // hidden exactly once and a popup the world change dropped is never hidden at all.
                    claimed = FeedbackPopups.Remove(popup);
                }

                if (claimed)
                {
                    HideFeedbackPopup(popup);
                }
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[FlareExfil] taking the flare success feedback down failed: {exception}");
        }
    }

    private static bool HasFeedbackPopups()
    {
        lock (Sync)
        {
            return FeedbackPopups.Count > 0;
        }
    }

    /// <summary>
    /// Removes one displayed popup. <c>Hide()</c> is this notification family's own removal call: it
    /// is declared by <c>FireFlareForExitNotification</c>, the base of the class shown here, and the
    /// base notification carries the <c>OnForceHide</c> event that the view host reacts to (the
    /// notification view hides through its own forced-complete path and the host removes the view).
    /// The native collector owns a copy of exactly this notification and has the same call available.
    /// </summary>
    private static void HideFeedbackPopup(FeedbackPopup popup)
    {
        try
        {
            popup.Notification?.Hide();

            LogOnce("feedback-hidden:" + popup.Key,
                $"[FlareExfil] flare success feedback hidden after {popup.Seconds:0.#}s zone='{popup.ZoneId}' " +
                $"local='{popup.ProfileId}' notification=SniperFlareZoneDisabledNotification " +
                "(switch 'Flare Extract: Feedback Seconds')");
        }
        catch (Exception exception)
        {
            LogFailure("feedback-hide:" + popup.Key, true,
                $"[FlareExfil] flare success feedback could not be hidden zone='{popup.ZoneId}' " +
                $"local='{popup.ProfileId}' ({exception.Message}) - the banner stays on screen until the " +
                "raid ends (source=Hide after the display time)",
                string.Empty);
        }
    }

    /// <summary>
    /// The text a notification would show, for the log line only. Every accessor of a native
    /// notification can throw, so a failed read stays a log detail instead of taking the flare path
    /// down with it.
    /// </summary>
    private static string DescribeNotification(EFT.Communications.Notification notification)
    {
        try
        {
            var description = notification?.Description;
            if (description == null)
            {
                return "none";
            }

            var text = description.ToString();
            return string.IsNullOrWhiteSpace(text) ? "none" : text;
        }
        catch (Exception exception)
        {
            return "unreadable (" + exception.Message + ")";
        }
    }

    /// <summary>
    /// The native class name of a notification, for the log line only. The proxy type name is the
    /// fallback, so the line names the notification even when the native type cannot be read.
    /// </summary>
    private static string NotificationTypeName(EFT.Communications.Notification notification)
    {
        try
        {
            var type = notification?.GetIl2CppType();
            if (type != null && !string.IsNullOrEmpty(type.Name))
            {
                return type.Name;
            }
        }
        catch (Exception)
        {
            // Falls back to the managed proxy type below.
        }

        try
        {
            return notification?.GetType().Name ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// True when the grant covers <paramref name="profileId"/>. Ordinal comparison, like every other
    /// profile id check in this class.
    /// </summary>
    private static bool IsCovered(List<string> profiles, string profileId)
    {
        if (profiles == null || string.IsNullOrEmpty(profileId))
        {
            return false;
        }

        for (var i = 0; i < profiles.Count; i++)
        {
            if (string.Equals(profiles[i], profileId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether any of this machine's registries answers "immune" for the raid id - the same query the
    /// sniper zones run, so a true here means the sniper really is standing down for that player.
    /// </summary>
    private static bool IsImmuneInAnyRegistry(PlayersWithImmuneToSniperFire[] registries, int raidId)
    {
        for (var r = 0; r < registries.Length; r++)
        {
            try
            {
                if (registries[r].IsPlayerImmuneForFire(raidId))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // A registry that cannot answer counts as not immune, exactly as before.
            }
        }

        return false;
    }

    /// <summary>
    /// The profiles a grant covers on this machine: the shooter, the shooter's squad while
    /// <c>Flare Extract: Squad Shares Activation</c> is on, and - when the grant covers the local
    /// player - the local player, so every machine contributes its own raid id even if its view of
    /// the squad is incomplete.
    /// </summary>
    private static List<string> CoveredProfiles(FlareExfilActivationPacket packet, string ownProfileId, bool covered)
    {
        var profiles = new List<string>();
        if (!string.IsNullOrEmpty(packet.ProfileId))
        {
            profiles.Add(packet.ProfileId);
        }

        if (SquadSharesActivation && !string.IsNullOrEmpty(packet.SquadId))
        {
            var squad = CollectGroupProfiles(packet.SquadId, packet.ProfileId);
            if (squad != null)
            {
                profiles.AddRange(squad);
            }
        }

        if (covered && !string.IsNullOrEmpty(ownProfileId) && !profiles.Contains(ownProfileId))
        {
            profiles.Add(ownProfileId);
        }

        return profiles;
    }

    /// <summary>
    /// Reports a failure when it starts and its recovery when it ends, keyed per failure.
    /// <para>
    /// Unlike <see cref="LogOnce"/> this cannot hide "failed first, then healed": a healthy first
    /// observation prints nothing (so a normal raid stays quiet), the first failure prints the whole
    /// failure line, and the transition back prints the recovery line.
    /// </para>
    /// </summary>
    private static void LogFailure(string key, bool failing, string failureLine, string recoveryLine)
    {
        bool changed;
        lock (Sync)
        {
            if (Failing.TryGetValue(key, out var wasFailing))
            {
                changed = wasFailing != failing;
            }
            else
            {
                changed = failing;
            }

            Failing[key] = failing;
        }

        if (!changed)
        {
            return;
        }

        FikaGlobals.LogWarning(failing ? failureLine : recoveryLine);
    }

    /// <summary>How many entries of an interop array are destroyed references.</summary>
    // ---------------------------------------------------------------------------------------------------
    // Exit visual replication: what the shooter's machine plays on its own has to play on every machine.
    //
    // The chain a signal flare starts on the machine that fires it is
    //   Player/FirearmController.InitiateFlare -> <c>FlareSuccessEvent</c> on the local event bus
    //   -> every scene <c>EFT.GameTriggers.TriggerFlareSuccess</c> whose <c>_flareEventType</c> matches
    //   -> <c>TriggersEmitter</c> with the trigger's id
    //   -> the handlers of the map (<c>HandlerAnimator</c>, <c>HandlerGameObjectState</c>,
    //      <c>HandlerPlayContinousSound</c>, <c>HandlerEffect</c>, <c>HandlerExfiltration</c>, ...),
    // which is what animates the icebreaker helicopter and makes the exit usable.
    //
    // Every one of those objects lives in the loaded scene on every machine, so the chain can simply be run
    // again on each peer. Nothing is invented here: the exact native entry point
    // (<c>TriggerFlareSuccess.OnFlareSuccess</c>) is called with the same event data the shooter's machine
    // used, and the map decides what that means.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Replays waiting for their own local flare event or their deadline, keyed by profile id. A grant can
    /// arrive before the shooter's flare packet does; applying the visual then would start the scene chain
    /// without the flare object the map's handlers read.
    /// </summary>
    private static readonly Dictionary<string, PendingVisual> PendingVisuals = new(StringComparer.Ordinal);

    /// <summary>
    /// Visuals already applied on this machine, keyed the way <see cref="VisualIdOf"/> builds the key. A grant
    /// is republished every <see cref="RepublishInterval"/> seconds, and the shooter's own machine sees both
    /// its local flare and the grant, so without this the animation would start again on every publication.
    /// </summary>
    private static readonly HashSet<string> AppliedVisuals = new(StringComparer.Ordinal);

    /// <summary>
    /// Seconds a pending visual waits for the shooter's local flare event before it is applied anyway. A
    /// machine that never sees the flare object still shows the exit animation rather than nothing.
    /// </summary>
    private const float VisualWaitSeconds = 1.5f;

    /// <summary>
    /// The switch 'Flare Extract: Replicate Exit Visual'. Read live, so a change in the config manager
    /// applies to the next flare without a restart.
    /// </summary>
    internal static bool ReplicatesExitVisual
    {
        get
        {
            try
            {
                var entry = FikaPlugin.Instance?.Settings?.FlareReplicateExitVisual;
                return entry == null || entry.Value;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Called from the shooter's own firearm controller, right after the shot was created: the native chain
    /// usually runs on its own here, so the visual only has to be applied when it did not (measured on the
    /// local flare event, see <see cref="MarkLocalFlareEvent"/>).
    /// </summary>
    private static void QueueLocalVisual(Ammo flareItem, string pointName, string profileId, Vector3 shotPosition)
    {
        if (!ReplicatesExitVisual || string.IsNullOrEmpty(profileId))
        {
            return;
        }

        var template = flareItem?.AmmoTemplate;
        if (template == null || !template.ContainsType(FlareEventType.ExitActivate))
        {
            return;
        }

        var key = VisualIdOf(pointName, profileId, shotPosition);
        var applied = false;
        lock (Sync)
        {
            applied = AppliedVisuals.Contains(key);
            if (!applied)
            {
                // The native chain runs on this machine by itself a moment after the shot, and the wait is what
                // lets it stop this replay (see MarkLocalFlareEvent). Without the wait this machine would play
                // the animation twice.
                PendingVisuals[profileId] = new PendingVisual
                {
                    PointName = pointName ?? string.Empty,
                    Position = shotPosition,
                    Holder = flareItem,
                    HolderArrived = true,
                    ReadyAt = Time.time + VisualWaitSeconds
                };
            }
        }

        if (applied)
        {
            return;
        }

        // Only the case where nothing ever runs here reaches the deadline: then this machine plays the chain
        // itself instead of showing nothing. It is logged now so the next raid can tell the two apart.
        FikaGlobals.LogWarning($"[FlareExfil] heli: point='{pointName}' open={DescribeLocalStatus(pointName)} " +
            $"visual=local-armed firedBy='{profileId}' replayIn={VisualWaitSeconds:F1}s " +
            "unless the native chain runs here first");
    }

    /// <summary>
    /// The local native chain for a green flare ran on this machine: the machine that fired it never needs a
    /// replay, so the pending visual is satisfied instead of applied. Called once per local flare event, which
    /// is why the evidence line is printed here rather than from the replay path.
    /// </summary>
    internal static void MarkLocalFlareEvent(string profileId, FlareEventType flareType, string source)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return;
        }

        var pending = false;
        lock (Sync)
        {
            pending = PendingVisuals.Remove(profileId);
        }

        if (flareType == FlareEventType.ExitActivate || pending)
        {
            LogOnce("localflare:" + profileId,
                $"[FlareExfil] heli: point=? open=? visual=local firedBy='{profileId}' flareType={DescribeFlareType(flareType)} " +
                $"chain=ran-local source={source}");
        }
    }

    /// <summary>
    /// A peer's flare event was queued for this machine. The holder has to be the object the shooter fired, so
    /// the queue waits until that object exists here (the flare packet) or its deadline passes.
    /// </summary>
    private static void QueueFlareVisual(FlareExfilActivationPacket packet)
    {
        if (packet == null || string.IsNullOrEmpty(packet.ProfileId))
        {
            return;
        }

        if (packet.FlareDirection == Vector3.zero)
        {
            // No position was carried (an older grant, or an immunity grant that never had one): there is
            // nothing to place, and starting the chain at the world origin would be wrong.
            return;
        }

        if (!ReplicatesExitVisual)
        {
            LogOnce("visual-off:" + packet.ProfileId + ":" + packet.PointName,
                $"[FlareExfil] heli: point='{packet.PointName}' open=? visual=skipped firedBy='{packet.ProfileId}' " +
                $"reason='Flare Extract: Replicate Exit Visual is off'");
            return;
        }

        var key = VisualIdOf(packet.PointName, packet.ProfileId, packet.FlareDirection);
        var applied = false;
        lock (Sync)
        {
            applied = AppliedVisuals.Contains(key);
            if (!applied)
            {
                var holder = FindFlareHolder(packet.ProfileId);
                var now = Time.time;
                PendingVisuals[packet.ProfileId] = new PendingVisual
                {
                    PointName = packet.PointName,
                    Position = packet.FlareDirection,
                    Holder = holder,
                    HolderArrived = holder != null,
                    ReadyAt = holder != null ? now : now + VisualWaitSeconds
                };
            }
        }

        if (applied)
        {
            return;
        }

        // The flare packet may be one frame behind the grant: asking here keeps the animation on the flare
        // instead of on the packet, and the tick below covers the other order.
        MatchArrivedFlare(packet.ProfileId);
    }

    /// <summary>
    /// One pass over the pending visuals. Called from the host and client tick, so a replay is applied as soon
    /// as its holder exists and never blocks a frame.
    /// </summary>
    internal static void FlushPendingVisuals()
    {
        List<string> flush = null;
        var now = Time.time;
        lock (Sync)
        {
            foreach (var entry in PendingVisuals)
            {
                var applied = AppliedVisuals.Contains(VisualIdOf(entry.Value.PointName, entry.Key, entry.Value.Position));
                if (applied)
                {
                    (flush ??= []).Add(entry.Key);
                    continue;
                }

                if (!entry.Value.HolderArrived && now >= entry.Value.ReadyAt)
                {
                    entry.Value.Holder = FindFlareHolder(entry.Key);
                    entry.Value.HolderArrived = entry.Value.Holder != null;
                    entry.Value.ReadyAt = now;
                }

                if (entry.Value.HolderArrived && now >= entry.Value.ReadyAt)
                {
                    (flush ??= []).Add(entry.Key);
                }
            }

            if (flush != null)
            {
                for (var i = 0; i < flush.Count; i++)
                {
                    ApplyVisualLocked(flush[i]);
                }
            }
        }
    }

    /// <summary>
    /// The holder a replay needs: the flare object the shooter fired, which is the object the map's handlers
    /// read out of the flare event on the shooter's machine. A grant that arrives before the shooter's flare
    /// packet leaves the holder unset and the visual waits (see <see cref="VisualWaitSeconds"/>).
    /// </summary>
    private static void MatchArrivedFlare(string profileId)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return;
        }

        lock (Sync)
        {
            if (!PendingVisuals.TryGetValue(profileId, out var pending) || pending.HolderArrived)
            {
                return;
            }

            var holder = FindFlareHolder(profileId);
            if (holder == null)
            {
                return;
            }

            pending.Holder = holder;
            pending.HolderArrived = true;
        }
    }

    /// <summary>
    /// Finds the flare object the packet carried. The shooter's own machine creates it in the firearm
    /// controller; on a peer the same code path runs when the flare packet is applied
    /// (<c>FlareShotPacket.Execute</c> creates the <c>Ammo</c> through <c>ItemFactory</c>), so the object
    /// exists here too.
    /// </summary>
    private static Ammo FindFlareHolder(string profileId)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return null;
        }

        lock (Sync)
        {
            return LastFlareByProfile.TryGetValue(profileId, out var flare) ? flare : null;
        }
    }

    /// <summary>
    /// The last flare recorded on this machine for a profile, kept so a grant that arrives after the flare
    /// packet can still replay the chain with the same object.
    /// </summary>
    private static readonly Dictionary<string, Ammo> LastFlareByProfile = new(StringComparer.Ordinal);

    /// <summary>Records the flare a peer's packet just created on this machine.</summary>
    internal static void RememberRemoteFlareShot(string profileId, Ammo flare)
    {
        if (string.IsNullOrEmpty(profileId) || flare == null)
        {
            return;
        }

        lock (Sync)
        {
            LastFlareByProfile[profileId] = flare;
        }

        var template = flare.AmmoTemplate;
        if (template != null && template.ContainsType(FlareEventType.ExitActivate))
        {
            LogOnce("peerflare:" + profileId,
                $"[FlareExfil] heli: peer flare received profile='{profileId}' flareType={DescribeFlareType(FlareEventType.ExitActivate)} - " +
                "the exit chain can be replayed on this machine");
        }

        MatchArrivedFlare(profileId);
    }

    /// <summary>
    /// Applies one pending visual. The caller holds <see cref="Sync"/>.
    /// </summary>
    private static void ApplyVisualLocked(string profileId)
    {
        if (!PendingVisuals.TryGetValue(profileId, out var pending))
        {
            return;
        }

        PendingVisuals.Remove(profileId);

        var key = VisualIdOf(pending.PointName, profileId, pending.Position);
        if (!AppliedVisuals.Add(key))
        {
            return;
        }

        ApplyFlareVisual(profileId, pending.PointName, pending.Position, pending.Holder, out var hops);
        LogOnce("visual:" + profileId + ":" + pending.PointName,
            $"[FlareExfil] heli: point='{pending.PointName}' open={DescribeLocalStatus(pending.PointName)} visual=broadcast " +
            $"firedBy='{profileId}' triggers={hops} - this machine ran the same scene chain the shooter ran");
    }

    /// <summary>
    /// Runs the native flare chain for one flare on this machine, exactly as the shooter's own machine ran it:
    /// the same <c>FlareSuccessEvent</c> data through the same entry points of every
    /// <c>TriggerFlareSuccess</c> in the scene. Nothing is played directly - the map's own handlers decide
    /// what the trigger means (helicopter in, lights, sounds, the exit opening).
    /// </summary>
    private static void ApplyFlareVisual(string profileId, string pointName, Vector3 position, Ammo holder, out int hops)
    {
        hops = 0;

        Player firedPlayer = null;
        try
        {
            firedPlayer = Singleton<GameWorld>.Instance?.GetAlivePlayerByProfileID(profileId);
        }
        catch (Exception)
        {
            // A peer that is already gone cannot be the event's holder; the chain is still worth starting
            // for the exit itself.
        }

        if (firedPlayer == null)
        {
            try
            {
                firedPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
            }
            catch (Exception)
            {
                firedPlayer = null;
            }
        }

        TriggerFlareSuccess[] triggers;
        try
        {
            triggers = UnityEngine.Object.FindObjectsOfType<TriggerFlareSuccess>(true);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[FlareExfil] heli: trigger lookup failed for point='{pointName}' ({exception.Message})");
            return;
        }

        if (triggers == null || triggers.Length == 0)
        {
            // Nothing to start here, so the exit is opened directly: the grant already decided who may leave,
            // and a map without the scene chain must not lose the activation over a missing animation.
            OpenPointForVisual(pointName);
            LogOnce("heli-notriggers:" + pointName,
                $"[FlareExfil] heli: point='{pointName}' open={DescribeLocalStatus(pointName)} visual=broadcast-noop " +
                $"firedBy='{profileId}' reason='this scene has no TriggerFlareSuccess component'");
            return;
        }

        // The event is filled through its public Invoke: that is the same call the native shot path uses,
        // and it is the only way to set the data of an il2cpp event (its properties are get-only on the C#
        // side). The player position is not read by the chain - only the flare position is - so the same
        // spot is passed for both.
        FlareSuccessEvent flareEvent = new();
        flareEvent.Invoke(firedPlayer?.Cast<IPlayer>(), position, position, holder?.AmmoTemplate);

        for (var i = 0; i < triggers.Length; i++)
        {
            var trigger = triggers[i];
            if (trigger == null)
            {
                continue;
            }

            try
            {
                trigger.OnFlareSuccess(flareEvent);
                hops++;
            }
            catch (Exception exception)
            {
                LogOnce("heli-triggerfail:" + (trigger.gameObject != null ? trigger.gameObject.name : "unknown"),
                    $"[FlareExfil] heli: TriggerFlareSuccess on '{trigger.gameObject?.name}' rejected the replayed " +
                    $"flare ({exception.Message})");
            }
        }

        // The exit itself is opened by the grant, not by the animation: a map whose handler chain does not
        // include an exfiltration handler must still let the covered players leave.
        OpenPointForVisual(pointName);
    }

    /// <summary>
    /// Opens the named exit when it still waits for its manual activation. The grant has already decided that
    /// this player may leave; without this the animation would play on a machine whose exit stayed shut.
    /// </summary>
    private static void OpenPointForVisual(string pointName)
    {
        if (string.IsNullOrEmpty(pointName))
        {
            return;
        }

        var point = FikaExfilManager.FindExfiltrationPoint(pointName);
        if (point == null)
        {
            return;
        }

        try
        {
            if (point.Status == EExfiltrationStatus.AwaitsManualActivation)
            {
                point.ExternalSetStatus(EExfiltrationStatus.RegularMode, NativeSignature);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[FlareExfil] heli: point='{pointName}' could not be opened for the visual ({exception.Message})");
        }
    }

    /// <summary>The key a visual is deduplicated on: point, shooter and the spot the flare burned at.</summary>
    private static string VisualIdOf(string pointName, string profileId, Vector3 position)
    {
        return $"{pointName}|{profileId}|{position.x:F1}|{position.y:F1}|{position.z:F1}";
    }

    /// <summary>The local status of a point, for the one-line evidence of a replay.</summary>
    private static string DescribeLocalStatus(string pointName)
    {
        try
        {
            var point = FikaExfilManager.FindExfiltrationPoint(pointName);
            return point == null ? "point-unknown" : point.Status.ToString();
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    /// <summary>
    /// True when this scene carries the flare-to-exit chain at all: at least one
    /// <c>EFT.GameTriggers.TriggerFlareSuccess</c> component. On such a map the flare event itself is the
    /// evidence for the exit - the map has no detector zone to ask.
    /// </summary>
    private static int CountSceneFlareTriggers()
    {
        try
        {
            var triggers = UnityEngine.Object.FindObjectsOfType<TriggerFlareSuccess>(true);
            return triggers == null ? 0 : triggers.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// The exit a scene-trigger flare belongs to: the awaiting-manual-activation exit closest to the shot
    /// position, or an exit a pass already classified as flare gated. Every other exit is refused, so a flare
    /// can never open a plain exit on a map that happens to have a TriggerFlareSuccess somewhere.
    /// </summary>
    private static bool TryResolveSceneTriggerPoint(Vector3 shotPosition, out string pointName, out string evidence)
    {
        pointName = null;
        evidence = null;

        var controller = ExfiltrationController.Instance;
        if (controller?.ExfiltrationPoints == null || controller.ExfiltrationPoints.Length == 0)
        {
            return false;
        }

        var best = null as ExfiltrationPoint;
        var bestDistance = float.MaxValue;
        var bestEvidence = null as string;

        for (var i = 0; i < controller.ExfiltrationPoints.Length; i++)
        {
            var point = controller.ExfiltrationPoints[i];
            if (point?.Settings == null || string.IsNullOrEmpty(point.Settings.Name))
            {
                continue;
            }

            var own = FlarePointEvidence(point);
            if (own == null)
            {
                continue;
            }

            float distance;
            try
            {
                distance = Vector3.Distance(point.transform.position, shotPosition);
            }
            catch (Exception)
            {
                continue;
            }

            if (distance <= PointRange && distance < bestDistance)
            {
                bestDistance = distance;
                best = point;
                bestEvidence = own;
            }
        }

        if (best == null)
        {
            return false;
        }

        pointName = best.Settings.Name;
        evidence = $"{bestEvidence}, {bestDistance:F0} m from the shot";
        return true;
    }

    /// <summary>
    /// Why a flare could open this exit, or null when it could not. The same verdict the zone based
    /// classification uses, so both paths accept exactly the same exits.
    /// </summary>
    private static string FlarePointEvidence(ExfiltrationPoint point)
    {
        if (point == null)
        {
            return null;
        }

        if (IsGatedPoint(point.Settings?.Name))
        {
            return "the exit was already classified as flare gated";
        }

        if (DescribeBlockingRequirement(point) != null)
        {
            return null;
        }

        if (HasManualActivationEvidence(point.Settings?.Name, point) == null)
        {
            return null;
        }

        // The scene-trigger chain of the map is the same evidence the native machinery uses here: it exists
        // only on maps that drive an exit from a flare event.
        return CountSceneFlareTriggers() > 0
            ? "the exit waits for a manual activation and this scene carries a TriggerFlareSuccess chain"
            : null;
    }

    /// <summary>
    /// One waiting replay.
    /// </summary>
    private sealed class PendingVisual
    {
        public string PointName = string.Empty;
        public Vector3 Position;

        /// <summary>The object the map's handlers read out of the flare event; null until it exists here.</summary>
        public Ammo Holder;

        public bool HolderArrived;
        public float ReadyAt;
    }

    /// <summary>True when a pending replay is waiting for its holder or its deadline. For the diagnostics.</summary>
    internal static bool HasPendingVisuals()
    {
        lock (Sync)
        {
            return PendingVisuals.Count > 0;
        }
    }

    /// <summary>How many exit-visual replays wait on this machine, for the diagnostics.</summary>
    internal static int PendingVisualCount()
    {
        lock (Sync)
        {
            return PendingVisuals.Count;
        }
    }

    /// <summary>How many scene flare triggers this map carries, for the diagnostics.</summary>
    internal static int SceneFlareTriggerCount()
    {
        return CountSceneFlareTriggers();
    }

    /// <summary>
    /// The exits this machine treats as flare gated (a pass classified them, or a flare opened them), for the
    /// diagnostics: a map whose flare exit is missing from this list has no gate at all.
    /// </summary>
    internal static List<string> GatedPointNames()
    {
        var names = new List<string>();
        lock (Sync)
        {
            foreach (var name in GatedPoints)
            {
                names.Add(name);
            }

            foreach (var name in ClassifiedGated)
            {
                if (!GatedPoints.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static int CountDestroyed(FlareShootDetectorZone[] zones)
    {
        if (zones == null)
        {
            return 0;
        }

        var destroyed = 0;
        for (var i = 0; i < zones.Length; i++)
        {
            if (zones[i] == null)
            {
                destroyed++;
            }
        }

        return destroyed;
    }

    /// <summary>
    /// The exits a pass had to skip because they are destroyed or not initialised, as
    /// <c>k/n: names</c>, or null when nothing was skipped. Without it an empty classification is
    /// indistinguishable from a map that has no flare exit at all.
    /// </summary>
    private static string DescribeSkippedPoints(ExfiltrationPoint[] points)
    {
        if (points == null)
        {
            return "the exit list itself is null";
        }

        var skipped = new List<string>();
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point == null)
            {
                skipped.Add($"[{i}]=destroyed");
                continue;
            }

            try
            {
                if (point.Settings == null)
                {
                    skipped.Add($"[{i}]='{point.name}'(no settings)");
                }
            }
            catch (Exception)
            {
                skipped.Add($"[{i}]=unreadable");
            }
        }

        return skipped.Count == 0 ? null : $"{skipped.Count}/{points.Length}: {string.Join(", ", skipped)}";
    }

    /// <summary>One line per key: the callers run per flare, per packet and per frame.</summary>
    private static void LogOnce(string key, string line, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        // A new world clears the table, so each raid gets its own set of lines.
        SyncRaidState();

        lock (Sync)
        {
            if (!LoggedLines.Add(key))
            {
                return;
            }
        }

        // The caller name is passed on, so the line keeps naming the method that produced it
        // instead of this deduplication helper.
        FikaGlobals.LogWarning(line, caller);
    }

    /// <summary>
    /// One banner this class displayed, with everything the take-down needs: the notification object
    /// itself (hiding it can neither repeat nor reach another popup) and the line it reports.
    /// </summary>
    private sealed class FeedbackPopup
    {
        /// <summary>
        /// The exact object that was displayed. Typed as the concrete class on purpose: <c>Hide()</c>
        /// is declared by <c>FireFlareForExitNotification</c>, the base of this type, and not by the
        /// abstract <c>Notification</c> - which is also why the entry is per popup rather than per key.
        /// </summary>
        internal SniperFlareZoneDisabledNotification Notification;
        internal string Key;
        internal string ZoneId;
        internal string ProfileId;
        internal float Seconds;
        internal float HideAt;
    }

    private readonly struct GrantRecord(string pointName, string profileId, string squadId, string zoneId, Vector3 flarePosition)
    {
        public readonly string PointName = pointName;
        public readonly string ProfileId = profileId;
        public readonly string SquadId = squadId;
        public readonly string ZoneId = zoneId;

        /// <summary>Where the flare burned. Carried to the peers with the grant so their own exit chain can
        /// be started at the same spot; <see cref="Vector3.zero"/> when the grant carries no visual.</summary>
        public readonly Vector3 FlarePosition = flarePosition;
    }

    /// <summary>
    /// What a green flare detector zone feeds. Vanilla has two shapes: a zone that opens an exit
    /// ("exit-gate") and a zone that puts the firing players into the native sniper immunity set
    /// ("immunity"). Anything else is an airdrop, quest or sniper fire zone and is left alone.
    /// </summary>
    internal enum ZoneKind
    {
        Other,
        ExitGate,
        Immunity
    }

    /// <summary>Everything the zone table reports about one detector zone of this raid.</summary>
    private sealed class ZoneInfo
    {
        public FlareShootDetectorZone Zone;
        public string ZoneId;
        public bool ExitZone;
        public string FlareType;
        public string Collider;
        public string NearestName;
        public float NearestDistance;
        public string InsideName;
        public string AssociatedName;
    }
}