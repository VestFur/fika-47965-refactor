using Comfort.Common;
using EFT;
using EFT.GlobalEvents;
using EFT.Interactive;
using JsonType;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.World;
using System.Collections.Generic;
using static Fika.Core.Networking.NetworkUtils;

namespace Fika.Core.Networking;

public sealed partial class FikaServer
{
    private GameWorld _btrSpawnWorld;
    private uint _btrSpawnGeneration;
    private int _btrSpawnPlayerRaidId;
    private Vector3 _btrSpawnPosition;
    private Quaternion _btrSpawnRotation;

    internal void BroadcastBtrSpawn(BtrSpawnOnThePathEvent spawn)
    {
        var world = Singleton<GameWorld>.Instance;
        if (world == null) return;
        if (_btrSpawnWorld == null || _btrSpawnWorld.Pointer != world.Pointer) _btrSpawnGeneration = 0;
        _btrSpawnWorld = world;
        _btrSpawnGeneration++;
        _btrSpawnPlayerRaidId = spawn.PlayerRaidId;
        _btrSpawnPosition = spawn.Position;
        _btrSpawnRotation = spawn.Rotation;
        SendGenericPacket(EGenericSubPacketType.SpawnBTR,
            BtrSpawn.FromValue(spawn.Position, spawn.Rotation, spawn.PlayerRaidId, _btrSpawnGeneration), true);
    }

    /// <summary>
    /// Hands a peer that joined a running raid the world state it cannot have seen: transit history and
    /// points, the event generators, Kolotun and the BTR. Sent when a peer reports its transit
    /// controllers, i.e. once per late join.
    /// </summary>
    private void SendMapStateSnapshot(Player player, NetPeer peer)
    {
        var world = Singleton<GameWorld>.Instance;
        // The optional Headless plugin supplies its own TransitController
        // subclass. Read the shared native state so these snapshots work
        // for both the playable host and Headless host.
        var transit = world?.TransitController;
        if (transit != null)
        {
            if (transit.summonedTransits.TryGetValue(player.ProfileId, out var history))
            {
                var historyPacket = new SyncTransitControllersPacket
                {
                    ProfileId = player.ProfileId, RaidId = history.raidId,
                    Count = history.count, Maps = history.maps, Events = history.events
                };
                SendDataToPeer(ref historyPacket, DeliveryMethod.ReliableOrdered, peer);
            }
            // CreateInitialization (the Fika transit controller) needs a host side history
            // entry for that profile. The native fallback below builds the snapshot without
            // one, so a peer that connects while the raid is already running is served either
            // way and never stays without transit points.
            var init = FikaWorldControllers.Find<FikaHostTransitController>(transit)?.CreateInitialization(player)
                ?? CreateNativeTransitInitialization(transit, player,
                    FikaSingleton<Main.GameMode.IFikaGame>.Instance?.GameController?.RaidSettings?.SelectedDateTime ?? default);
            if (init != null)
            {
                SendDataToPeer(ref init, DeliveryMethod.ReliableOrdered, peer);
            }
            else
            {
                _logger.LogWarning($"[Transit] No initialization snapshot could be built for {player?.ProfileId}; the peer keeps its locally created transit state");
            }
            foreach (var point in transit.pointsById.Values)
            {
                if (!transit.transitPlayers.ContainsKey(player.ProfileId) ||
                    !point._playerGroups.TryGetValue(player.ProfileId, out var group)) continue;
                point.SendTimer(player.ProfileId, group);
                if (point._groupSize.TryGetValue(group, out var size))
                {
                    var sizes = new Il2CppSystem.Collections.Generic.Dictionary<int, byte>();
                    sizes.Add(player.RaidId, (byte)System.Math.Clamp(size, 0, byte.MaxValue));
                    var sizePacket = new TransitEventPacket
                    {
                        EventType = TransitEventPacket.ETransitEventType.GroupSize,
                        TransitEvent = new TransitGroupSizeEvent { PointId = point.parameters.id, Sizes = sizes }
                    };
                    SendDataToPeer(ref sizePacket, DeliveryMethod.ReliableOrdered, peer);
                }
            }
        }
        if (FikaWorldControllers.Find<HostRunddansController>(world.RunddansController) is { } events)
        {
            var state = events.CreateSnapshot();
            SendDataToPeer(ref state, DeliveryMethod.ReliableOrdered, peer);
            // Generator state alone does not say whether the event has already
            // started: a running generator can subsequently break or finish.
            // Re-send the transit lock after the state snapshot to late peers.
            if (events.EventStarted)
            {
                var started = new EventControllerEventPacket
                {
                    Type = EventControllerEventPacket.EEventType.StartedEvent
                };
                SendDataToPeer(ref started, DeliveryMethod.ReliableOrdered, peer);
            }
        }
        if (world.KolotunController is { } kolotun)
        {
            var state = new KolotunStatePacket
            {
                IsActive = kolotun.IsActive,
                EventStage = kolotun.EventStage
            };
            SendDataToPeer(ref state, DeliveryMethod.ReliableOrdered, peer);
        }
        if (_btrSpawnGeneration > 0 && _btrSpawnWorld != null && _btrSpawnWorld.Pointer == world.Pointer && world.BtrController is { } btr)
        {
            var view = btr.BtrView;
            var position = view != null ? view.transform.position : _btrSpawnPosition;
            var rotation = view != null ? view.transform.rotation : _btrSpawnRotation;
            SendGenericPacketToPeer(EGenericSubPacketType.SpawnBTR,
                BtrSpawn.FromValue(position, rotation, _btrSpawnPlayerRaidId, _btrSpawnGeneration), peer);
            _dataWriter.Reset();
            _dataWriter.PutEnum(EPacketType.BTR);
            _dataWriter.PutUnmanaged(btr._offlineSyncPacket);
            peer.Send(_dataWriter.AsReadOnlySpan(), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// Builds the transit initialization snapshot for a player whose state this server can
    /// describe. It deliberately does not require a host side transit history: a peer reports
    /// one through BaseGameController.SyncTransitControllers, and that report only runs from
    /// countdown statement 6/8, which the cooperative settings can skip. Without the history
    /// fallback a peer that connected mid raid received no initialization at all and its
    /// transit points stayed unusable.
    /// </summary>
    private TransitEventPacket CreateNativeTransitInitialization(TransitController transit, Player player,
        EDateTime fallbackTime)
    {
        if (player == null || transit == null)
        {
            return null;
        }

        ushort transitionCount;
        bool eventPlayer;
        if (transit.summonedTransits.TryGetValue(player.ProfileId, out var history))
        {
            transitionCount = (ushort)System.Math.Clamp(history.count, 0, ushort.MaxValue);
            eventPlayer = history.events;
        }
        else
        {
            // The peer keeps the count it built from the raid settings it received, so report
            // the same raid wide transition state (the object the local raid settings point
            // at) instead of a zeroed history, which would take its transit points away.
            var transitData = FikaBackendUtils.TransitData;
            transitionCount = (ushort)System.Math.Clamp(transitData?.transitionCount ?? 0, 0, ushort.MaxValue);
            eventPlayer = transitData != null && transitData.transitionType.HasFlagNoBox(ELocationTransition.Event);
            _logger.LogWarning($"[Transit] No host side transit history for {player.ProfileId}; sending an initialization snapshot from the raid transition state (count={transitionCount}, event={eventPlayer})");
        }

        var points = new Il2CppSystem.Collections.Generic.Dictionary<int, JsonType.LocationSettings.Location.TransitParameters>();
        var requirements = new Il2CppSystem.Collections.Generic.Dictionary<int, bool>();
        var enabled = new List<int>();
        var registered = -1;
        foreach (var entry in transit.pointsById)
        {
            var point = entry.Value;
            if (point.parameters.active) points.Add(entry.Key, point.parameters);
            // This is the initializer the clients actually read (logged as [Init] Host
            // transit Init). Reporting the raw requirement disabled the point in every
            // client UI, exactly like the other initializer did, so it is reported as
            // met here too; per-player validation stays with the client that shows the
            // interaction prompt and with HasMetEntryRequirements on the host.
            requirements.Add(entry.Key, true);
            if (point.Enabled) enabled.Add(entry.Key);
            if (transit.transitPlayers.ContainsKey(player.ProfileId) &&
                point._playerGroups.ContainsKey(player.ProfileId)) registered = entry.Key;
        }
        transit.profileKeys.TryGetValue(player.ProfileId, out var key);
        var time = transit.profileTime.TryGetValue(player.ProfileId, out var selected)
            ? selected : fallbackTime;
        return new TransitEventPacket
        {
            EventType = TransitEventPacket.ETransitEventType.Init,
            TransitEvent = new TransitInitEvent
            {
                PlayerRaidId = player.RaidId, Points = points,
                CompletedQuestRequirementMetByPointId = requirements,
                TransitionCount = transitionCount, EventPlayer = eventPlayer
            },
            EnabledPointIds = enabled.ToArray(), RegisteredPointId = registered,
            KeyId = key, SelectedTime = time
        };
    }
}
