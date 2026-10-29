using JsonType;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.Communications;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.UI.Models;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.LocalGame;

public sealed class TarkovApplication_LocalGamePreparer_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TarkovApplication).GetMethod(nameof(TarkovApplication.LocalGameMatching));
    }

    [PatchPrefix]
    public static async void Prefix(TarkovApplication __instance)
    {
        // Keep the native LocalGameMatching state machine intact. In IL2CPP,
        // replacing its returned Task and invoking a copied trampoline changes
        // the initialization order inside LoadMapAndData (the map unload
        // controllers are not reached). The official 5.0 plugin uses an
        // async-void preparation prefix and lets the native method continue.
        if (FikaBackendUtils.ClientType == EClientType.None)
            return;

        // A new raid start owns a clean failure state: without this, the reason an earlier attempt
        // recorded kept aborting every later start at CreateFikaGameCore ("The raid could not be
        // started: <old reason>"), including host raids. A failure of this attempt still wins.
        FikaBackendUtils.ClearLeftoverJoinFailure();

        var isServer = FikaBackendUtils.IsServer;
        
        try
        {
            await Prepare(__instance, isServer);
            
        }
        catch (Exception exception)
        {
            // This prefix is async void and the native method body keeps running while it
            // awaits, so a swallowed failure here used to let the raid start continue with a
            // network manager that was never created or connected: the Fika loading screen
            // then sits at 0 % and the raid start throws further down the line. Mark the
            // attempt as failed, so the raid start can abort with the real reason.
            FikaBackendUtils.MarkJoinFailed("The Fika network preparation failed: " + exception.Message, exception);
            Logger.LogError($"LocalGameMatching preparation failed: {exception}");
            
        }
    }

    private static async Task Prepare(TarkovApplication __instance, bool isServer)
    {
        Logger.LogDebug("TarkovApplication_LocalGamePreparer_Patch:Prefix");
        

        FikaBackendUtils.RequestFikaWorld = true;

        if (!isServer && !string.IsNullOrEmpty(FikaBackendUtils.HostLocationId))
        {
            if (string.Equals(__instance._raidSettings.LocationId, "sandbox", StringComparison.OrdinalIgnoreCase)
                && string.Equals(FikaBackendUtils.HostLocationId, "sandbox_high", StringComparison.OrdinalIgnoreCase))
            {
                __instance._raidSettings.SelectedLocation = __instance.Session.LocationSettings.locations.Values.AsManaged()
                    .FirstOrDefault(IsSandboxHigh);

                FikaNotifications.DisplayMessageNotification("Notification/HighLevelQueue".Localized(null),
                    ENotificationDurationType.Default, ENotificationIconType.Default, null);
            }

            if (string.Equals(__instance._raidSettings.LocationId, "sandbox_high", StringComparison.OrdinalIgnoreCase)
                && string.Equals(FikaBackendUtils.HostLocationId, "sandbox", StringComparison.OrdinalIgnoreCase))
            {
                __instance._raidSettings.SelectedLocation = __instance.Session.LocationSettings.locations.Values.AsManaged()
                    .FirstOrDefault(IsSandbox);
            }
        }

        if (!FikaBackendUtils.IsTransit)
        {
#if DEBUG
            FikaGlobals.LogInfo("Creating net manager");
#endif
            NetManagerUtils.CreateNetManager(isServer);
            if (isServer)
            {
                NetManagerUtils.StartPinger();
            }

            
            await NetManagerUtils.InitNetManager(isServer);
            

            if (isServer)
            {
                var status = new SetStatusModel(FikaBackendUtils.GroupId, LobbyEntry.ELobbyStatus.COMPLETE);
                await FikaRequestHandler.UpdateSetStatus(status);
            }
        }
    }

    private static bool IsSandboxHigh(LocationSettings.Location location)
    {
        return string.Equals(location.Id, "sandbox_high", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSandbox(LocationSettings.Location location)
    {
        return string.Equals(location.Id, "sandbox", StringComparison.OrdinalIgnoreCase);
    }
}
