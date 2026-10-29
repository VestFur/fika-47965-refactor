using EFT.Communications;
using System.Reflection;
using Comfort.Common;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using EFT;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches;

public class DisconnectButton_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MenuScreen).GetMethod(nameof(MenuScreen.Awake));
    }

    [PatchPostfix]
    static void Postfix(MenuScreen __instance)
    {
        // Keep the native menu initialization and existing listeners. Guard the
        // disconnect event itself for both mouse clicks and keyboard submission.
        var button = __instance._disconnectButton;
        var originalClick = button.OnClick;
        var guardedClick = new UnityEngine.Events.UnityEvent();
        guardedClick.AddListener(() =>
        {
            if (CanDisconnect()) originalClick.Invoke();
        });
        button.OnClick = guardedClick;
    }

    private static bool CanDisconnect()
    {
        if (FikaBackendUtils.IsServer)
        {
            var server = FikaSingleton<FikaServer>.Instance;
            if (server != null && server.NetServer.ConnectedPeersCount > 0)
            {
                NotificationManager.DisplayWarningNotification(string.Format(LocaleUtils.HOST_CANNOT_EXTRACT_MENU.Localized(),
                    server.NetServer.ConnectedPeersCount));
                return false;
            }
        }
        return true;
    }
}
