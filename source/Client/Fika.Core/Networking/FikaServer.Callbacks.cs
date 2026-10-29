using Fika.Core.Main.BaseClasses;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Communications;
using EFT.Interactive;
using EFT.InventoryLogic;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.Factories;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Communication;
#if DEBUG
using Fika.Core.Networking.Packets.Debug;
using EFT.UI;
using static Fika.Core.Networking.Packets.Debug.CommandPacket;
using Fika.Core.ConsoleCommands;
#endif
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.Player;
using Fika.Core.Networking.Packets.Player.Common;
using Fika.Core.Networking.Packets.World;
using HarmonyLib;

// BJ (analysis/terminal-rauf-card): the sentry access line of the terminal, host side - see
// FikaSentryAccess and the patches of SentryTerminalAccessPatches.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using EFT.NetworkPackets;
using Diz.Jobs;
using static Fika.Core.Networking.NetworkUtils;

namespace Fika.Core.Networking;

public sealed partial class FikaServer
{
    private void SpawnItemInInventoryPacketReceived(SpawnItemInInventoryPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (!CoopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            _logger.LogError($"Could not find player with id [{packet.NetId}] when trying to spawn item in inventoy");
            return;
        }

        var item = Singleton<ItemFactory>.Instance.CreateItem(packet.ItemId, packet.TemplateId, null);
        if (item == null)
        {
            _logger.LogError($"Failed to create item [{packet.ItemId}] with templateId [{LogSafe.Ref(packet.TemplateId)}]");
            return;
        }

        if (packet.Amount > 1 && item.StackMaxSize > 1)
        {
            item.StackObjectsCount = Mathf.Clamp(packet.Amount, 1, item.StackMaxSize);
        }
        else
        {
            item.StackObjectsCount = 1;
        }

        var tempMove = TemporaryStash.Grid.AddAnywhere(item, EErrorHandlingType.Ignore);
        if (tempMove.Failed)
        {
            _logger.LogError($"Failed to move item to temporary stash: {tempMove.Error}");
            return;
        }

        new ItemController(TemporaryStash, player.InventoryController.ID, "temp item stash", false, EOwnerType.Profile);

        var inventoryController = player.InventoryController;
        var result = ItemManipulator.Move(item, packet.ItemAddress, inventoryController);
        if (result.Failed)
        {
            _logger.LogError($"Failed to move spawned item: {result.Error}");
        }

        var resources = new Il2CppSystem.Collections.Generic.List<ResourceKey>();
        foreach (var resource in item.Template.AllResources) resources.Add(resource);
        Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Online,
            resources, JobYieldPriority.Immediate, null, new Il2CppSystem.Threading.CancellationToken())
            .HandleExceptions();
    }

    private void OnQuestSyncPacketReceived(QuestSyncPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (!CoopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            _logger.LogError($"Could not find player with id [{packet.NetId}] when trying to sync quest packet");
            return;
        }

        if (player.FikaQuests is ObservedQuestController observedQuestController)
        {
            observedQuestController.UpdateQuestStatusForClient(packet);
            return;
        }

        _logger.LogError($"QuestController on player [{player.Profile.GetCorrectedNickname()}] was not of type observed, was [{player.QuestController.GetType().Name}]");
    }

    private void OnKnifeHitPacketReceived(KnifeHitPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null)
        {
            FikaGlobals.LogError("GameWorld was null when receiving KnifeHitPacket");
            return;
        }

        if (gameWorld.TryCast<FikaHostGameWorld>() is not { } hostGameWorld)
        {
            FikaGlobals.LogError($"GameWorld not a FikaHostGameWorld, was: {gameWorld.GetType().Name}");
            return;
        }

        switch (packet.HitType)
        {
            case EHitType.Window:
                {
                    if (hostGameWorld.Windows == null || !hostGameWorld.Windows.TryGetByKey(packet.HitId, out var window))
                    {
                        FikaGlobals.LogError($"Could not find window with NetId {packet.HitId}");
                        return;
                    }

                    var damageInfo = new DamageInfo
                    {
                        HitPoint = packet.HitPoint
                    };
                    window.MakeHit(ref damageInfo);
                }
                break;
            case EHitType.Btr:
                {
                    if (!CoopHandler.Players.TryGetValue(packet.NetId, out var player))
                    {
                        FikaGlobals.LogError($"Could not find player with NetId {packet.NetId}");
                        return;
                    }

                    if (hostGameWorld.BtrController != null)
                    {
                        hostGameWorld.BtrController.HitFromPlayer(player);
                    }
                }
                break;
            case EHitType.Default:
            case EHitType.Lamp:
                {
                    if (hostGameWorld.TurnablesDict == null || !hostGameWorld.TurnablesDict.TryGetValue(packet.HitId, out var turnable))
                    {
                        FikaGlobals.LogError($"Could not find turnable with NetId {packet.HitId}");
                        return;
                    }

                    turnable.OnHit(new DamageInfo
                    {
                        HitPoint = packet.HitPoint
                    });
                }
                break;
            case EHitType.Tripwire:
            case EHitType.Event:
                break;
        }
    }

    private void OnProceedRequestPacketReceived(ProceedRequestPacket packet, NetPeer peer)
    {
        var response = new ProceedResponsePacket
        {
            CallbackId = packet.CallbackId
        };

        if (peer.Player == null || packet.NetId != peer.Player.NetId)
        {
            response.Error = $"Proceed request for unbound player {packet.NetId}";
            SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
            return;
        }

        if (!CoopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            response.Error = $"Could not find player with id {packet.NetId}";
            SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
            return;
        }

        if (packet.ProceedType is EProceedType.EmptyHands) // always accept empty hands
        {
            SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
            return;
        }

        if (!TryFindItemForProceedPacket(packet.ItemId, out var item))
        {
            response.Error = $"Could not find item with id {packet.ItemId}";
            SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
            return;
        }

        if (item.CurrentAddress != null)
        {
            var result = item.CheckAction(null);
            if (result.Failed)
            {
                response.Error = $"Player cannot equip item with id {packet.ItemId}: {result.Error}";
                SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
                return;
            }
        }

        SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
    }

    /// <summary>
    /// Attempts to find the item with the given id
    /// </summary>
    /// <param name="itemId">The id to search for</param>
    /// <param name="item">The item if found</param>
    /// <returns><see langword="true"/> if found; otherwise <see langword="false"/></returns>
    private bool TryFindItemForProceedPacket(MongoID itemId, out Item item)
    {
        var gameWorld = Singleton<GameWorld>.Instance;

        var search = gameWorld.FindItemWithWorldData(itemId);
        if (search.Succeeded)
        {
            item = search.Value.Item1;
            return true;
        }

        var stationary = gameWorld.FindStationaryWeaponByItemId(itemId);
        if (stationary != null)
        {
            item = stationary.Item;
            return true;
        }

        item = null;
        return false;
    }

    private void OnClearSnapshotterPacketReceived(ClearSnapshotterPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var player) && player.TryCast<ObservedPlayer>() is { } observedPlayer)
        {
            observedPlayer.HealthBar.RemoveAllActiveEffects();
            observedPlayer.Snapshotter.Clear();
            return;
        }

        _logger.LogError($"Could not find player {packet.NetId} to reset snapshotter");
    }

    private void OnLoadingScreenPlayersPacketReceived(LoadingScreenPlayersPacket packet, NetPeer peer)
    {
        if (!_assignedPeerNetIds.TryGetValue(peer, out var assignedNetId) || packet.NetIds?.Length != 1 ||
            packet.NetIds[0] != assignedNetId || packet.Nicknames?.Length != 1 || LoadingScreenUI.Instance == null)
        {
            // (analysis/loading-screen-peer-state) A dropped row announcement is the fingerprints of a peer
            // whose loading state can never appear: name it instead of returning silently.
            LoadingScreenUI.DiagThrottled("players-rejected-" + peer.Id, 5f,
                "row announcement from peer " + peer.Id + " did not pass the check and was dropped: netIds="
                + (packet.NetIds?.Length.ToString() ?? "null") + ", first="
                + (packet.NetIds is { Length: > 0 } ? packet.NetIds[0].ToString() : "n/a")
                + ", assigned=" + (_assignedPeerNetIds.TryGetValue(peer, out var known) ? known.ToString() : "none")
                + ", nicknames=" + (packet.Nicknames?.Length.ToString() ?? "null")
                + ", screen=" + (LoadingScreenUI.Instance == null ? "absent" : "present")
                + " => this peer's row stays unknown on this machine");
            return;
        }

        var hadRow = LoadingScreenUI.Instance.EnsureRow(assignedNetId, packet.Nicknames[0]);
        if (hadRow)
        {
            // The row was re-created by this announcement: the peer announced itself again after this
            // machine's own re-arm had dropped its row (the round10 gap) - or it is a normal late join.
            LoadingScreenUI.Diag("row for peer " + peer.Id + " (netId=" + assignedNetId + ", '"
                + packet.Nicknames[0] + "') did not exist on this screen and was created from the peer's "
                + "announcement (" + LoadingScreenUI.Instance.DescribeRows() + ")");
        }

        var newPacket = LoadingScreenUI.Instance.GetPlayersPacket();
        SendData(ref newPacket, DeliveryMethod.ReliableUnordered);
    }

    private void OnLoadingScreenPacketReceived(LoadingScreenPacket packet, NetPeer peer)
    {
        if (!_assignedPeerNetIds.TryGetValue(peer, out var assignedNetId) || packet.NetId != assignedNetId)
        {
            // (analysis/loading-screen-peer-state) Progress of a peer that this machine cannot match to an
            // assigned net id would otherwise vanish without a line.
            LoadingScreenUI.DiagThrottled("progress-rejected-" + peer.Id, 5f,
                "progress from peer " + peer.Id + " was dropped: packetNetId=" + packet.NetId + ", assigned="
                + (_assignedPeerNetIds.TryGetValue(peer, out var known) ? known.ToString() : "none")
                + " => the peer's loading state cannot be shown on this machine");
            return;
        }

        if (LoadingScreenUI.Instance != null)
        {
            // (analysis/loading-screen-peer-state) ApplyPeerProgress creates the row when this machine's
            // re-arm dropped it; the row is what makes the peer's loading state visible here at all.
            LoadingScreenUI.Instance.ApplyPeerProgress(packet.NetId, packet.Progress, "peer#" + peer.Id);
        }

        SendData(ref packet, DeliveryMethod.Unreliable, peer);
    }

#if DEBUG
    private void OnCommandPacketReceived(CommandPacket packet, NetPeer peer)
    {
        switch (packet.CommandType)
        {
            case ECommandType.SpawnAI:
                FikaCommands.SpawnNPC(packet.SpawnType, packet.SpawnAmount);
                break;
            case ECommandType.DespawnAI:
                FikaCommands.DespawnAllAI();
                break;
            case ECommandType.Bring:
                FikaCommands.BringReplicated(packet.NetId);
                break;
            case ECommandType.SpawnAirdrop:
                FikaCommands.SpawnAirdrop();
                break;
            default:
                break;
        }
    }

    private void OnSpawnItemPacketReceived(SpawnItemPacket packet, NetPeer peer)
    {
        SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);

        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            FikaGlobals.SpawnItemInWorld(packet.Item, playerToApply);
        }
    }
#endif

    private void OnEventControllerInteractPacketReceived(EventControllerInteractPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        var gameWorld = Singleton<GameWorld>.Instance;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            if (FikaWorldControllers.Find<HostRunddansController>(gameWorld.RunddansController) is { } controller)
            {
                controller.ObservedInteractWithEventObject(player, packet.Data, peer);
                return;
            }

            _logger.LogError("Could not find the RunddansController when player interacted");
        }
    }

    private void OnInraidQuestPacketReceived(InRaidQuestPacket packet, NetPeer peer)
    {
        if (peer.Player == null || peer.Player.NetId != packet.NetId)
        {
            _logger.LogWarning($"Rejected quest inventory update for net id {packet.NetId} from an unbound peer");
            return;
        }

        if (_coopHandler.Players.TryGetValue(packet.NetId, out var player))
        {
            if (player.FikaQuests is ObservedQuestController controller)
            {
                // Resource loading can suspend. Keep the decoded packet value
                // and relay only after the host observer has applied it.
                var relay = packet;
                MainThreadDispatcher.RunAsync(async () =>
                {
                    try
                    {
                        await controller.HandleInraidQuestPacketAsync(relay);
                        // The sender can disconnect while reward bundles load.
                        // The host has accepted the update, so other clients
                        // must still receive it after the observer applies it.
                        SendData(ref relay, DeliveryMethod.ReliableOrdered, peer);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError($"Quest inventory synchronization failed for {relay.NetId}: {exception}");
                    }
                }).Forget();
            }
        }
    }

    private void OnNetworkSettingsPacketReceived(NetworkSettingsPacket packet, NetPeer peer)
    {
#if DEBUG
        _logger.LogInfo($"Received connection from {packet.ProfileId}");
#endif
        if (string.IsNullOrEmpty(packet.ProfileId)) return;
        var scavLateJoin = _scavLateJoinPeers.Contains(peer);
        if (_assignedPeerProfiles.TryGetValue(peer, out var assignedProfile) && assignedProfile != packet.ProfileId)
        {
            if (!scavLateJoin)
            {
                _logger.LogWarning($"Rejected profile change on peer {peer.Id}");
                return;
            }

            // SPT regenerated the scav profile for this late joiner.
            _logger.LogInfo($"Rebinding peer {peer.Id} from {assignedProfile} to {packet.ProfileId} (player scav late join)");
            _assignedPeerProfiles[peer] = packet.ProfileId;
        }
        if (_assignedPeerProfiles.Any(entry => entry.Key != peer && entry.Value == packet.ProfileId &&
            entry.Key.ConnectionState == ConnectionState.Connected))
        {
            _logger.LogWarning($"Rejected simultaneous connection for profile {packet.ProfileId}");
            return;
        }
        if (!_cachedConnections.TryGetValue(packet.ProfileId, out var netId))
        {
            netId = PopNetId();
            _cachedConnections.Add(packet.ProfileId, netId);
        }

        _assignedPeerNetIds[peer] = netId;
        _assignedPeerProfiles[peer] = packet.ProfileId;

        NetworkSettingsPacket response = new()
        {
            SendRate = _sendRate,
            NetId = netId,
            AllowVOIP = AllowVOIP,
            StrictSync = StrictInventorySync
        };
        SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnWorldPacketReceived(WorldPacket packet, NetPeer peer)
    {
        if (peer.Player == null) return;
        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null)
        {
            _logger.LogError("OnNewWorldPacketReceived: GameWorld was null!");
            return;
        }

        FikaHostWorld.LootSyncPackets.AddRange(packet.LootSyncStructs);

        packet.Flush();
    }

    private void OnRequestPacketReceived(RequestPacket packet, NetPeer peer)
    {
        if (packet.RequestSubPacket == null)
        {
            _logger.LogError("OnRequestPacketReceived: RequestSubPacket was null!");
            return;
        }

        packet.RequestSubPacket.HandleRequest(peer, this);
    }

    private void OnSideEffectPacketReceived(SideEffectPacket packet, NetPeer peer)
    {
#if DEBUG
        _logger.LogWarning("OnSideEffectPacketReceived: Received");
#endif

        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld == null)
        {
            _logger.LogError("OnSideEffectPacketReceived: GameWorld was null!");
            return;
        }

        var gstruct2 = gameWorld.FindItemById(packet.ItemId);
        if (gstruct2.Failed)
        {
            _logger.LogError("OnSideEffectPacketReceived: " + gstruct2.Error);
            return;
        }
        var item = gstruct2.Value;
        if (item.TryGetItemComponent(out SideEffectComponent sideEffectComponent))
        {
#if DEBUG
            _logger.LogInfo("Setting value to: " + packet.Value + ", original: " + sideEffectComponent.Value);
#endif
            sideEffectComponent.Value = packet.Value;
            item.RaiseRefreshEvent(false, false);
            return;
        }
        _logger.LogError("OnSideEffectPacketReceived: SideEffectComponent was not found!");
    }

    private void OnLoadingProfilePacketReceived(LoadingProfilePacket packet, NetPeer peer)
    {
        if (packet.Profiles == null || packet.Profiles.Count != 1)
        {
            _logger.LogError("OnLoadingProfilePacketReceived: Expected one client profile");
            return;
        }

        (var profile, var isLeader) = packet.Profiles.First();
        if (!_assignedPeerProfiles.TryGetValue(peer, out var assignedProfile) || profile.ProfileId != assignedProfile)
        {
            if (!_scavLateJoinPeers.Contains(peer))
            {
                _logger.LogWarning($"Rejected loading profile for peer {peer.Id}: profile did not match settings");
                return;
            }

            // Player scav late join: SPT regenerated the profile for this raid.
            _logger.LogInfo($"Accepting loading profile {profile.ProfileId} from player scav peer {peer.Id}");
            _assignedPeerProfiles[peer] = profile.ProfileId;
        }
        if (!_visualProfiles.Any(x => x.Key.ProfileId == profile.ProfileId))
        {
            _visualProfiles.Add(profile, _visualProfiles.Count == 0 || isLeader);
        }
        FikaBackendUtils.AddPartyMembers(_visualProfiles);
        packet.Profiles = _visualProfiles;
        SendData(ref packet, DeliveryMethod.ReliableOrdered);

        var clientConnected = ClientConnected.FromValue(profile.Info.MainProfileNickname);
        if (!FikaBackendUtils.IsHeadless)
        {
            clientConnected.Execute();
        }

        SendGenericPacket(EGenericSubPacketType.ClientConnected,
            clientConnected, true);

        peer.Tag = profile.Info.MainProfileNickname;
    }

    private void OnPingPacketReceived(PingPacket packet, NetPeer peer)
    {
        if (FikaPlugin.Instance.Settings.UsePingSystem.Value && !FikaBackendUtils.IsHeadless)
        {
            PingFactory.ReceivePing(packet.PingLocation, packet.PingType, packet.PingColor, packet.Nickname, packet.LocaleId);
        }
    }

    private void OnBotStatePacketReceived(BotStatePacket packet, NetPeer peer)
    {
        // A client acknowledges loading host-created bots; it cannot own
        // their enable, disable, or disposal transitions.
        if (packet.Type != BotStatePacket.EStateType.LoadBot) return;
        var host = FikaSingleton<IFikaGame>.Instance?.GameController as HostGameController;
        host?.IncreaseLoadedPlayers(packet.NetId, peer);
    }

    private void OnTransitInteractPacketReceived(TransitInteractPacket packet, NetPeer peer)
    {
        // (analysis/client-transit-terminal) This drop used to be silent. When a client's press produced no request
        // on its own machine, the fix re-sends that request, and a mismatch here is the one place it can still
        // vanish - the line below is the pair of the client's 'press fallback' line, so "the gate refused" and
        // "the request never arrived" can be told apart in one grep.
        if (peer.Player == null || packet.NetId != peer.Player.NetId)
        {
            return;
        }
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            var transitController = Singleton<GameWorld>.Instance?.TransitController;
            if (transitController != null)
            {
                transitController.InteractWithTransit(playerToApply, packet.Data);
            }
        }
    }

    private void OnSyncTransitControllersPacketReceived(SyncTransitControllersPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.ProfileId != peer.Player.ProfileId) return;
        var transitController = Singleton<GameWorld>.Instance?.TransitController;
        if (transitController != null)
        {
            if (!transitController.summonedTransits.ContainsKey(packet.ProfileId))
                transitController.summonedTransits[packet.ProfileId] = new(packet.RaidId, packet.Count, packet.Maps, packet.Events);
            if (FikaSingleton<IFikaGame>.Instance?.GameController?.GameInstance?.Status == GameStatus.Started)
            {
                var player = _coopHandler.HumanPlayers.FirstOrDefault(x => x.ProfileId == packet.ProfileId);
                if (player != null) SendMapStateSnapshot(player, peer);
            }
            return;
        }

        _logger.LogError("OnSyncTransitControllersPacketReceived: TransitController was null!");
    }

    private void OnBTRInteractionPacketReceived(BTRInteractionPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId || packet.IsResponse)
        {
            return;
        }

        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld.BtrController != null && gameWorld.BtrController.BtrVehicle != null)
            {
                var status = gameWorld.BtrController.BtrVehicle.InteractInternal(playerToApply, packet.Data);
                BTRInteractionPacket response = new(packet.NetId)
                {
                    IsResponse = true,
                    Status = status,
                    Data = packet.Data
                };

                SendData(ref response, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void OnResyncInventoryIdPacketReceived(ResyncInventoryIdPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            if (playerToApply.TryCast<ObservedPlayer>() is { } observedPlayer)
            {
                if (observedPlayer.InventoryController?.TryCast<ObservedInventoryController>() is { } observedController)
                {
                    observedController.SetNewID(packet.MongoId);
                }
            }
        }
    }


    private void OnWorldLootPacketReceived(WorldLootPacket packet, NetPeer peer)
    {
        var fikaGame = FikaSingleton<IFikaGame>.Instance;
        if (fikaGame != null)
        {
            WorldLootPacket response = new()
            {
                Data = (fikaGame.GameController as HostGameController).GetHostLootItems()
            };

            SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
        }
    }

    private void OnInteractableInitPacketReceived(InteractableInitPacket packet, NetPeer peer)
    {
        if (packet.IsRequest)
        {
            if (Singleton<GameWorld>.Instantiated)
            {
                var world = Singleton<GameWorld>.Instance.World;
                if (world.Interactables != null)
                {
                    var response = InteractableInitPacket.FromInteractables(world.Interactables);

                    SendDataToPeer(ref response, DeliveryMethod.ReliableOrdered, peer);
                }
            }
        }
    }

    private void OnQuestDropItemPacketReceived(QuestDropItemPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        packet.Nickname = peer.Player.Profile.Info.MainProfileNickname;

        if (_hostPlayer?.HealthController?.IsAlive == true)
        {
            if (_hostPlayer.FikaQuests is ClientSharedQuestController sharedQuestController)
            {
                sharedQuestController.ReceiveQuestDropItemPacket(packet);
            }
        }
        SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnQuestItemPacketReceived(QuestItemPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        packet.Nickname = peer.Player.Profile.Info.MainProfileNickname;

        if (_hostPlayer?.HealthController?.IsAlive == true)
        {
            if (_hostPlayer.FikaQuests is ClientSharedQuestController sharedQuestController)
            {
                sharedQuestController.ReceiveQuestItemPacket(packet);
            }
        }
        SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnQuestConditionPacketReceived(QuestConditionPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        packet.Nickname = peer.Player.Profile.Info.MainProfileNickname;

        if (_hostPlayer?.HealthController?.IsAlive == true)
        {
            if (_hostPlayer.FikaQuests is ClientSharedQuestController sharedQuestController)
            {
                sharedQuestController.ReceiveQuestPacket(packet);
            }
        }
        SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnTextMessagePacketReceived(TextMessagePacket packet, NetPeer peer)
    {
        if (peer.Player == null)
        {
            return;
        }

        packet.Nickname = peer.Player.Profile.Info.MainProfileNickname;
        _logger.LogInfo($"Received message from: {packet.Nickname}, Message: {packet.Message}");

        if (_fikaChat != null)
        {
            _fikaChat.AddMessage(new(packet.Nickname, packet.Message), true);
            _fikaChat.OpenChat(true);
        }

        SendData(ref packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnGenericPacketReceived(GenericPacket packet, NetPeer peer)
    {
        if (!_assignedPeerNetIds.TryGetValue(peer, out var assignedNetId) && packet.Type is EGenericSubPacketType.SendCharacter)
        {
            // (2026-10-17) A transit keeps the UDP connection, but the host can be left without the
            // per-peer assignment (the assignment table is rebuilt whenever the server object is
            // re-created, and a surviving connection never re-sends NetworkSettings on its own). The peer
            // then announces its character into a missing assignment, no player is ever created for it and
            // every later request is answered with "unbound player" - the reported state after a transit:
            // no weapon switching, no item moves, and the client standing still on the host.
            // The assignment is restored only from the net id this host itself handed out for exactly that
            // profile, never from a value the peer chooses.
            if (packet.SubPacket is SendCharacterPacket restoring
                && restoring.PlayerInfoPacket.Profile != null
                && _cachedConnections.TryGetValue(restoring.PlayerInfoPacket.Profile.ProfileId, out var cachedNetId)
                && cachedNetId == packet.NetId)
            {
                _assignedPeerNetIds[peer] = cachedNetId;
                _assignedPeerProfiles[peer] = restoring.PlayerInfoPacket.Profile.ProfileId;
                assignedNetId = cachedNetId;
                _logger.LogWarning($"Restored the net id {cachedNetId} of peer {peer.Id} from its character " +
                    $"announcement (profile {restoring.PlayerInfoPacket.Profile.ProfileId})");
            }
        }
        if (!_assignedPeerNetIds.TryGetValue(peer, out assignedNetId) || packet.NetId != assignedNetId ||
            IsHostOnlyGenericPacket(packet.Type))
        {
            if (packet.Type is EGenericSubPacketType.SendCharacter)
            {
                _logger.LogWarning($"Dropped a character announcement from peer {peer.Id}: " +
                    $"assignedNetId={(assignedNetId == 0 ? "none" : assignedNetId.ToString())}, " +
                    $"packetNetId={packet.NetId}, hostOnly={IsHostOnlyGenericPacket(packet.Type)}");
            }

            return;
        }
        if (packet.Type is EGenericSubPacketType.SendCharacter)
        {
            // The first character announcement precedes the final information
            // packet that binds peer.Player. Validate against the settings
            // assignment instead of requiring that later binding.
            if (packet.SubPacket is SendCharacterPacket logged)
            {
                _logger.LogInfo($"Character announcement from peer {peer.Id}: netId={logged.NetId} " +
                    $"(assigned {assignedNetId}), isAI={logged.IsAI}, " +
                    $"profile={logged.PlayerInfoPacket.Profile?.ProfileId}, scavPeer={_scavLateJoinPeers.Contains(peer)}");
            }

            if (packet.SubPacket is not SendCharacterPacket character)
            {
                _logger.LogWarning($"Character announcement from peer {peer.Id} carried " +
                    $"{packet.SubPacket?.GetType().Name ?? "no sub packet"} instead of a character packet");
                return;
            }

            if (character.IsAI || character.NetId != assignedNetId || character.PlayerInfoPacket.Profile == null)
            {
                // Without this the peer silently never gets a player, and every one
                // of its inventory operations is rejected afterwards.
                _logger.LogWarning($"Rejected the character announcement from peer {peer.Id}: " +
                    $"isAI={character.IsAI}, netId={character.NetId} (assigned {assignedNetId}), " +
                    $"profile={(character.PlayerInfoPacket.Profile == null ? "null" : "present")}");
                return;
            }

            if (!_assignedPeerProfiles.TryGetValue(peer, out var assignedProfile) ||
                character.PlayerInfoPacket.Profile.ProfileId != assignedProfile)
            {
                _logger.LogInfo($"Peer {peer.Id} announced profile " +
                    $"{character.PlayerInfoPacket.Profile.ProfileId} while the server assigned " +
                    $"{(_assignedPeerProfiles.TryGetValue(peer, out var current) ? current : "none")}");
                // A player scav that joined a running raid announces the profile
                // SPT regenerated for this raid; accept and rebind it, otherwise
                // the host never creates a player for it.
                if (!_scavLateJoinPeers.Contains(peer)) return;
                _logger.LogInfo($"Accepting regenerated scav profile " +
                    $"{character.PlayerInfoPacket.Profile.ProfileId} from peer {peer.Id}");
                _assignedPeerProfiles[peer] = character.PlayerInfoPacket.Profile.ProfileId;
            }
        }
        if (packet.Type is EGenericSubPacketType.MuffledState &&
            (peer.Player == null || ((MuffledState)packet.SubPacket).NetId != peer.Player.NetId)) return;
        if (packet.Type is EGenericSubPacketType.ClientExtract &&
            (peer.Player == null || ((ClientExtract)packet.SubPacket).NetId != peer.Player.NetId)) return;
        if (packet.Type is EGenericSubPacketType.DialogEntry)
        {
            OnDialogEntryPacketReceived((DialogEntryPacket)packet.SubPacket, peer);
            return;
        }
        if (packet.Type is EGenericSubPacketType.InventoryOperation)
        {
            OnInventoryPacketReceived((InventoryPacket)packet.SubPacket, peer);
            return;
        }
        packet.Execute();
        if (packet.Type is EGenericSubPacketType.SendCharacter)
        {
            // This announcement was validated above. Forward it explicitly:
            // the raw broadcast path suppresses unvalidated character data.
            // Do not clear the receive packet; NetPacketProcessor owns its pool.
            var writer = new NetDataWriter();
            writer.PutEnum(EPacketType.Serializable);
            _packetProcessor.WriteNetReusable(writer, ref packet);
            _netServer.SendToAll(writer.AsReadOnlySpan(), DeliveryMethod.ReliableOrdered, peer);
        }
    }

    private void OnInformationPacketReceived(InformationPacket packet, NetPeer peer)
    {
        // The client polls this request and can retry the ready message during
        // reconnect. Count the connection once for this raid's ready barrier.
        // A packet can arrive while the native server component is being
        // constructed (or while a reconnect is tearing down the old raid).
        // Treat the unbound state as not ready instead of dereferencing the
        // dictionaries before Init has populated them.
        if (_assignedPeerProfiles?.ContainsKey(peer) == true && packet.ReadyPlayers > 0 &&
            _readyPeers?.Add(peer) == true) ReadyClients++;

        var gameExists = _coopHandler != null && _coopHandler.LocalGameInstance != null;

        InformationPacket respondPackage = new()
        {
            RaidStarted = gameExists && FikaSingleton<IFikaGame>.Instance?.GameController?.RaidStarted == true,
            ReadyPlayers = ReadyClients,
            HostReady = HostReady,
            HostLoaded = RaidInitialized,
            HostReceivedLocation = LocationReceived,
            AmountOfPeers = (_netServer?.ConnectedPeersCount ?? 0) + 1
        };

        if (gameExists && packet.RequestStart && _assignedPeerProfiles?.ContainsKey(peer) == true)
        {
            if (FikaSingleton<IFikaGame>.Instance?.GameController is { } gameController)
            {
                gameController.RaidStarted = true;
                respondPackage.RaidStarted = true;
            }
        }

        if (gameExists && HostReady)
        {
            var gameTimer = _coopHandler.LocalGameInstance.GameController.GameInstance.GameTimer;
            var sessionTime = NativeNullable<Il2CppSystem.TimeSpan>.Read(gameTimer, "<SessionTime>k__BackingField");
            // During reconnect the native GameTimer can briefly exist without its
            // SessionTime payload. Do not read Nullable.Value in that window and
            // do not advertise a partially populated timing packet to the client.
            if (_gameStartTime is not { } gameStartTime || !sessionTime.HasValue)
            {
                respondPackage.HostReady = false;
            }
            else
            {
                respondPackage.GameTime = gameStartTime;
                respondPackage.SessionTime = sessionTime.Value.ToManaged();
                respondPackage.GameDateTime = (FikaSingleton<IFikaGame>.Instance.GameController as HostGameController).GameDateTime;
            }
        }

        if (_assignedPeerNetIds?.TryGetValue(peer, out var assignedNetId) == true && assignedNetId != 0)
        {
            // A player scav that walks into a running raid can announce itself
            // before it ever reports a remote net id, so fall back to the net id
            // the server assigned to this peer. Without the binding every proceed
            // request is answered with "unbound player".
            var remoteNetId = packet.RemoteNetId != 0 ? packet.RemoteNetId : assignedNetId;
            if (remoteNetId == assignedNetId &&
                _assignedPeerProfiles?.TryGetValue(peer, out var assignedProfile) == true &&
                _coopHandler?.Players?.TryGetValue(remoteNetId, out var player) == true &&
                player.ProfileId == assignedProfile && !ReferenceEquals(peer.Player, player))
            {
                peer.Player = player;
                _logger.LogInfo($"Bound peer {peer.Id} to player {player.NetId} ({player.ProfileId}), " +
                    $"remoteNetId={packet.RemoteNetId}");
            }
        }

        SendData(ref respondPackage, DeliveryMethod.ReliableOrdered);
    }

    private void OnCommonPlayerPacketReceived(CommonPlayerPacket packet, NetPeer peer)
    {
        if (!CanPeerSendCommonPlayerPacket(peer, packet.NetId, packet.Type)) return;
        if (_coopHandler.QueueSpawningPlayerState(packet)) return;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            packet.Execute(playerToApply);
        }
    }

    private void OnInventoryPacketReceived(InventoryPacket packet, NetPeer peer)
    {
        // A client must not choose another player's inventory as the host-side
        // execution target. Reconnect associates the peer on the information
        // exchange before normal inventory traffic starts.
        if (peer.Player == null || peer.Player.NetId != packet.NetId)
        {
            _logger.LogWarning($"Rejected inventory operation for net id {packet.NetId} from an unbound peer");
            return;
        }

        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            // [CarExtract] A paid exfiltration point ("car extract") handover is a
            // local prediction on the paying client: the native interaction already
            // told the player the money was transferred. Rejecting the request here
            // is therefore the only way to stop a second payment, and the rejection
            // carries the authoritative inventory back so the client can roll its
            // prediction back.
            ExfiltrationPoint carExtractPoint = null;
            try
            {
                if (packet.Descriptor.TryCast<EFT.InventoryLogic.Operations.TransferExfiltrationPaymentOperationDescriptor>() is { } payment)
                {
                    carExtractPoint = FikaExfilManager.FindExfiltrationPoint(payment.PointName);
                    _logger.LogInfo($"[CarExtract] received a payment request from {playerToApply.ProfileId} " +
                        $"(net id {packet.NetId}) for point '{payment.PointName}': {FikaExfilManager.DescribePaymentItems(payment)}");
                    var rejection = carExtractPoint == null
                        ? $"There is no exfiltration point named '{payment.PointName}'."
                        : FikaExfilManager.HasPlayerPaid(carExtractPoint, playerToApply)
                            ? $"Player already paid for '{payment.PointName}'."
                            : null;
                    if (rejection != null)
                    {
                        _logger.LogWarning($"[CarExtract] rejected the payment from {playerToApply.ProfileId} for " +
                            $"'{payment.PointName}': {rejection}");
                        SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Failed, rejection), peer);
                        SendInventoryResyncToPeer(packet.NetId, packet.CallbackId, peer);
                        ResyncInventoryIdPacket rejectedResyncPacket = new(playerToApply.NetId);
                        SendDataToPeer(ref rejectedResyncPacket, DeliveryMethod.ReliableOrdered, peer);
                        return;
                    }
                }

                // [PlantGuard] (analysis/plant-client-host-crash): the host rebuilds a client's
                // operation from its descriptor and executes it natively afterwards. For the
                // destroy-shaped descriptors that rebuild walks the sender's item graph and builds an
                // ItemManipulator.Discard result before the operation object even exists; a removal
                // this host can already tell it cannot complete must be stopped in managed code,
                // here, instead of being handed to that native chain. Every refusal answers exactly
                // like the conversion failure below (Failed + authoritative resync + id resync), so
                // the client is never left waiting for a callback.
                if (PlantRelayGuard.TryRefuse(playerToApply, packet.Descriptor, out var guardRefusal))
                {
                    PlantRelayGuard.ReportRefusal(packet.NetId, packet.CallbackId, packet.Descriptor, guardRefusal);
                    SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                        OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Failed,
                        "The host does not support this operation: " + guardRefusal), peer);
                    SendInventoryResyncToPeer(packet.NetId, packet.CallbackId, peer);
                    ResyncInventoryIdPacket guardResyncPacket = new(playerToApply.NetId);
                    SendDataToPeer(ref guardResyncPacket, DeliveryMethod.ReliableOrdered, peer);
                    return;
                }

                if (playerToApply.InventoryController is IOperationHandler inventoryController)
                {
                    var result = inventoryController.CreateOperationFromDescriptor(packet.Descriptor);
#if DEBUG
                    if (result.Succeeded)
                    {
                        ConsoleScreen.Log($"Received InvOperation: {result.Value.GetType().Name}, Id: {result.Value.Id}");
                    }
#endif

                    if (result.Failed)
                    {
                        _logger.LogError($"ItemControllerExecutePacket::Operation conversion failed: {result.Error}");
                        // The client's descriptor can name items the host never spawned
                        // (profile-filtered loot). Log enough to tell a resolver defect
                        // apart from a genuinely missing world item.
                        try
                        {
                            _logger.LogError($"ItemControllerExecutePacket::Descriptor {packet.Descriptor}");
                            if (packet.Descriptor is MoveOperationDescriptor moveDescriptor)
                            {
                                var world = Singleton<GameWorld>.Instance;
                                var fromId = moveDescriptor.From?.Container?.ContainerId;
                                var toId = moveDescriptor.To?.Container?.ContainerId;
                                _logger.LogError($"ItemControllerExecutePacket::Move from {fromId} to {toId} " +
                                    $"fromFound={(fromId != null && world?.FindItemById(fromId).Succeeded == true)} " +
                                    $"toFound={(toId != null && world?.FindItemById(toId).Succeeded == true)}");
                            }
                        }
                        catch (Exception diagnosticException)
                        {
                            _logger.LogError($"ItemControllerExecutePacket::Descriptor dump failed: {diagnosticException}");
                        }
                        SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Failed, result.Error.ToString()), peer);

                        SendInventoryResyncToPeer(packet.NetId, packet.CallbackId, peer);

                        ResyncInventoryIdPacket resyncPacket = new(playerToApply.NetId);
                        SendDataToPeer(ref resyncPacket, DeliveryMethod.ReliableOrdered, peer);
                        return;
                    }

                    var handler = _inventoryOperationHandlerPool.Get();
                    handler.Set(result, packet.CallbackId, packet.NetId, peer, this);
                    try
                    {
                        // Prepare the relay before acknowledging Started. If
                        // serialization fails, the client only sees Failed and
                        // the native operation has not been executed.
                        handler.SetRelayPayload(CreateInventoryRelayPayload(packet));
                        SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Started), peer);

                        // Native execution can finish after the receive packet
                        // has been returned to its pool.
                        handler.OperationResult.Value.Execute(handler.HandleResultDelegate);

                        if (carExtractPoint != null)
                        {
                            _logger.LogInfo($"[CarExtract] applied the payment from {playerToApply.ProfileId} on the host: " +
                                $"point '{carExtractPoint.Settings.Name}' status {carExtractPoint.Status}, " +
                                $"queued {carExtractPoint.QueuedPlayers.Count}");
                        }
                    }
                    catch (Exception exception)
                    {
                        // A native Execute may invoke its completion synchronously
                        // and still throw while unwinding. Its terminal status has
                        // already been sent in that case; do not contradict it.
                        if (handler.TerminalStatusReported)
                        {
                            _logger.LogError($"Inventory operation threw after completion for {packet.NetId}: {exception}");
                            return;
                        }
                        // No completion was sent. Invalidate the callback before
                        // sending the failure and authoritative snapshot below.
                        handler.Cancel();
                        throw;
                    }
                    finally
                    {
                        handler.FinishDispatch();
                    }
                }
                else
                {
                    throw new InvalidTypeException($"Inventory controller was not of type {nameof(IOperationHandler)}!");
                }
            }
            catch (Exception exception)
            {
                _logger.LogError($"ItemControllerExecutePacket::Exception thrown: {exception}");
                SendGenericPacketToPeer(EGenericSubPacketType.OperationCallback,
                            OperationCallbackPacket.FromValue(packet.NetId, packet.CallbackId, EOperationStatus.Failed, exception.Message), peer);

                SendInventoryResyncToPeer(packet.NetId, packet.CallbackId, peer);

                ResyncInventoryIdPacket resyncPacket = new(playerToApply.NetId);
                SendDataToPeer(ref resyncPacket, DeliveryMethod.ReliableOrdered, peer);
            }
        }
    }

    private void OnWeaponPacketReceived(WeaponPacket packet, NetPeer peer)
    {
        if (peer.Player == null || packet.NetId != peer.Player.NetId) return;
        if (_coopHandler.Players.TryGetValue(packet.NetId, out var playerToApply))
        {
            packet.Execute(playerToApply);
        }
    }
}
