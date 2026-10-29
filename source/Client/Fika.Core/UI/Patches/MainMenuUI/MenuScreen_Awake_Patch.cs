using System.Reflection;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Websocket;
using Fika.Core.UI.Custom;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.UI.Patches.MainMenuUI;

public class MenuScreen_Awake_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(MenuScreen)
            .GetMethod(nameof(MenuScreen.Awake));
    }

    /// <summary>
    /// Adds the injected presence/panel behaviour to the menu screen the engine owns.
    ///
    /// The behaviour is rooted (and any previous one is torn down) before it can run anything:
    /// the engine calls Start/OnEnable/OnDisable on it, so its managed side must stay reachable
    /// from the moment it exists. A menu screen that wakes twice (a relog) must not end up with
    /// two behaviours, each with its own panel and its own listeners registered with the engine.
    /// </summary>
    [PatchPostfix]
    public static void Postfix(MenuScreen __instance)
    {
        if (__instance == null)
        {
            return;
        }

        var gameObject = __instance.gameObject;
        if (gameObject == null)
        {
            
            return;
        }

        // Fallback for the presence registration. The roster this panel shows is filled by the
        // server from its online list, and a player only lands in that list when the
        // /fika/notification/ websocket connected. That socket is normally opened by
        // TarkovApplication_InitNotificationManager_Patch; this call makes the panel work even
        // when that patch cannot be installed (it targets a game method the build may rename).
        FikaNotificationManager.EnsureCreated("MenuScreen.Awake");

        var existing = gameObject.GetComponent<MainMenuUIScript>();
        if (existing != null)
        {
            MainMenuUIScript.Attach(existing);
            return;
        }

        MainMenuUIScript.Attach(gameObject.AddComponent<MainMenuUIScript>());
    }
}
