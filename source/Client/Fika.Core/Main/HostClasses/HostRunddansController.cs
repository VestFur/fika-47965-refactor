using Fika.Core.Main.BaseClasses;
using Comfort.Common;
using EFT;
using Fika.Core.Main.Utils;
using Fika.Core.Main.Players;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Communication;
using JsonType;

namespace Fika.Core.Main.HostClasses;

public class HostRunddansController(RunddansGlobalSettings settings, LocationSettings.Location location)
    : FikaWorldController<LocalRunddansController>(new LocalRunddansController(settings, location))
{
    internal bool EventStarted { get; private set; }

    public void InteractWithEventObject(Player player, InteractWithEventObjectPacket packet) => HandleInteraction(player, packet);

    public void ObservedInteractWithEventObject(Player player, InteractWithEventObjectPacket packet, NetPeer peer) => HandleInteraction(player, packet);

    private void HandleInteraction(Player player, InteractWithEventObjectPacket packet)
    {
        // Apply the same eligibility and transition rules to local, remote and headless users.
        if (player == null || player.HealthController?.IsAlive != true || !packet.hasInteraction) return;

        TransitController transit;
        if (FikaBackendUtils.IsHeadless)
        {
            // Headless installs FikaHeadlessTransitController, which is a
            // direct TransitController subclass and deliberately has no local
            // UI/event transition history.  The native IsValid<T> helper
            // requires that history and therefore rejects valid headless
            // interactions.  Preserve the old headless contract: only the
            // presence of a transit controller is required.
            transit = Singleton<GameWorld>.Instance?.TransitController;
            if (transit == null || !Native.Objects.TryGetValue(packet.objectId, out var eventObject)) return;
            HandleInteractionCore(player, packet, transit, eventObject);
            return;
        }

        // Graphical hosts use LocalTransitController and retain the native
        // event-history eligibility check.
        if (!Native.IsValid(player, out LocalTransitController localTransit, out var history) ||
            history?.events != true || !Native.Objects.TryGetValue(packet.objectId, out var localEventObject)) return;
        transit = localTransit;
        HandleInteractionCore(player, packet, transit, localEventObject);
    }

    private void HandleInteractionCore(Player player, InteractWithEventObjectPacket packet,
        TransitController transit, EventObject eventObject)
    {
        if ((packet.interaction == EventObject.EInteraction.Run && eventObject.State != EventObject.EState.Ready) ||
            (packet.interaction == EventObject.EInteraction.Repair && eventObject.State != EventObject.EState.Broken) ||
            (packet.interaction != EventObject.EInteraction.Run && packet.interaction != EventObject.EInteraction.Repair))
        {
            Notify(player, RunddansMessagesEvent.EType.NonInteractive);
            return;
        }

        string consumedItemId = null;
        if (packet.interaction == EventObject.EInteraction.Run)
        {
            if (!Native.TryGetConsumableItem(player, out var item))
            {
                Notify(player, RunddansMessagesEvent.EType.NoRequiredItem);
                return;
            }
            consumedItemId = item.Id;
            if (!Native.TryRemoveConsumableItem(item))
            {
                FikaGlobals.LogError($"Runddans could not remove item {consumedItemId} from {player.ProfileId}");
                return;
            }
        }
        if (consumedItemId != null)
        {
            var removal = new EventControllerEventPacket
            {
                NetId = player.TryCast<FikaPlayer>()?.NetId ?? player.Id,
                ItemId = consumedItemId, Type = EventControllerEventPacket.EEventType.RemoveItem
            };
            FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref removal, DeliveryMethod.ReliableOrdered);
        }
        EventStarted = true;
        Native.SetState(packet.objectId, EventObject.EState.Running);
        transit.EnablePoints(false);
        if (!FikaBackendUtils.IsHeadless && GamePlayerOwner.MyPlayer != null &&
            transit.TryCast<LocalTransitController>() is { } localTransit)
            localTransit.UpdateTimers();
        var started = new EventControllerEventPacket { Type = EventControllerEventPacket.EEventType.StartedEvent };
        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref started, DeliveryMethod.ReliableOrdered);
    }

    internal EventControllerEventPacket CreateSnapshot()
    {
        var state = new RunddansStateEvent { PlayerRaidId = 0, Objects = new() };
        var durability = new System.Collections.Generic.Dictionary<int, float>();
        var dateTimes = new System.Collections.Generic.Dictionary<int, long>();
        foreach (var entry in Native.Objects)
        {
            state.Objects.Add(entry.Key, entry.Value.State);
            durability[entry.Key] = entry.Value._durability;
            dateTimes[entry.Key] = entry.Value._dateTime.Ticks;
        }
        return new EventControllerEventPacket
        {
            Type = EventControllerEventPacket.EEventType.StateEvent,
            Event = state, Durability = durability, DateTimeTicks = dateTimes
        };
    }

    public void OnTriggerStateChanged(EventObject.EState state)
    {
        var packet = CreateSnapshot();
        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private void Notify(Player player, RunddansMessagesEvent.EType type)
    {
        if (player.IsYourPlayer)
        {
            if (type == RunddansMessagesEvent.EType.NoRequiredItem) Native.NoRequiredItemNotification();
            else Native.NonInteractiveNotification();
            return;
        }
        var packet = new EventControllerEventPacket
        {
            Type = EventControllerEventPacket.EEventType.MessageEvent,
            Event = new RunddansMessagesEvent { PlayerRaidId = player.RaidId, Type = type }
        };
        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    protected override void DisposeNative() => Native.Dispose();
}
