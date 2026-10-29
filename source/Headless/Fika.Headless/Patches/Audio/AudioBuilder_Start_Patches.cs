using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.Audio;

internal class SpatialAudioBuilder_Start_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SpatialAudioBuilder).GetMethod(nameof(SpatialAudioBuilder.Start));
    }

    [PatchPrefix]
    public static bool Prefix(ref AudioHandle __result)
    {
        __result = AudioHandle.Invalid;
        return false;
    }
}

internal class NonspatialAudioBuilder_Start_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(NonspatialAudioBuilder).GetMethod(nameof(NonspatialAudioBuilder.Start));
    }

    [PatchPrefix]
    public static bool Prefix(ref AudioHandle __result)
    {
        __result = AudioHandle.Invalid;
        return false;
    }
}
