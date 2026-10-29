using System.Reflection;
using Comfort.Common;
using SPTushonka.Reflection.Patching;
using Il2CppSystems.Effects;

namespace Fika.Core.Main.Patches.Muzzle;

/// <summary>
/// This patch skips a LINQ allocation during <c>MuzzleManager.IMuzzleParticlePivot.Play(EMuzzleParticlePivot, Transform)</c>
/// </summary>
/// <remarks>Allocation found by <b>ifp</b></remarks>
/// <remarks>
/// Deliberately not installed on EFT 1.2.0.47888. The target is an explicit interface implementation and
/// Il2CppInterop 1.6.7 generates its stub against <c>NativeInterfaceMethodInfoPtr_IMuzzleParticlePivot_Play_...</c>;
/// <c>Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod</c> scans the stub body for a field
/// starting with <c>NativeMethodInfoPtr_</c> and therefore finds nothing, so
/// <c>Il2CppDetourMethodPatcher.IsValid</c> stays false and Harmony falls back to ManagedMethodPatcher (patching
/// the CLR stub, which native code never calls). That is exactly what Fika.Core's PatchManager refused with
/// "requires an IL2CPP detour; resolved HarmonyLib.Public.Patching.ManagedMethodPatcher". The same optimization
/// ships in SPTushonka.Custom (<c>MemoryImprovements.MuzzleManagerPlayPatch</c>) with an identical prefix body; a
/// second install would not change runtime behavior either. Nothing else calls this method, so the only effect of
/// the missing patch is one LINQ allocation per muzzle play. Drop [IgnoreAutoPatch] once the build (or a newer
/// Il2CppInterop) exposes the implementation behind a detourable <c>NativeMethodInfoPtr_</c> field, for example
/// <c>MuzzleManager.Shot(bool, float)</c> as the non-interface caller.
/// </remarks>
[IgnoreAutoPatch]
public sealed class MuzzleManager_Play_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MuzzleManager).GetMethod("IMuzzleParticlePivot.Play", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    }

    [PatchPrefix]
    public static bool Prefix(EMuzzleParticlePivot pivot, Transform pTransform)
    {
        var instance = Singleton<Effects>.Instance;
        var commonSystems = instance.MuzzleEffect.CommonSystems;
        instance.TryAddToMBOITParticleManager(commonSystems);
        for (var i = 0; i < commonSystems.Length; i++)
        {
            var container = commonSystems[i];
            if (container.Pivot == pivot)
            {
                var rootParticleSystem = container.RootParticleSystem;
                var t = rootParticleSystem.transform;
                t.SetPositionAndRotation(pTransform.position, pTransform.rotation);

                rootParticleSystem.Stop(true);
                rootParticleSystem.Play(true);
                break;
            }
        }

        return false;
    }
}
