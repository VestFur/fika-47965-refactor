using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.PlayerPatches;

/// <summary>
/// This patch stops BSGs dogtag handling as it is poorly executed
/// </summary>
public class Player_SetDogtagInfo_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod(nameof(Player.SetDogtagInfo));
    }

    // Preserve Fika's replicated dogtag details instead of replacing them with
    // the vanilla death packet (the original Mono patch returned immediately).
    [PatchPrefix]
    public static bool Prefix() => FikaBackendUtils.ClientType == EClientType.None;
}
