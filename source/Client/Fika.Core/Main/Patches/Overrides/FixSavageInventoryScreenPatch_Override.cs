using System;
using System.Linq;
using System.Reflection;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Common.Http;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Overrides;

internal static class RaidEndProfile
{
    public static ProfileDescriptor ProfileDescriptor { get; private set; }

    internal static void Capture(Profile profile)
    {
        // Failed raid setup can reach CoopGame.Stop without a profile that
        // should be offered to the scav result screen.  Do not leave the
        // previous raid's descriptor alive across that path.
        if (profile == null)
        {
            ProfileDescriptor = null;
            return;
        }

        ProfileDescriptor = new ProfileDescriptor(profile, FikaGlobals.SearchControllerSerializer);
        // The raid-end descriptor is posted to /raid/profile/scavsave; the local
        // profile's group still holds Fika's "Fika" sentinel at this point and must
        // not end up in the saved profile.
        FikaGlobals.StripFikaGroupSentinel(ProfileDescriptor);
    }

    internal static void Clear() => ProfileDescriptor = null;
}
/// <summary>
/// Get profile from other patch (GetProfileAtEndOfRaidPatch)
/// if our profile is savage Create new Session.AllProfiles and pass in our own profile to allow us to use the ScavengerInventoryScreen
/// </summary>
public class FixSavageInventoryScreenPatch_Override : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(SessionResultShowOperation), nameof(SessionResultShowOperation.Init));
    }

    [PatchPrefix]
    public static void PatchPrefix(EFT.SessionResultShowOperation __instance)
    {
        // GameWorld.Dispose releases the network manager before the native
        // result operation starts, resetting ClientType to None. The captured
        // raid-end descriptor is the marker for this Fika result screen.
        var descriptor = RaidEndProfile.ProfileDescriptor;
        if (descriptor == null || __instance?._session == null)
        {
            return;
        }

        // Consume the cross-raid descriptor before deserializing it.  If the
        // native profile constructor throws, a later result-screen callback
        // must not retry the previous raid's profile.
        RaidEndProfile.Clear();

        Profile profile = new(descriptor);

        if (profile.Side != EPlayerSide.Savage)
        {
            return;
        }

        // The result operation retains the Scav profile supplied when this
        // raid started. The backend's current ProfileOfPet may already have
        // been regenerated after death, so compare against the operation's
        // raid profile before applying a captured inventory to this screen.
        var raidScavProfile = __instance._savageProfile;
        if (raidScavProfile == null ||
            !string.Equals(profile.Id, raidScavProfile.Id, System.StringComparison.Ordinal))
        {
            return;
        }

        var session = __instance._session.TryCast<ClientBackendSession>();
        if (session == null || session.AllProfiles == null || session.AllProfiles.Length == 0)
        {
            return;
        }

        var primaryProfile = session.AllProfiles.FirstOrDefault(x => x != null && x.Side != EPlayerSide.Savage);
        if (primaryProfile == null)
        {
            return;
        }

        session.AllProfiles = new Profile[]
        {
            primaryProfile,
            profile
        };
        session.ProfileOfPet?.LearnAll();

        // make a request to the server, so it knows of the items we might transfer
        var payload = Serialize(descriptor);
        if (payload == null)
        {
            FikaGlobals.LogError("Could not serialize the raid-end profile, the scav save was skipped");
            return;
        }

        RequestHandler.PutJson("/raid/profile/scavsave", payload);
    }

    /// <summary>
    /// Kept in its own non-inlined method: a linkage failure against a shadowing
    /// Newtonsoft.Json surfaces while this method is compiled, and a try/catch in
    /// the same method could never observe it.
    /// </summary>
    private static string Serialize(EFT.ProfileDescriptor descriptor)
    {
        try
        {
            return ProfileDescriptorJson.ToServerJson(descriptor);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"Could not serialize the raid-end profile: {exception}");
            return null;
        }
    }
}
