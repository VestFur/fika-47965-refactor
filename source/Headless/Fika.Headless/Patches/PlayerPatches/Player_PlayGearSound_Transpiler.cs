using EFT;
using SPTushonka.Reflection.Patching;
using System.Reflection;

namespace Fika.Headless.Patches.PlayerPatches;

internal class Player_PlayGearSound_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player)
            .GetMethod(nameof(Player.PlayGearSound), [typeof(float), typeof(bool)]);
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}

internal class Player_PlayGearSoundBank_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player)
            .GetMethod(nameof(Player.PlayGearSound), [typeof(SoundBank), typeof(float)]);
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}
