// © 2026 Lacyway All Rights Reserved

using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.BaseClasses;
using Fika.Core.Main.Patches.World;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Packets.Generic;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Fika.Core.Networking.Pooling;
using Diz.LanguageExtensions;
using static EFT.Player;

namespace Fika.Core.Main.BotClasses;

public sealed class BotInventoryController : BaseInventoryController
{

    public BotInventoryController(System.IntPtr pointer) : base(pointer) { }


    public override bool HasDiscardLimits
    {
        get
        {
            return false;
        }
    }
    private readonly FikaBot _fikaBot;
    private readonly BotInventoryOperationHandlerPool _botInventoryOperationHandlerPool;

    public BotInventoryController(Player player, Profile profile, bool examined, MongoID currentId, ushort nextOperationId)
        : base(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<BotInventoryController>(), player, profile, examined, false)
    {
        _fikaBot = player.TryCast<FikaBot>()
            ?? throw new System.InvalidOperationException("Bot inventory controller received a non-Fika player");
        IdSource = currentId;
        _nextOperationId = nextOperationId;
        PlayerSearchController = new BotSearchController(profile);
        _botInventoryOperationHandlerPool = BotInventoryOperationHandlerPool.Instance;
    }

    public override IPlayerSearchController PlayerSearchController { get; }

    public override void CallMalfunctionRepaired(Weapon weapon)
    {
        // Do nothing
    }

    /// <summary>
    /// Gets an inventory handler
    /// </summary>
    /// <returns>A pooled handler</returns>
    public BotInventoryOperationHandler GetHandler()
    {
        return _botInventoryOperationHandlerPool.Get();
    }

    /// <summary>
    /// Returns a handler
    /// </summary>
    /// <param name="handler">The handler to return</param>
    public void ReturnHandler(BotInventoryOperationHandler handler)
    {
        _botInventoryOperationHandlerPool.ReturnHandler(handler);
    }

    public override void Execute(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
#if DEBUG
        FikaGlobals.LogInfo($"Sending bot operation {operation.GetType()} from {_fikaBot.Profile.Nickname}");
#endif
        _fikaBot.PacketSender.NetworkManager.SendGenericPacket(EGenericSubPacketType.InventoryOperation,
            InventoryPacket.FromValue(_fikaBot.NetId, operation), true);
        MainThreadDispatcher.RunAsync(() => HandleOperation(operation, callback)).Forget();
    }

    /// <summary>
    /// Override to not replicate the tripwire
    /// </summary>
    public override void PlantTripwire(ThrowWeap grenade, PlantingKit plantingKit, Vector3 fromPosition, Vector3 toPosition, Callback callback = null)
    {
        // [PlantDiag] (analysis/plant-explosive-door): the native plant result always carries a
        // DiscardResult for the grenade, so when the AI hands over a grenade that is no longer
        // attached to any container, ItemManipulator.Discard dereferences Item.Parent and throws
        // ("Trying to get parent of an item that doesn't have a parent: ...") out of
        // PlantTripwireResult.Execute - one IL2CPP exception per attempt, five of them in the
        // 2026-10-12 raid. The operation cannot succeed for a detached item, so it is refused
        // before it is built and the refusal is logged once instead.
        if (!PlantDiag.HasParent(grenade) || (plantingKit != null && !PlantDiag.HasParent(plantingKit)))
        {
            
            return;
        }

        try
        {
            var gstruct = ItemManipulator.SimulatePlantTripwire(this, grenade, plantingKit);
            if (!gstruct.Failed)
            {
                MainThreadDispatcher.RunAsync(() => HandleOperation(new PlantTripwireOperation(GetAndIncrementNextOperationId(), this, gstruct.Value, fromPosition, toPosition, _fikaBot), callback))
                .Forget();
                return;
            }
            callback?.Invoke(gstruct.ToResult());
        }
        catch (System.Exception exception)
        {
            
        }
    }

    private async Task HandleOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        if (_fikaBot.HealthController.IsAlive)
        {
            await Task.Yield();
        }
        RunBotOperation(operation, callback);
    }

    private void RunBotOperation(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
    {
        var handler = _botInventoryOperationHandlerPool.Get();
        handler.Set(this, operation, callback);
        try
        {
            if (CanExecute(operation))
            {
                handler.Operation.Execute((Callback)handler.HandleResult);
                return;
            }
            handler.Operation.Dispose();
            handler.Callback?.Fail($"Can't execute {handler.Operation}", 1);
        }
        catch (System.Exception exception)
        {
            // [PlantDiag] (analysis/plant-explosive-door): a native operation can throw out of
            // Execute (PlantTripwireResult.Execute does it for a detached grenade). Failing the
            // callback keeps the AI bookkeeping intact and stops the exception from escaping into
            // Fika.Core.TaskExtensions.Forget as a log storm.
            try
            {
                handler.Operation.Dispose();
            }
            catch
            {
                // Disposing twice is not worth another exception.
            }
            handler.Callback?.Fail($"{operation?.GetType().Name} threw: {exception.Message}", 1);
        }
        finally
        {
            _botInventoryOperationHandlerPool.ReturnHandler(handler);
        }
    }

    public override SearchContentOperation CreateSearchOperation(SearchableItem item)
    {
        return new SinglePlayerSearchContentOperation(GetAndIncrementNextOperationId(), this, PlayerSearchController, Profile, item);
    }
}
