using System.Reflection;
using Comfort.Common;
using EFT;
using Fika.Core.Main.HostClasses;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.GameWorldPatches;

/// <summary>
/// Preserves the final network state of a host grenade when SPT raises the
/// native destroy event.  SPT 5 may also queue this state from
/// <c>UnregisterGrenade</c>; the host world de-duplicates delivery by clearing
/// its per-tick queue after it has been flushed.
/// </summary>
public sealed class GameWorld_GrenadeDestroyEvent_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(GameWorld).GetMethod(nameof(GameWorld.GrenadeDestroyEvent));

    [PatchPrefix]
    private static bool Prefix(Throwable grenade)
    {
        if (grenade == null || !grenade.HasNetData ||
            !Singleton<FikaHostGameWorld>.Instantiated)
            return true;

        var hostWorld = Singleton<FikaHostGameWorld>.Instance?.FikaHostWorld;
        if (hostWorld == null)
            return true;

        // SPT 5's native unregister path is authoritative when it has already
        // queued a critical final state.  Avoid adding a second identical
        // packet when the legacy destroy callback arrives afterwards.
        if (!hostWorld.TryQueueGrenadeDestroy(grenade))
            return true;

        return true;
    }
}
