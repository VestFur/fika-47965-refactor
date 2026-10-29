using EFT.BufferZone;
using System;
using System.Reflection;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Lighthouse;

public class BufferZoneController_OnGameTypeSeted_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BufferZoneController)
            .GetMethod(nameof(BufferZoneController.OnGameTypeSeted));
    }

    [PatchPrefix]
    public static void Prefix(ref EGameType gameType)
    {
        if (FikaBackendUtils.IsClient)
        {
            // Let the native initializer own its subscriptions and cleanup while
            // clients consume the host's buffer-zone state as an online peer.
            gameType = EGameType.Online;
        }
    }
}
