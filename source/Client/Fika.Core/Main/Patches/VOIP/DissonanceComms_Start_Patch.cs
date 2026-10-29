using System.Reflection;
using Dissonance;
using Dissonance.Integrations.MirrorIgnorance;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.VOIP;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.VOIP;

public class DissonanceComms_Start_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools
            .Method(typeof(DissonanceComms), nameof(DissonanceComms.Initialize));
    }

    [PatchPrefix]
    public static bool Prefix(DissonanceComms __instance)
    {
        // Outside a Fika raid, the stock SPT voice scene owns initialization.
        if (FikaBackendUtils.ClientType == EClientType.None) return true;
        var native = __instance.GetComponent<MirrorIgnoranceCommsNetwork>();
        return !__instance.IsStarted && FikaCommsNetwork.Find(native)?.IsActive == true;
    }
}
