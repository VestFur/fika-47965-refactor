using System.Reflection;
using EFT.UI;
using SPTushonka.Reflection.Patching;
using TMPro;
using UnityEngine.UI;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.UI.Patches;

public class ChangeGameModeButton_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ChangeGameModeButton).GetMethod(nameof(ChangeGameModeButton.Show));
    }

    [PatchPostfix]
    private static void Postfix(ChangeGameModeButton __instance)
    {
        __instance._buttonLabel.SetText("PvE");
        __instance._buttonLabelHover.SetText("PvE");
        __instance._buttonDescription.SetText($"Fika will always be {ColorizeText(EColor.BLUE, "PvE")}");
        __instance._buttonDescriptionIcon.gameObject.SetActive(false);
    }
}
