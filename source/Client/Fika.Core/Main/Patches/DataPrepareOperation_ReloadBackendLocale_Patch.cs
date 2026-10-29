using EFT;
using System.Reflection;
using System.Threading.Tasks;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches;

internal class DataPrepareOperation_ReloadBackendLocale_Patch : ModulePatch
{
    private static bool _hasBeenSet = false;

    protected override MethodBase GetTargetMethod()
    {
        // SPT 5's startup prefetch calls the shared loader directly, bypassing
        // ClientBackendSession.LoadLocale. Observe the actual locale task.
        return typeof(LocalizationLoader)
            .GetMethod(nameof(LocalizationLoader.LoadLocale));
    }

    [PatchPostfix]
    public static void Postfix(Il2CppSystem.Threading.Tasks.Task __result)
    {
        if (!_hasBeenSet)
        {
            FikaPlugin.Instance.WaitForLocales(__result);
            _hasBeenSet = true;
        }
    }
}
