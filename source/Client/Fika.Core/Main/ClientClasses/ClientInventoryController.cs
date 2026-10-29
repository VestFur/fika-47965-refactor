using EFT.Settings;
using System;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using EFT.UI;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets;
using Fika.Core.Networking.Packets.Communication;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Packets.World;
using Fika.Core.Networking.Pooling;

namespace Fika.Core.Main.ClientClasses;

public sealed class ClientInventoryController : BaseInventoryController
{

    public ClientInventoryController(System.IntPtr pointer) : base(pointer) { }


    /// <summary>
    /// How long a client waits for the host to acknowledge a sent inventory
    /// operation before failing it locally.
    /// </summary>
    private const int OperationAcknowledgementTimeoutMs = 30000;

    /// <summary>
    /// The acknowledgement only means "the host received the operation". A
    /// missing terminal status afterwards keeps the operation in progress,
    /// which locks the requesting player in the interaction state (ground loot
    /// pickup) until the raid ends, so the same watchdog releases it locally.
    /// </summary>
    private const int OperationCompletionTimeoutMs = 20000;

    public FikaPlayer FikaPlayer { get; }

    public override bool HasDiscardLimits
    {
        get
        {
            return false;
        }
    }
    private readonly Player _player;
    private readonly ClientInventoryOperationHandlerPool _clientInventoryOperationHandlerPool;
    private readonly System.Collections.Generic.Dictionary<ushort, ClientInventoryOperationHandler> _activeOperationHandlers = [];

    /// <summary>
    /// Paid exfiltration point ("car extract") payments this client dispatched
    /// and whose operation has not finished yet, keyed by exfil point name.
    /// The native interaction keeps offering the handover until the point
    /// reports the player as queued, so without this every extra click would
    /// queue another payment.
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<string, ushort> _carExtractPayments = [];

    public ClientInventoryController(Player player, Profile profile, bool examined, bool strictSync)
        : base(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<ClientInventoryController>(), player, profile, examined, strictSync)
    {
        _player = player;
        FikaPlayer = player.TryCast<FikaPlayer>()
            ?? throw new InvalidOperationException("Client inventory controller received a non-Fika player");
        IdSource = MongoID.Generate(true);
        PlayerSearchController = new ActiveSearchController(profile, this);
        _clientInventoryOperationHandlerPool = new ClientInventoryOperationHandlerPool(8, ClientInventoryOperationHandler.CreateInstance);
    }

    public override IPlayerSearchController PlayerSearchController { get; }

    public override void GetTraderServicesDataFromServer(string traderId)
    {
        if (FikaBackendUtils.IsClient)
        {
            RequestPacket request = new()
            {
                Type = ERequestSubPacketType.TraderServices,
                RequestSubPacket = new RequestSubPackets.TraderServicesRequest()
                {
                    NetId = FikaPlayer.NetId,
                    TraderId = traderId
                }
            };

            FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref request, DeliveryMethod.ReliableOrdered);
            return;
        }

        FikaPlayer.UpdateTradersServiceData(traderId).HandleExceptions();
    }

    public override void CallMalfunctionRepaired(Weapon weapon)
    {
        if (Singleton<SettingsManager>.Instance.Game.Settings.MalfunctionVisability)
        {
            MonoBehaviourSingleton<PreloaderUI>.Instance.MalfunctionGlow.ShowGlow(BattleUIMalfunctionGlow.EGlowType.Repaired, true, GetGlowAlphaMultiplier());
        }
    }

    public override void ExecuteStationaryOperation(StationaryWeapon stationaryWeapon, Callback callback = null)
    {
        FikaPlayer.OperationStationaryCallbackId = NextOperationId;
        global::Fika.Core.Main.Utils.NativeBaseCalls.Call2(this, stationaryWeapon, callback);
    }

    public override void Execute(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        MainThreadDispatcher.RunAsync(() => HandleOperation(operation, callback)).Forget();
    }

    private async Task HandleOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        if (_player.HealthController.IsAlive)
        {
            await Task.Yield();
        }
        RunClientOperation(operation, callback);
    }

    /// <summary>
    /// Gets an inventory handler
    /// </summary>
    /// <returns>A pooled handler</returns>
    public ClientInventoryOperationHandler GetHandler()
    {
        return _clientInventoryOperationHandlerPool.Get();
    }

    /// <summary>
    /// Returns a handler
    /// </summary>
    /// <param name="handler">The handler to return</param>
    public void ReturnHandler(ClientInventoryOperationHandler handler)
    {
        // A late completion from an earlier operation must not remove the
        // current handler when the native ushort operation ID wraps around.
        if (_activeOperationHandlers.TryGetValue(handler.CallbackId, out var current) &&
            ReferenceEquals(current, handler))
        {
            _activeOperationHandlers.Remove(handler.CallbackId);
            // The server callback registration has to leave with its handler. It
            // is normally removed by FikaPlayer.HandleCallbackFromServer when a
            // terminal status arrives, but every FailLocally path (30 s
            // acknowledgement watchdog, 50 s completion watchdog, resync decode
            // failure, "still in progress after the server reported X") ends the
            // operation WITHOUT an answer, and its registration stayed in
            // FikaPlayer.OperationCallbacks for the rest of the raid.
            // FikaPlayer.WaitingForCallback reads that dictionary and the client
            // firearm/grenade controllers return false from CanPressTrigger and
            // CanStartReload while it is non-empty, so one failed operation kept
            // the local player unable to shoot.
            FikaPlayer.OperationCallbacks.Remove(handler.CallbackId);
        }
        _clientInventoryOperationHandlerPool.ReturnHandler(handler);
    }

    internal void ReceiveInventoryResync(InventoryResyncPacket packet)
    {
        if (_activeOperationHandlers.TryGetValue(packet.CallbackId, out var handler))
        {
            // Decoding the authoritative snapshot must never abort the packet
            // handler: the pending operation would then wait for a callback that
            // can no longer arrive, leaving the player stuck in the interaction
            // state the rejection was supposed to release.
            try
            {
                // A descriptor that decodes to nothing must never reach
                // ReplaceInventory: replacing the tree with null empties the whole
                // inventory for the rest of the raid. Fail the operation instead
                // so its watchdog releases the caller.
                var authoritative = packet.Descriptor.ToInventory();
                if (authoritative == null)
                {
                    FikaGlobals.LogError($"{ID} - The host's authoritative inventory snapshot for operation " +
                        $"{packet.CallbackId} decoded to nothing");
                    handler.FailWithoutServerStatus("The host's authoritative inventory snapshot was empty.");
                }
                else
                {
                    // [SyncDiag] Count at the decode site: this is the tree that replaces the local one.
                    FikaGlobals.LogInfo($"[SyncDiag] op={packet.CallbackId} resync-descriptor " +
                        $"items={CountSnapshotItems(authoritative)}");
                    handler.ReceiveAuthoritativeInventory(authoritative);
                }
            }
            catch (Exception exception)
            {
                FikaGlobals.LogError($"{ID} - Failed to decode the authoritative inventory for " +
                    $"operation {packet.CallbackId}: {exception}");
                try
                {
                    handler.FailWithoutServerStatus("The host's authoritative inventory snapshot could not be applied.");
                }
                catch (Exception failException)
                {
                    FikaGlobals.LogError($"{ID} - Failed to release operation {packet.CallbackId}: {failException}");
                }
            }
        }
        // A snapshot without its operation can be a late duplicate after a
        // successful newer operation. Replacing the entire inventory here
        // would roll back that newer state.
    }

    private void RunClientOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        if (!CanExecute(operation))
        {
            try
            {
                var itemId = operation.TryCast<MoveOperation>() is { } moving ? moving.Item?.Id?.ToString() : null;
                FikaGlobals.LogWarning($"{ID} - Cannot execute local operation {operation.Id} " +
                    $"({operation.GetIl2CppType().Name}{(itemId == null ? string.Empty : ", item " + itemId)}); " +
                    "failing it locally so the native pickup interaction can finish");
            }
            catch (Exception diagnosticException)
            {
                FikaGlobals.LogError($"{ID} - Failed to describe the rejected operation: {diagnosticException}");
            }

            // CanExecute is the native gate (PlayerInventoryController delegates
            // it to the hands controller, and CheckItemAction refuses everything
            // while ItemController.Locked is set). A refusal is the evidence the
            // inventory watchdog needs, and the operation may never have started,
            // so no callback of ours will ever release the interaction state.
            // [LootDiag] (analysis/pickup-stuck-again): a take whose operation never left this
            // client. With the pickup-request line next to it, this is the "refused here, not
            // lost on the wire" evidence.
            var refusedItemId = "n/a";
            try
            {
                if (operation.TryCast<MoveOperation>() is { } refusedMove && refusedMove.Item != null)
                {
                    refusedItemId = refusedMove.Item.Id.ToString();
                }
            }
            catch
            {
                // Reporting a refused operation must never fail on its own item lookup.
            }
                        NoteGateRefusal($"operation {operation.Id} ({operation.GetIl2CppType().Name}) refused by CanExecute");
            NativeInteractionRelease.Release(FikaPlayer, "local-operation-refused");
            try
            {
                operation.Dispose();
            }
            catch (Exception disposeException)
            {
                // Disposing an operation that is still "Started" raises the
                // terminal event and can throw; when it does, the Begin events it
                // registered stay in ItemController.ActiveEvents and keep the item
                // busy (PlayerIsBusyError) for the rest of the raid.
                FikaGlobals.LogError($"[InventoryRepair] {ID} - disposing the refused operation {operation.Id} threw, " +
                    $"its item events may stay registered: {disposeException}");
            }
            callback?.Fail("LOCAL: hands controller can't perform this operation");
            return;
        }

        // Quest transactions are authoritative locally; their committed inventory
        // effects are sent by the quest-operation hooks, after successful execution.
        if (operation.TryCast<BaseQuestOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // Quest reading (note, chapter conditions, condition, note link) and tape
        // study are not BaseQuestOperation subclasses, but they change nothing but
        // the local player's own quest and item data - the profile this machine
        // owns and uploads when the raid ends. They run here and are never
        // relayed; see QuestOperations for why.
        if (QuestOperations.IsLocalOnly(operation, out var questDetail))
        {
            FikaGlobals.LogInfo($"[QuestOp] {operation.GetIl2CppType().Name} executed locally ({questDetail}); " +
                "local quest data, not relayed");
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // Do not replicate picking up quest items, throws an error on the other clients            
        if (operation.TryCast<MoveOperation>() is { } moveOperation)
        {
            var lootedItem = moveOperation.Item;
            if (lootedItem.QuestItem)
            {
                global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
                return;
            }
        }

        // Do not replicate stashing quest items
        if (operation.TryCast<RemoveOperation>() is { } discardOperation && discardOperation.Item.QuestItem)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // Search and dialog progress require local dialog state.
        if (operation.TryCast<SinglePlayerSearchContentOperation>() != null
            || operation.TryCast<SetDialogProgressOperation>() != null)
        {
            global::Fika.Core.Main.Utils.NativeBaseCalls.Call3(this, operation, callback);
            return;
        }

        // [CarExtract] A paid exfiltration point hands its cash over through
        // EFT.InventoryLogic.Operations.TransferExfiltrationPaymentOperation. The
        // native interaction tells the player the money was transferred as soon
        // as TransferExitItem returns, so a second click while the first payment
        // is still unanswered must be refused here instead of queuing another
        // handover the host would happily apply a second time.
        var carExtractPoint = GetCarExtractPointName(operation);
        if (carExtractPoint != null
            && _carExtractPayments.TryGetValue(carExtractPoint, out var pendingPayment)
            && _activeOperationHandlers.ContainsKey(pendingPayment))
        {
            FikaGlobals.LogWarning($"[CarExtract] {ID} - refusing a second payment for '{carExtractPoint}', " +
                $"operation {pendingPayment} is still awaiting the host");
            FikaNotifications.DisplayMessageNotification(
                $"[CarExtract] A payment for '{carExtractPoint}' is already being processed.",
                iconType: ENotificationIconType.Alert);
            try
            {
                operation.Dispose();
            }
            catch (Exception disposeException)
            {
                FikaGlobals.LogError($"[CarExtract] {ID} - disposing the duplicate payment {operation.Id} threw: {disposeException}");
            }
            callback?.Fail("A payment for this extraction point is already being processed.");
            return;
        }

        var handler = _clientInventoryOperationHandlerPool.Get();
        var operationNum = operation.Id;
        try
        {
            handler.Set(this, operation, callback);
            // Registration, the active table and the watchdog are all inside the
            // guarded block: a throw from any of them used to leave the caller's
            // callback uninvoked and the operation without a watchdog, which locks
            // the player in the native pickup interaction state for the rest of the
            // raid.
            operationNum = AddOperationCallback(operation, handler.ServerStatusDelegate);
            _activeOperationHandlers[operationNum] = handler;
            if (carExtractPoint != null)
            {
                _carExtractPayments[carExtractPoint] = operationNum;
            }
            WatchOperationAcknowledgement(handler, operationNum);

            var packet = InventoryPacket.FromValue(FikaPlayer.NetId, operation);
            // Defensive: every quest operation takes the local branch above, so this
            // only fires if one ever reaches the relay again.
            if (QuestOperations.TryDescribe(packet.Descriptor, out var relayedQuestDetail))
            {
                FikaGlobals.LogInfo($"[QuestOp] relaying {operation.GetIl2CppType().Name} to the host ({relayedQuestDetail})");
            }
            if (packet.Descriptor.TryCast<TransferExfiltrationPaymentOperationDescriptor>() is { } paymentDescriptor)
            {
                FikaGlobals.LogInfo($"[CarExtract] client payment request: point '{paymentDescriptor.PointName}', " +
                    $"{FikaExfilManager.DescribePaymentItems(paymentDescriptor)}, operation {operation.Id}, player {FikaPlayer.ProfileId}");
            }
            try
            {
                if (operation.TryCast<MoveOperation>() is { } moving)
                {
                    FikaGlobals.LogInfo($"Sending inventory operation {operation.Id} ({operation.GetIl2CppType().Name}) " +
                        $"for item {moving.Item?.Id} from {moving.Item?.CurrentAddress} descriptor: {packet.Descriptor}");
                }
                else
                {
                    // Drops (and every other non-move operation) were invisible in the
                    // log, which made "the dropped backpack vanished" untraceable.
                    FikaGlobals.LogInfo($"Sending inventory operation {operation.Id} " +
                        $"({operation.GetIl2CppType().Name}) descriptor: {packet.Descriptor}");
                }
            }
            catch (Exception diagnosticException)
            {
                FikaGlobals.LogError($"Failed to describe outgoing operation {operation.Id}: {diagnosticException}");
            }

            FikaPlayer.PacketSender.NetworkManager.SendGenericPacket(EGenericSubPacketType.InventoryOperation, packet);
        }
        catch (Exception dispatchException)
        {
            FikaGlobals.LogError($"{ID} - Failed to dispatch inventory operation {operationNum}: {dispatchException}");
            if (carExtractPoint != null)
            {
                // The native interaction already reported the handover as done, so
                // the player has to be told that the host never received it.
                FikaGlobals.LogError($"[CarExtract] the payment for '{carExtractPoint}' (operation {operationNum}) " +
                    $"never reached the host: {dispatchException.Message}");
                FikaNotifications.DisplayMessageNotification(
                    $"[CarExtract] The payment for '{carExtractPoint}' could not be sent to the host.",
                    iconType: ENotificationIconType.Alert);
            }
            try
            {
                FikaPlayer.OperationCallbacks.Remove(operationNum);
            }
            catch (Exception removeException)
            {
                FikaGlobals.LogError($"{ID} - Failed to remove the callback of operation {operationNum}: {removeException}");
            }
            ReturnHandler(handler);
            // The native pickup flow waits for this callback; without it the player
            // stays locked in the PickUpState and the interaction queue.
            global::Fika.Core.Main.Utils.NativeInteractionRelease.Release(FikaPlayer, "dispatch-failed");
            try
            {
                operation.Dispose();
            }
            catch (Exception disposeException)
            {
                FikaGlobals.LogError($"{ID} - Failed to dispose operation {operationNum}: {disposeException}");
            }
            try
            {
                callback?.Invoke(new FailedResult($"The inventory operation {operationNum} could not be sent to the host."));
            }
            catch (Exception callbackException)
            {
                FikaGlobals.LogError($"{ID} - Failed to fail operation {operationNum} locally: {callbackException}");
            }
            return;
        }
#if DEBUG
        ConsoleScreen.Log($"InvOperation: {operation.GetType().Name}, Id: {operation.Id}");
#endif
    }

    /// <summary>
    /// Returns the exfil point name of a paid exfiltration point ("car extract")
    /// payment operation, or null when the operation is not a payment. Reading
    /// the name is purely diagnostic and must never stop a payment from being
    /// dispatched, so every failure is swallowed and reported as "not a payment".
    /// </summary>
    private static string GetCarExtractPointName(EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        try
        {
            if (operation.TryCast<TransferExfiltrationPaymentOperation>() is not { } payment)
            {
                return null;
            }

            var pointName = payment._result?.PointName;
            return string.IsNullOrEmpty(pointName) ? null : pointName;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[CarExtract] failed to read the payment operation {operation?.Id}: {exception}");
            return null;
        }
    }

    /// <summary>
    /// The host acknowledges an inventory operation before executing it. If that
    /// acknowledgement never arrives, the operation is failed locally so the
    /// requesting player is released instead of being stuck in the interaction
    /// state waiting for a callback that can no longer come.
    /// </summary>
    private async void WatchOperationAcknowledgement(ClientInventoryOperationHandler handler, ushort operationNum)
    {
        try
        {
            await Task.Delay(OperationAcknowledgementTimeoutMs);
            if (!handler.IsActive) return;
            if (!handler.ReceivedServerStatus)
            {
                if (!_activeOperationHandlers.TryGetValue(operationNum, out var pending) || !ReferenceEquals(pending, handler)) return;
                FikaGlobals.LogError($"{ID} - No acknowledgement for inventory operation {operationNum} " +
                    $"after {OperationAcknowledgementTimeoutMs} ms, failing it locally: {handler.Operation}");
                handler.FailWithoutServerStatus("The host never acknowledged the inventory operation.");
                return;
            }

            await Task.Delay(OperationCompletionTimeoutMs);
            if (!handler.IsActive) return;
            if (!_activeOperationHandlers.TryGetValue(operationNum, out var unfinished) || !ReferenceEquals(unfinished, handler)) return;
            FikaGlobals.LogError($"{ID} - Inventory operation {operationNum} was acknowledged but never finished " +
                $"after {OperationAcknowledgementTimeoutMs + OperationCompletionTimeoutMs} ms, failing it locally: {handler.Operation}");
            handler.FailWithoutServerStatus("The host did not finish the inventory operation.");
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"{ID} - Inventory operation acknowledgement watchdog failed: {exception}");
        }
    }

    public override bool HasCultistAmulet(out CultistAmulet amulet)
    {
        amulet = null;
        var slots = new Il2CppSystem.Collections.Generic.List<EquipmentSlot>();
        slots.Add(EquipmentSlot.Pockets);
        var enumerator = Inventory.GetItemsInSlots(slots).GetEnumerator();

        while (enumerator.MoveNext())
        {
            if (enumerator.Current?.TryCast<CultistAmulet>() is { } cultistAmuletClass)
            {
                amulet = cultistAmuletClass;
                return true;
            }
        }
        return false;
    }

    public ushort AddOperationCallback(EFT.InventoryLogic.Operations.AbstractOperation operation, Action<ServerOperationStatus> callback)
    {
        var id = operation.Id;
        FikaPlayer.OperationCallbacks.Add(id, callback);
        return id;
    }

    /// <summary>Operations the port dispatched whose handler is still registered.</summary>
    internal override int PendingOperationCount => _activeOperationHandlers.Count;

    /// <summary>Server callback registrations without a handler behind them.</summary>
    internal override int ReleaseStaleServerCallbacks(string reason) => ReleaseStaleOperationCallbacks(reason);

    /// <inheritdoc/>
    internal override int CountStaleServerCallbacks() => CountStaleOperationCallbacks();

    /// <summary>
    /// Server callback registrations without a handler behind them. They are the
    /// leftovers of an operation that failed locally, and
    /// <see cref="FikaPlayer.WaitingForCallback"/> counts them: while there is
    /// one, the client hands controllers refuse to press the trigger or start a
    /// reload.
    /// </summary>
    internal int CountStaleOperationCallbacks()
    {
        var stale = 0;
        foreach (var pair in FikaPlayer.OperationCallbacks)
        {
            if (!_activeOperationHandlers.ContainsKey((ushort)pair.Key))
            {
                stale++;
            }
        }
        return stale;
    }

    /// <summary>
    /// Drops every server callback registration that no registered handler owns.
    /// A live operation always owns one, so this can only remove leftovers.
    /// </summary>
    internal int ReleaseStaleOperationCallbacks(string reason)
    {
        System.Collections.Generic.List<uint> stale = null;
        foreach (var pair in FikaPlayer.OperationCallbacks)
        {
            if (_activeOperationHandlers.ContainsKey((ushort)pair.Key))
            {
                continue;
            }
            (stale ??= []).Add(pair.Key);
        }

        if (stale == null)
        {
            return 0;
        }

        foreach (var id in stale)
        {
            FikaPlayer.OperationCallbacks.Remove(id);
        }

        FikaGlobals.LogWarning($"[InventoryRepair] {ID} - dropped {stale.Count} server callback registration(s) " +
            $"that had no handler left (reason={reason})");
        return stale.Count;
    }

    /// <summary>
    /// Recovery for a client inventory pipeline that stopped answering. Only
    /// called by the conservative watchdog in <c>CoopGame</c>, which requires a
    /// player attempt (open inventory or a refused operation) AND a gate that
    /// stayed closed while the player was idle.
    /// <para>
    /// It clears the port's own bookkeeping first (handlers that are registered
    /// although they are no longer active, callback registrations without a
    /// handler), then the native gate, and finally releases the native
    /// interaction state. <paramref name="escalate"/> additionally drops the item
    /// events the native runtime itself reports as outdated, which is what keeps
    /// a single item unmovable after an aborted operation.
    /// </para>
    /// </summary>
    internal override void RepairBlockedInventory(string reason, bool escalate)
    {
        var deadHandlers = 0;
        System.Collections.Generic.List<ushort> deadKeys = null;
        foreach (var pair in _activeOperationHandlers)
        {
            if (pair.Value == null || !pair.Value.IsActive)
            {
                (deadKeys ??= []).Add(pair.Key);
            }
        }

        if (deadKeys != null)
        {
            foreach (var key in deadKeys)
            {
                // Only the table entry is dropped: an inactive handler was already
                // returned to the pool once, and returning it again would hand the
                // same instance to two operations.
                _activeOperationHandlers.Remove(key);
                FikaPlayer.OperationCallbacks.Remove(key);
                deadHandlers++;
            }
        }

        if (deadHandlers > 0)
        {
            FikaGlobals.LogWarning($"[InventoryRepair] {ID} - dropped {deadHandlers} inventory handler(s) " +
                "that were still registered although they were no longer active");
        }

        base.RepairBlockedInventory(reason, escalate);
    }

    public override SearchContentOperation CreateSearchOperation(SearchableItem item)
    {
        return new SinglePlayerSearchContentOperation(GetAndIncrementNextOperationId(), this, PlayerSearchController, Profile, item);
    }

    public readonly struct ServerOperationStatus(EOperationStatus status, string error)
    {
        public readonly EOperationStatus Status = status;
        public readonly string Error = error;
    }

    public void ClearPool()
    {
        foreach (var callbackId in _activeOperationHandlers.Keys)
            FikaPlayer.OperationCallbacks.Remove(callbackId);
        _activeOperationHandlers.Clear();
        _clientInventoryOperationHandlerPool.Dispose();
        // [InvDiag] (2026-10-27, T4) The bookkeeping report budget is static and was never reset, so after 40 relayed
        // operations in one game process the "known=a->b" line that the item acceptance is read from stopped
        // appearing - a later raid could not prove anything about it. ClearPool runs once per raid teardown.
        ClientInventoryOperationHandler.ResetBookkeepingReport();
    }

    /// <summary>
    /// (2026-10-17) The local half of a relayed operation runs through Operation.ExecuteWithoutDispose, which
    /// bypasses the native InventoryController.Execute wrapper. That wrapper is what registers every item a
    /// move touched as known at its new address in the player search controller; without it an item shows as
    /// unsearched the moment it lands in a container (the reported magazine after 'unload magazine'). The same
    /// bookkeeping is repeated here: one dictionary entry per item, no item field and no profile field changes.
    /// </summary>
    internal void MarkOperationItemsKnown(EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        try
        {
            var search = PlayerSearchController;
            if (search == null || operation == null)
            {
                return;
            }

            MarkOperationItemsKnown(search, operation, 0);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not mark the items of operation {operation?.Id} as known: {exception.Message}");
        }
    }

    /// <summary>
    /// [InvDiag] (2026-10-27, T4) Recursive half of <see cref="MarkOperationItemsKnown(EFT.InventoryLogic.Operations.AbstractOperation)"/>.
    /// <para>
    /// The wrapper shape is why this exists. <c>LoadMagOperation</c> and <c>UnloadMagOperation</c> derive from
    /// <c>AbstractMagOperation&lt;TDescriptor&gt;</c> and are constructed with the inner operation (their own
    /// constructor is <c>(ushort id, ItemController controller, AbstractOperation internalOperation)</c>), so the
    /// magazine, the ammo stack and the split that actually move are reachable only through
    /// <c>IParentOperation.InternalOperation</c>. The flat version read the wrapper's own accessors, found nothing and
    /// returned, which left exactly those items unexamined while an ordinary move was fine. This machine dispatches
    /// one such wrapper per unload batch: <c>BaseInventoryController.CustomAmmoUnloader.DoUnloadLoopAsync</c> builds an
    /// <c>UnloadMagOperation</c> and calls <c>Execute</c> with it.
    /// <see cref="MarkOperationItemsSearched"/> and <see cref="CaptureResyncItems"/> already walked the tree; this is
    /// the same walk for the known half.
    /// </para>
    /// </summary>
    private void MarkOperationItemsKnown(IPlayerSearchController search,
        EFT.InventoryLogic.Operations.AbstractOperation operation, int depth)
    {
        if (operation == null || depth > 4)
        {
            return;
        }

        // A wrapper is only a shell around the operation that really touches the items, and the operation that is
        // relayed IS the wrapper (see RunClientOperation).
        if (operation.TryCast<IParentOperation>() is { } parent && parent.InternalOperation != null)
        {
            MarkOperationItemsKnown(search, parent.InternalOperation, depth + 1);
        }

        // One broken accessor of a single operation must not stop the marking of the others.
        try
        {
            MarkOperationOwnItemsKnown(search, operation);

            // Every operation that carries items implements IOneItemOperation (the item it moves) and the
            // two-item ones add ITwoItemOperation (the second item of a swap or transfer). Both are marked
            // with their whole subtree: the magazine that lands in a backpack grid and everything inside it.
            if (operation.TryCast<IOneItemOperation>() is { } one)
            {
                MarkKnownTree(search, one.Item1);
            }

            if (operation.TryCast<ITwoItemOperation>() is { } two)
            {
                MarkKnownTree(search, two.Item2);
            }

            // A split produces one more item instance than the accessors above expose: SplitOperation keeps it in
            // _splitItem. Only the stack reachable through Item1 was registered, so the other one rendered as
            // unexamined. Registering both is idempotent (one dictionary entry per item instance).
            if (operation.TryCast<SplitOperation>() is { } split)
            {
                MarkKnownTree(search, split._splitItem);
            }

            // Second half of the same native bookkeeping: the containers this operation produced, moved or filled
            // inside the player's own inventory. See MarkOperationItemsSearched.
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not mark the items of operation {operation?.Id} as known: {exception.Message}");
        }
    }

    /// <summary>
    /// [InvDiag] (2026-10-27, T4) Registers the operation's own item collection
    /// (<c>AbstractOperation.Items</c> plus <c>IMultipleItemsOperation.Items</c>) as known. That collection is the only
    /// place some operation types keep their items, and the flat accessor read never looked at it.
    /// <para>
    /// Only the item itself is registered here, never its subtree: a container that still holds loot the player has
    /// not seen must not be revealed by this bookkeeping. The items behind <c>IOneItemOperation</c> keep the subtree
    /// registration that the success path always had.
    /// </para>
    /// </summary>
    private void MarkOperationOwnItemsKnown(IPlayerSearchController search,
        EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        var items = operation.Items;
        if (items != null)
        {
            foreach (var item in items)
            {
                MarkItemKnown(search, item);
            }
        }

        if (operation.TryCast<IMultipleItemsOperation>() is { } multiple)
        {
            var multipleItems = multiple.Items;
            if (multipleItems != null)
            {
                foreach (var item in multipleItems)
                {
                    MarkItemKnown(search, item);
                }
            }
        }
    }

    private static void MarkItemKnown(IPlayerSearchController search, Item item)
    {
        if (item == null)
        {
            return;
        }

        try
        {
            // raiseEvents: false - the same reason MarkKnownTree uses it: an address entry is what the views read,
            // a "found item" event for something the player only moved would be a visible side effect.
            search.SetItemAsKnown(item, false);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[InvDiag] could not mark item {item.Id} as known: {exception.Message}");
        }
    }

    /// <summary>
    /// (2026-10-21) THE REAL APPLICATION PATH of a relayed client operation, in one call: the native
    /// <c>InventoryController.Execute</c> wrapper - the one <see cref="Execute"/> replaces - is what registers
    /// every item a move touched as known and every container it touched as searched. A relayed operation runs
    /// through <c>Operation.ExecuteWithoutDispose</c>, so none of that bookkeeping happened, and the items a
    /// successful operation produced came back as "unexamined / unsearched" the next time the inventory was
    /// opened. Both halves are replayed here and the result is reported per operation, before and after, so the
    /// log answers "did the registration reach the item the player saw" without a second run.
    /// <para>
    /// Nothing is relaxed by the report: a container whose content is still unknown stays unsearched
    /// (<see cref="MarkContainerSearched"/>), which is what keeps the looting mechanic intact.
    /// </para>
    /// </summary>
    /// <returns>A one-line description of the operation and both counts, for the caller's log line.</returns>
    internal string ApplyRelayedOperationBookkeeping(EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        var name = operation == null ? "null" : operation.GetIl2CppType().Name;
        try
        {
            var search = PlayerSearchController;
            if (search == null || operation == null)
            {
                return $"op={name} skipped={(search == null ? "no-search-controller" : "no-operation")}";
            }

            var items = CollectOperationItems(operation);
            var knownBefore = CountKnown(search, items);
            var searchedBefore = CountSearched(search, items);

            MarkOperationItemsKnown(operation);
            MarkOperationItemsSearched(search, operation, 0);

            var knownAfter = CountKnown(search, items);
            var searchedAfter = CountSearched(search, items);

            // [InvDiag] (T4) `wrapped` names the shape the flat accessor read could not see: a wrapper operation
            // (load/unload magazine) keeps the items that really move in IParentOperation.InternalOperation.
            // "op=UnloadMagOperation wrapped=True items=4 known=4->4" is therefore readable as "the wrapper was
            // walked and its inner items were registered"; wrapped=False on an operation that still reports
            // known=0 sends the search somewhere else (host rejection / resync) instead of back into this method.
            var wrapped = operation.TryCast<IParentOperation>() is { } parent && parent.InternalOperation != null;
            // The counts of a wrapper cover only the wrapper's own accessors (scope=outer-only): the items that
            // really move sit one level deeper, so for it the numbers are a floor, not a verdict.
            return $"op={name} id={operation.Id} items={items.Count} wrapped={wrapped}" +
                $"{(wrapped ? " scope=outer-only" : string.Empty)} " +
                $"known={knownBefore}->{knownAfter} containers={searchedBefore}->{searchedAfter}";
        }
        catch (Exception exception)
        {
            return $"op={name} failed={exception.GetType().Name}:{exception.Message}";
        }
    }

    /// <summary>The items an operation touches, as one flat list of the whole subtree of each accessor.</summary>
    private static System.Collections.Generic.List<Item> CollectOperationItems(
        EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        var items = new System.Collections.Generic.List<Item>();
        if (operation == null) return items;
        try
        {
            var own = operation.Items;
            if (own != null)
            {
                foreach (var item in own)
                {
                    if (item != null) items.Add(item);
                }
            }
        }
        catch (Exception) { }

        try
        {
            if (operation.TryCast<IOneItemOperation>() is { } one) AddTree(items, one.Item1);
            if (operation.TryCast<ITwoItemOperation>() is { } two) AddTree(items, two.Item2);
            if (operation.TryCast<SplitOperation>() is { } split) AddTree(items, split._splitItem);
        }
        catch (Exception) { }

        return items;
    }

    private static void AddTree(System.Collections.Generic.List<Item> items, Item item)
    {
        if (item == null) return;
        try
        {
            foreach (var candidate in item.GetAllItems())
            {
                if (candidate != null) items.Add(candidate);
            }
        }
        catch (Exception) { }
    }

    private static int CountKnown(IPlayerSearchController search, System.Collections.Generic.List<Item> items)
    {
        var known = 0;
        for (var i = 0; i < items.Count; i++)
        {
            try
            {
                if (search.IsItemKnown(items[i], items[i].Parent)) known++;
            }
            catch (Exception) { }
        }

        return known;
    }

    private static int CountSearched(IPlayerSearchController search, System.Collections.Generic.List<Item> items)
    {
        var searched = 0;
        for (var i = 0; i < items.Count; i++)
        {
            try
            {
                if (items[i].TryCast<SearchableItem>() is { } container && search.IsSearched(container)) searched++;
            }
            catch (Exception) { }
        }

        return searched;
    }

    private static void MarkKnownTree(IPlayerSearchController search, Item item)
    {
        if (item == null)
        {
            return;
        }

        foreach (var candidate in item.GetAllItems())
        {
            if (candidate != null)
            {
                // raiseEvents: false - the address entry is what the UI reads; a 'found item' event for
                // an item the player is only moving would be a visible side effect of this bookkeeping.
                search.SetItemAsKnown(candidate, false);
            }
        }
    }

    /// <summary>
    /// Registers the containers of a relayed operation as searched in the client search controller.
    /// <para>
    /// The client builds its controller with <c>new ActiveSearchController(profile, this)</c> (constructor above),
    /// and the native <c>PlayerSearchController</c> constructor only uncovers the containers that exist inside
    /// <c>profile.Inventory.Equipment</c> at that moment. Every container the player later produces or moves -
    /// the backpack a weapon is stored in, the rig a reloaded magazine lands in, a vest picked back up - is
    /// therefore missing from the controller's searched set, and the native
    /// <c>ISearchController.IsSearched</c> that the inventory views and
    /// <c>Player.InventoryController.GetAcceptableItemsNonAlloc</c> read reports it as unsearched. The native
    /// <c>InventoryController.Execute</c> wrapper maintains that set; a relayed operation runs through
    /// <c>ExecuteWithoutDispose</c> and bypasses it, so the bookkeeping is repeated here.
    /// </para>
    /// <para>
    /// Only containers can be registered: the native entry point is
    /// <c>SetItemAsSearched&lt;TItem&gt;(TItem) where TItem : SearchableItem</c>, and in 1.2.0.47965 only
    /// Backpack/Vest/Pockets/LootContainer/MobContainer derive from <c>SearchableItem</c> (magazines, weapons and
    /// mods do not, they are covered by the knowledge registration in <see cref="MarkOperationItemsKnown"/>).
    /// </para>
    /// </summary>

    /// <summary>
    /// Recursive half of <see cref="MarkOperationItemsSearched(AbstractOperation)"/>.
    /// <para>
    /// A load/unload magazine operation is only a wrapper: <c>IParentOperation.InternalOperation</c> holds the move
    /// and split operations that actually touch items, and those are the ones the accessors below can read. Every
    /// level is therefore visited, and <c>AbstractOperation.Items</c> - the operation's own item collection - is read
    /// instead of guessing which accessor a given operation type implements.
    /// </para>
    /// </summary>
    private void MarkOperationItemsSearched(IPlayerSearchController search,
        EFT.InventoryLogic.Operations.AbstractOperation operation, int depth)
    {
        if (operation == null || depth > 4)
        {
            return;
        }

        if (operation.TryCast<IParentOperation>() is { } parent && parent.InternalOperation != null)
        {
            MarkOperationItemsSearched(search, parent.InternalOperation, depth + 1);
        }

        // One broken accessor of a single operation must not stop the marking of the others.
        try
        {
            MarkOperationContainersSearched(search, operation);

            if (operation.TryCast<IOneItemOperation>() is { } one)
            {
                // The item itself, when the operation produced or moved a container that was already ours.
                if (IsOwnItemAddress(one.From1))
                {
                    MarkSearchedTree(search, one.Item1);
                }

                // The container that received the item. This is the entry that matters for a magazine, a weapon or
                // a split-off stack: they are not containers themselves, but the rig/backpack they landed in is,
                // and its searched state is what the inventory views gate the content display on.
                MarkReceivingContainerSearched(search, one.To1);
            }

            if (operation.TryCast<ITwoItemOperation>() is { } two)
            {
                if (IsOwnItemAddress(two.From2))
                {
                    MarkSearchedTree(search, two.Item2);
                }

                MarkReceivingContainerSearched(search, two.To2);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not read the items of operation {operation.Id} ({operation.GetIl2CppType().Name}): {exception.Message}");
        }
    }

    /// <summary>Marks every container of the operation's own item collection that belongs to this player's inventory.</summary>
    private void MarkOperationContainersSearched(IPlayerSearchController search,
        EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        var items = operation.Items;
        if (items != null)
        {
            foreach (var item in items)
            {
                MarkOwnContainerSearched(search, item);
            }
        }

        if (operation.TryCast<IMultipleItemsOperation>() is { } multiple)
        {
            var multipleItems = multiple.Items;
            if (multipleItems != null)
            {
                foreach (var item in multipleItems)
                {
                    MarkOwnContainerSearched(search, item);
                }
            }
        }
    }

    /// <summary>
    /// Marks one item of an operation as searched when it is a container that currently sits inside this player's
    /// own inventory. The content check in <see cref="MarkContainerSearched"/> keeps a container that still holds
    /// unseen items - the loot the player has to search first - untouched.
    /// </summary>
    private void MarkOwnContainerSearched(IPlayerSearchController search, Item item)
    {
        if (item == null || item.TryCast<SearchableItem>() is not { } searchable)
        {
            return;
        }

        try
        {
            if (!TryFindOwnSearchableContainer(searchable.CurrentAddress, out _))
            {
                return;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not resolve the container {searchable.Id}: {exception.Message}");
            return;
        }

        MarkContainerSearched(search, searchable);
    }

    /// <summary>
    /// Registers a container of the player's own inventory as searched, after making sure it does not still hold
    /// items the player has never seen. That guard is what keeps the looting mechanic intact: a container that
    /// still has unknown content is the loot container the player has to search first, and it is left untouched.
    /// </summary>
    private void MarkReceivingContainerSearched(IPlayerSearchController search, ItemAddress destination)
    {
        if (destination == null || !TryFindOwnSearchableContainer(destination, out var container) || container == null)
        {
            return;
        }

        MarkContainerSearched(search, container);
    }

    private void MarkSearchedTree(IPlayerSearchController search, Item item)
    {
        if (item == null)
        {
            return;
        }

        foreach (var candidate in item.GetAllItems())
        {
            if (candidate != null && candidate.TryCast<SearchableItem>() is { } searchable)
            {
                MarkContainerSearched(search, searchable);
            }
        }
    }

    private void MarkContainerSearched(IPlayerSearchController search, SearchableItem container)
    {
        if (container == null)
        {
            return;
        }

        try
        {
            // A container whose content is not fully known yet must stay unsearched, whatever produced it.
            if (search.ContainsUnknownItems(container))
            {
                return;
            }

            search.SetItemAsSearched<SearchableItem>(container);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not mark container {container.Id} as searched: {exception.Message}");
        }
    }

    /// <summary>
    /// Whether the address belongs to this player's own inventory (equipment, stash, quest items, sorting table)
    /// instead of a ground, loot or corpse container. Items coming from outside must keep their unexamined state.
    /// </summary>
    private bool IsOwnItemAddress(ItemAddress address)
    {
        return TryFindOwnSearchableContainer(address, out _);
    }

    /// <summary>
    /// Walks the address chain upwards until it reaches one of this player's inventory roots. Returns false - and
    /// no container - for anything that is not part of the player's own tree. The walk is bounded because a broken
    /// address chain must never spin the main thread.
    /// </summary>
    private bool TryFindOwnSearchableContainer(ItemAddress address, out SearchableItem container)
    {
        container = null;

        try
        {
            var inventory = Inventory;
            if (address == null || inventory == null)
            {
                return false;
            }

            var current = address.Container;
            for (var depth = 0; current != null && depth < 32; depth++)
            {
                var owner = current.ParentItem;
                if (owner == null)
                {
                    break;
                }

                if (IsOwnInventoryRoot(inventory, owner))
                {
                    return true;
                }

                if (container == null && owner.TryCast<SearchableItem>() is { } searchable)
                {
                    container = searchable;
                }

                var parentAddress = owner.CurrentAddress;
                if (parentAddress == null)
                {
                    break;
                }

                current = parentAddress.Container;
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"{ID} - could not resolve an inventory address of an operation item: {exception.Message}");
        }

        container = null;
        return false;
    }

    private static bool IsOwnInventoryRoot(EFT.InventoryLogic.Inventory inventory, Item item)
    {
        return IsSameItem(item, inventory.Equipment)
            || IsSameItem(item, inventory.Stash)
            || IsSameItem(item, inventory.QuestRaidItems)
            || IsSameItem(item, inventory.QuestStashItems)
            || IsSameItem(item, inventory.SortingTable);
    }

    private static bool IsSameItem(Item left, Item right)
    {
        return left != null && right != null && left.Pointer == right.Pointer;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Authoritative resync: the host rejected the operation and sent its own inventory back as a descriptor. The
    // client rebuilds every Item from that descriptor, so the search controller entries (instance and address based)
    // do not survive the rebuild. Everything below captures the rejected operation's items before the rebuild, decides
    // whether the local instances can be kept at all, and re-registers the bookkeeping afterwards.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One item of the rejected operation, captured before the authoritative snapshot replaces the local item graph.
    /// The instance itself is gone after a rebuild, so only its id and the two search flags survive.
    /// </summary>
    internal readonly struct ResyncItemSnapshot
    {
        public readonly string Id;
        public readonly IntPtr Pointer;
        public readonly bool KnownBefore;
        public readonly bool SearchedBefore;

        internal ResyncItemSnapshot(string id, IntPtr pointer, bool knownBefore, bool searchedBefore)
        {
            Id = id;
            Pointer = pointer;
            KnownBefore = knownBefore;
            SearchedBefore = searchedBefore;
        }
    }

    /// <summary>Collects the items the rejected operation touched, before <c>Operation.Dispose()</c> and the rebuild.</summary>
    internal System.Collections.Generic.List<ResyncItemSnapshot> CaptureResyncItems(
        EFT.InventoryLogic.Operations.AbstractOperation operation)
    {
        var captured = new System.Collections.Generic.List<ResyncItemSnapshot>();
        try
        {
            var search = PlayerSearchController;
            if (search == null || operation == null)
            {
                return captured;
            }

            CaptureResyncItems(search, operation, captured, new System.Collections.Generic.HashSet<string>(), 0);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op=capture item=none stage=capture error={exception.Message}");
        }

        return captured;
    }

    private void CaptureResyncItems(IPlayerSearchController search,
        EFT.InventoryLogic.Operations.AbstractOperation operation,
        System.Collections.Generic.List<ResyncItemSnapshot> captured,
        System.Collections.Generic.HashSet<string> seen, int depth)
    {
        if (operation == null || depth > 4)
        {
            return;
        }

        if (operation.TryCast<IParentOperation>() is { } parent && parent.InternalOperation != null)
        {
            CaptureResyncItems(search, parent.InternalOperation, captured, seen, depth + 1);
        }

        var items = operation.Items;
        if (items != null)
        {
            foreach (var item in items)
            {
                CaptureResyncItem(search, captured, seen, item);
            }
        }

        if (operation.TryCast<IMultipleItemsOperation>() is { } multiple)
        {
            var multipleItems = multiple.Items;
            if (multipleItems != null)
            {
                foreach (var item in multipleItems)
                {
                    CaptureResyncItem(search, captured, seen, item);
                }
            }
        }

        if (operation.TryCast<IOneItemOperation>() is { } one)
        {
            CaptureResyncItem(search, captured, seen, one.Item1);
        }

        if (operation.TryCast<ITwoItemOperation>() is { } two)
        {
            CaptureResyncItem(search, captured, seen, two.Item2);
        }
    }

    private void CaptureResyncItem(IPlayerSearchController search,
        System.Collections.Generic.List<ResyncItemSnapshot> captured,
        System.Collections.Generic.HashSet<string> seen, Item item)
    {
        if (item == null)
        {
            return;
        }

        string id;
        try
        {
            id = item.Id;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op=capture item=unknown stage=capture-id error={exception.Message}");
            return;
        }

        if (string.IsNullOrEmpty(id) || !seen.Add(id))
        {
            return;
        }

        var knownBefore = false;
        var searchedBefore = false;
        try
        {
            knownBefore = search.IsItemKnown(item, item.Parent);
            if (item.TryCast<SearchableItem>() is { } searchable)
            {
                searchedBefore = search.IsSearched(searchable);
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op=capture item={id} stage=capture-state error={exception.Message}");
        }

        captured.Add(new ResyncItemSnapshot(id, item.Pointer, knownBefore, searchedBefore));
    }

    /// <summary>
    /// Decides whether the authoritative snapshot may be ignored in favour of the local item graph.
    /// <para>
    /// Keeping the instances preserves the controller's per-instance search entries and the open inventory views, but
    /// it is only safe when the snapshot is provably identical to the local tree (same item ids, same address hash -
    /// <c>ItemAddress.GetHashSum</c> covers grid position and slot - and the same stack count) <b>and</b> the rejected
    /// operation can only have changed placement and counts. Anything else rebuilds, because a difference this
    /// comparison cannot see (durability, installed mods, ...) would otherwise stay divergent.
    /// </para>
    /// </summary>
    internal bool TryKeepLocalInstances(Inventory snapshot,
        EFT.InventoryLogic.Operations.AbstractOperation operation, out string reason)
    {
        try
        {
            var inventory = Inventory;
            if (snapshot == null)
            {
                reason = "no-snapshot";
                return false;
            }

            if (inventory == null)
            {
                reason = "no-local-inventory";
                return false;
            }

            if (operation == null)
            {
                reason = "no-operation";
                return false;
            }

            if (!IsPlacementOnlyOperation(operation, 0))
            {
                reason = "operation-not-placement-only";
                return false;
            }

            if (!FingerprintsEqual(inventory, snapshot))
            {
                reason = "structure-differs";
                return false;
            }

            reason = "identical-structure";
            return true;
        }
        catch (Exception exception)
        {
            reason = "keep-check-failed:" + exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Whether the operation only moves items around or changes stack counts. Those are the only changes the
    /// fingerprint in <see cref="FingerprintsEqual"/> can see, so only they may keep the local instances.
    /// </summary>
    private static bool IsPlacementOnlyOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, int depth)
    {
        if (operation == null || depth > 4)
        {
            return false;
        }

        if (operation.TryCast<IParentOperation>() is { } parent && parent.InternalOperation != null)
        {
            return IsPlacementOnlyOperation(parent.InternalOperation, depth + 1);
        }

        return operation.TryCast<MoveOperation>() != null
            || operation.TryCast<SwapOperation>() != null
            || operation.TryCast<MergeOperation>() != null
            || operation.TryCast<SplitOperation>() != null
            || operation.TryCast<TransferOperation>() != null
            || operation.TryCast<ThrowOperation>() != null
            || operation.TryCast<RemoveOperation>() != null
            || operation.TryCast<LoadMagOperation>() != null
            || operation.TryCast<UnloadMagOperation>() != null;
    }

    /// <summary>Item id to "address hash:stack count:template" for the whole player inventory of one tree.</summary>
    private static System.Collections.Generic.Dictionary<string, string> BuildInventoryFingerprint(Inventory inventory)
    {
        var fingerprint = new System.Collections.Generic.Dictionary<string, string>();
        var items = inventory.GetPlayerItems(EPlayerItems.All);
        if (items == null)
        {
            return fingerprint;
        }

        foreach (var item in items)
        {
            if (item == null)
            {
                continue;
            }

            var address = item.Parent;
            var addressHash = address == null ? 0 : address.GetHashSum();
            fingerprint[item.Id] = string.Concat(addressHash.ToString(), ":", item.StackObjectsCount.ToString(),
                ":", item.StringTemplateId);
        }

        return fingerprint;
    }

    private static bool FingerprintsEqual(Inventory left, Inventory right)
    {
        var first = BuildInventoryFingerprint(left);
        var second = BuildInventoryFingerprint(right);
        if (first.Count != second.Count)
        {
            return false;
        }

        foreach (var pair in first)
        {
            if (!second.TryGetValue(pair.Key, out var value) || value != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Re-registers the captured operation items in the (possibly rebuilt) inventory: each item is marked known at
    /// its current address, and a container that was searched before the resync is marked searched again. Contents
    /// are deliberately not touched (see the comment at the call). Writes one <c>[SyncDiag]</c> line per item so a
    /// raid log shows whether the ids survived the rebuild.
    /// </summary>
    internal void RestoreOperationItemsAfterResync(System.Collections.Generic.List<ResyncItemSnapshot> captured,
        bool keptLocalInstances, ushort operationId)
    {
        if (captured == null || captured.Count == 0)
        {
            return;
        }

        try
        {
            var search = PlayerSearchController;
            if (search == null)
            {
                return;
            }

            var lookup = new System.Collections.Generic.Dictionary<string, Item>();
            var items = Inventory?.GetPlayerItems(EPlayerItems.All);
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item != null && !string.IsNullOrEmpty(item.Id))
                    {
                        lookup[item.Id] = item;
                    }
                }
            }

            foreach (var snapshot in captured)
            {
                var found = lookup.TryGetValue(snapshot.Id, out var item) && item != null;
                var sameInstance = found && item.Pointer == snapshot.Pointer;
                var knownAfter = false;

                if (found)
                {
                    try
                    {
                        // Only the item itself is registered: its contents were either restored by
                        // ReplaceInventoryFromNetwork (everything that was known before) or must stay unknown -
                        // descending into a freshly picked up container here would reveal loot that the player
                        // has not searched yet.
                        search.SetItemAsKnown(item, false);
                        // [InvDiag] (2026-10-27, T4) The reject path restored the searched flag only when the
                        // container had been searched BEFORE the resync. CaptureResyncItems runs after the local
                        // execution but before any marking (that only happens on the success path), so for a
                        // container this operation just moved inside the player's own inventory SearchedBefore is
                        // structurally always false - the rebuilt tree then showed it "unsearched" while the very
                        // same operation through the success path comes back searched. A container that sits in the
                        // player's own inventory is therefore marked exactly like the success path marks it;
                        // MarkContainerSearched still refuses one that holds contents the player has not seen, so
                        // the looting mechanic is untouched.
                        if (item.TryCast<SearchableItem>() is { } searchable
                            && (snapshot.SearchedBefore || IsOwnItemAddress(item.CurrentAddress)))
                        {
                            MarkContainerSearched(search, searchable);
                        }

                        knownAfter = search.IsItemKnown(item, item.Parent);
                    }
                    catch (Exception exception)
                    {
                        FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op={operationId} item={snapshot.Id} " +
                            $"stage=restore error={exception.Message}");
                    }
                }

                FikaGlobals.LogInfo($"[SyncDiag] op={operationId} item={snapshot.Id} sameId={found} " +
                    $"sameInstance={sameInstance} knownBefore={snapshot.KnownBefore} knownAfter={knownAfter} " +
                    $"searchedBefore={snapshot.SearchedBefore} keptInstances={keptLocalInstances}");
            }
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op={operationId} item=none stage=restore-all " +
                $"error={exception.Message}");
        }
    }

    /// <summary>Item count of a decoded snapshot, for the resync diagnostic at the rebuild site.</summary>
    internal static int CountSnapshotItems(Inventory inventory)
    {
        try
        {
            var items = inventory?.GetPlayerItems(EPlayerItems.All);
            if (items == null)
            {
                return 0;
            }

            var count = 0;
            foreach (var item in items)
            {
                if (item != null)
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"[SyncDiag] item-mark-failed op=descriptor item=none stage=count error={exception.Message}");
            return -1;
        }
    }
}
