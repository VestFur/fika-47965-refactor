using System;
using EFT.InventoryLogic;

namespace Fika.Core.Main.Utils;

internal static class FikaShotInventory
{
    internal static Item PopAndClone(StackSlot cartridges, ItemController controller)
    {
        var result = cartridges.PopToNowhere(controller);
        if (result.Failed)
            throw new InvalidOperationException("Observed shot could not consume ammunition: " + result.Error);

        // SPT 5 FireOperationBase clones the detached ResultItem with the
        // inventory's ID generator. PopToNowhere alone consumes the stack but
        // does not allocate this shot's item ID, unlike PopTo into a chamber.
        return result.Value.ResultItem.CloneItem(controller.Cast<IDatabaseIdGenerator>());
    }
}
