using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.Matchmaker;
using EFT.UI;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Bugfixes;

/// <summary>
/// Borrowed from Tyfon's UI Fixes with permission, a patch that fixes a bug if you inspect a player during loading when the controller is instantiated <br/><br/>
/// Source code here: <see href="https://github.com/tyfon7/UIFixes/blob/main/src/Patches/FixPlayerInspectPatch.cs"/>
/// </summary>
[IgnoreAutoPatch]
public class PartyInfoPanel_CG_method_3_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ItemUiContext).GetMethod(nameof(ItemUiContext.ShowPlayerEquipmentWindow));
    }

    [PatchPrefix]
    public static bool Prefix(GroupPlayer groupPlayer, ref PlayerEquipmentWindowContext __result)
    {
        var equipment = groupPlayer.PlayerVisualRepresentation?.Equipment;
        var owner = equipment?.CurrentAddress?.GetOwnerOrNull();
        if (owner?.TryCast<Player.PlayerOwnerInventoryController>() != null)
        {
            // The live raid inventory already owns this equipment. Creating a
            // second inspection controller would detach it from that owner.
            __result = null;
            return false;
        }

        return true;
    }
}
