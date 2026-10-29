using System.Reflection;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.AbstractGamePatches;

/// <summary>
/// Used to support mods that rely on the <see cref="AbstractGame.InRaid"/> property, which normally casts to <see cref="LocalGame"/>
/// </summary>
internal class AbstractGame_InRaid_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(AbstractGame).GetProperty(nameof(AbstractGame.InRaid)).GetGetMethod();
    }

    [PatchPrefix]
    public static bool Prefix(AbstractGame __instance, ref bool __result)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return true;
        // HeadlessGame is an AbstractGame too, but it still represents an
        // active Fika raid.  Returning false here makes native callers skip
        // raid-only cleanup and reconnect logic on a dedicated host.
        __result = __instance is CoopGame || FikaBackendUtils.IsHeadless || FikaBackendUtils.IsHeadlessGame;
        return false;
    }
}
