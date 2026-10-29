using System.Reflection;
using EFT;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches.LoadingScreen;

public class MapLoadingPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        // The display class ordinal changed with the build (216 on the ported build, 237 on 47888).
        return LoadMapAndDataTargets.Resolve("_LoadMapAndData_b__1");
    }

    [PatchPrefix]
    public static void Postfix(float pr)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return;
        var progress = 0f + (pr * (FikaBackendUtils.IsHeadless ? 50f : 25f)); // headless doesn't cache culling
        LoadingScreenUI.Instance.UpdateAndBroadcast(progress);
    }
}
