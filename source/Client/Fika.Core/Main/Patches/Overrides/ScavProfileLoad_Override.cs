using System.Reflection;
using EFT;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Overrides;

internal class ScavProfileLoad_Override : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TarkovApplication).GetMethod(nameof(TarkovApplication.OnGameEnd));
    }

    [PatchPrefix]
    private static void PatchPrefix(ref string profileId, Profile savageProfile, EFT.TarkovApplication __instance)
    {
        // SPT can enter OnGameEnd from a cancelled/failed raid before the
        // secondary Scav profile has been created. Keep the native PMC id in
        // that path instead of dereferencing a missing profile and aborting
        // the result/save coroutine. GameWorld.Dispose can also reset
        // ClientType before this callback, so scope the override to the
        // profile captured by this Fika raid rather than the network state.
        var descriptor = RaidEndProfile.ProfileDescriptor;
        if (__instance?._raidSettings != null &&
            !__instance._raidSettings.IsPmc &&
            savageProfile != null &&
            !string.IsNullOrEmpty(savageProfile.Id) &&
            descriptor != null &&
            string.Equals(descriptor.Id.ToString(), savageProfile.Id, System.StringComparison.Ordinal))
        {
            profileId = savageProfile.Id;
        }
    }
}
