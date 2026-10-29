using System.Reflection;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Overrides;

/// <summary>
/// SPT disables hardware telemetry at HWEcho_Json, but this IL2CPP build's
/// GetMetrics contains the P/Invoke directly and bypasses that detour.
/// Apply SPT's offline behavior at the caller before resolving hwecho.dll.
/// </summary>
internal sealed class HWEcho_GetMetrics_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(HWEcho).GetMethod(nameof(HWEcho.GetMetrics));

    [PatchPrefix]
    private static bool Prefix(ref Il2CppSystem.ValueTuple<string, string> __result)
    {
        __result = new Il2CppSystem.ValueTuple<string, string>(string.Empty, string.Empty);
        return false;
    }
}
