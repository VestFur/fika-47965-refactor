using CommonAssets.Scripts.ArtilleryShelling;
using Fika.Core.Main.Utils;
using System.Reflection;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Artillery;

public class ArtilleryShellingControllerServer_ArtilleryProjectileExplosionEvent_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ArtilleryShellingControllerServer).GetMethod(nameof(ArtilleryShellingControllerServer.ArtilleryProjectileExplosionEvent));
    }

    [PatchPrefix]
    public static void Prefix(ArtilleryShellingControllerServer __instance, ArtilleryProjectileServer serverProjectile)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return;
        __instance.WriteArtilleryExplosionProjectilePacket(serverProjectile);
    }
}
