using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.SynchronizableObjects;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Tripwire;

public class TripwireSynchronizableObject_ActivateGrenade_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TripwireSynchronizableObject)
            .GetMethod(nameof(TripwireSynchronizableObject.ActivateGrenade));
    }

    [PatchPostfix]
    public static void Prefix(EFT.SynchronizableObjects.TripwireSynchronizableObject __instance)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return;
        Singleton<IGameLevel>.Instance.RegisterGrenade(__instance._grenadeInWorld);
    }
}
