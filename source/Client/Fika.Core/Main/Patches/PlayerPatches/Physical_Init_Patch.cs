using System.Reflection;
using EFT;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Players;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.PlayerPatches;

public sealed class Physical_Init_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(Physical)
        .GetMethod(nameof(Physical.Init), new[] { typeof(IPlayer) });

    [PatchPostfix]
    public static void Postfix(Physical __instance, IPlayer player)
    {
        // Init creates/resets the stamina state. Apply raid overrides only
        // afterwards, to the same local players and bots as the old subclass.
        if (player?.TryCast<FikaPlayer>() != null)
            FikaClientPhysical.ApplySettings(__instance);
    }
}
