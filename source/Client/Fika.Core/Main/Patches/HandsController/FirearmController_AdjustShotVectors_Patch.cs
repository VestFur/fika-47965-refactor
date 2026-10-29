using System.Reflection;
using EFT;
using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.HandsController;

/// <summary>
/// Observed (remote/bot) firearm controllers must not adjust shot vectors locally - the shot the
/// shooter replicates is already in its final position. Upstream Fika expressed that as an empty
/// override on the injected <see cref="ObservedFirearmController"/>.
///
/// Every method of an injected type gets an Il2CppInterop reverse-P/Invoke entry point (an
/// "Invoker_&lt;signature&gt;" dynamic method behind an InvokerDelegateMetadataV29 stub, and a
/// "Trampoline_&lt;signature&gt;&lt;type&gt;&lt;method&gt;" dynamic method behind the method pointer). Those entries are
/// JIT compiled lazily, on the first native call, i.e. inside the native callout that reaches them;
/// a compile that the JIT refuses there is a fatal 0x80131506 fail fast, not a managed exception
/// (see analysis/adjust-shot-vectors/REPORT.md). No override means no injected entry point for
/// this method at all.
///
/// The same behaviour is therefore kept as a prefix on the native method: the native
/// implementation stays in place for every other controller (local player, AI without Fika's
/// observed controller) and is skipped only for the observed one - which is exactly what the empty
/// override did. The prefix deliberately declares only __instance, so the patch's own callable
/// shape carries no struct parameters either.
/// </summary>
public sealed class FirearmController_AdjustShotVectors_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(
        typeof(Player.FirearmController), nameof(Player.FirearmController.AdjustShotVectors),
        [typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType()]);

    [PatchPrefix]
    private static bool Prefix(Player.FirearmController __instance)
    {
        if (__instance is not ObservedFirearmController) return true;
        
        return false;
    }
}
