using EFT.Rendering.Clouds;
using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.DestroyGraphics;

// Keep CloudController alive: WeatherController.ControlClouds still uses it on
// outdoor maps, even when the Headless host does not render the sky.
public class CloudController_UpdateAmbient_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(CloudController).GetMethod(nameof(CloudController.UpdateAmbient));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
