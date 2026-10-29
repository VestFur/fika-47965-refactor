using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Http;
using SPTushonka.Reflection.Patching;
using UnityEngine.UI;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.UI.Patches;

public class MenuTaskBar_Patch : ModulePatch
{
    private static DateTime _lastDownload = DateTime.Now.AddMinutes(-2);

    // The toggle and the tooltip below are handed to the live MenuTaskBar, which calls into
    // them on every screen transition. They are kept so the next main menu can re-check the
    // exact pointers that were registered (a reference the engine holds is the one thing a
    // guard in this class cannot see).
    private static AnimatedToggle _registeredToggle;
    private static HoverTooltipArea _registeredTooltip;
    private static bool _disabledReported;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(MenuTaskBar).GetMethod(nameof(MenuTaskBar.Awake));
    }

    [PatchPostfix]
    public static void Postfix(EFT.UI.MenuTaskBar __instance)
    {
        // Coop / 'Download Profile Button' is this decoration's own kill switch: the taskbar
        // calls into the toggle and the tooltip registered here on every screen transition,
        // so it can be switched off while the online players panel stays enabled.
        if (!(FikaPlugin.Instance.Settings.DownloadProfileButton?.Value ?? true))
        {
            if (!_disabledReported)
            {
                _disabledReported = true;
                FikaGlobals.LogWarning("[MenuTaskBar] the download-profile button is disabled in the config (Coop / 'Download Profile Button' = false); the taskbar is left untouched");
            }

            return;
        }

        // The nodes this patch rewires are live SPT 5 taskbar objects. A missing node used
        // to throw straight out of MenuTaskBar.Awake and abort the main menu; report it and
        // leave the taskbar untouched instead.
        try
        {
            Apply(__instance);
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[MenuTaskBar] the download-profile button was not created: {exception}");
        }
    }

    /// <summary>
    /// Looks up the first child image of a taskbar node without ever dereferencing a child
    /// that is not a real object (the chain used to be evaluated unconditionally).
    /// </summary>
    private static Image FirstImageOf(GameObject root)
    {
        if (!NativeRefGuard.IsUsable(root))
        {
            return null;
        }

        var rootTransform = root.transform;
        if (!NativeRefGuard.IsUsable(rootTransform) || rootTransform.childCount < 1)
        {
            return null;
        }

        var child = rootTransform.GetChild(0);
        if (!NativeRefGuard.IsUsable(child))
        {
            return null;
        }

        var childObject = child.gameObject;
        if (!NativeRefGuard.IsUsable(childObject))
        {
            return null;
        }

        var image = childObject.GetComponent<Image>();
        return NativeRefGuard.IsUsable(image) ? image : null;
    }

    private static void Apply(EFT.UI.MenuTaskBar __instance)
    {
        
        FikaGlobals.LogInfo("[MenuTaskBar] 1/2 looking up the SPT 5 taskbar nodes");
        var watchlistGameobject = GameObject.Find("Preloader UI/Preloader UI/BottomPanel/Content/TaskBar/Tabs/Watchlist");
        if (NativeRefGuard.IsUsable(watchlistGameobject))
        {
            // SPT 5 retains serialized references to taskbar controls after
            // Awake. Keep hidden controls alive for later screen transitions.
            watchlistGameobject.SetActive(false);
            var fleaMarketGameObject = GameObject.Find("Preloader UI/Preloader UI/BottomPanel/Content/TaskBar/Tabs/FleaMarket");
            if (NativeRefGuard.IsUsable(fleaMarketGameObject))
            {
                var downloadProfileGameObject = GameObject.Instantiate(fleaMarketGameObject);
                
                downloadProfileGameObject.name = "DownloadProfile";
                // The live taskbar node is used as the parent, so it is verified before it
                // is handed to SetParent - an invalid parent reference would be stored by the
                // engine and dereferenced later.
                var taskBarParent = fleaMarketGameObject.transform.parent;
                if (!NativeRefGuard.IsUsable(taskBarParent))
                {
                    FikaGlobals.LogError("[MenuTaskBar] the flea market tab has no usable parent; the download-profile button is not created");
                    return;
                }

                var cloneTransform = downloadProfileGameObject.transform;
                if (!NativeRefGuard.IsProvablyUsable(cloneTransform, "Transform", out string cloneTransformDetail))
                {
                    FikaGlobals.LogError($"[MenuTaskBar] the cloned tab has no provably usable transform ({cloneTransformDetail}); the download-profile button is not created");
                    return;
                }

                
                cloneTransform.SetParent(taskBarParent, false);
                // The sibling index is only applied to a transform that proved to be real: the
                // engine stores it in the parent's child list and dereferences it later.
                cloneTransform.SetSiblingIndex(10);

                var downloadProfileButton = downloadProfileGameObject.transform.GetChild(0).gameObject;
                
                downloadProfileButton.name = "DownloadProfileButton";

                var text = downloadProfileGameObject.GetComponentInChildren<LocalizedText>();
                
                if (NativeRefGuard.IsProvablyUsable(text, "LocalizedText", out _))
                {
                    text.SetLabelText(LocaleUtils.UI_DOWNLOAD_PROFILE.Localized());
                    text.LocalizationKey = "";
                }

                var buildListObject = GameObject.Find("/Menu UI/UI/EquipmentBuildsScreen/Panels/BuildsListPanel/Header/SizeSample/Selected/Icon");
                if (NativeRefGuard.IsUsable(buildListObject))
                {
                    // The sprite donors are live SPT 5 objects; each step of the chain is
                    // checked so a missing node cannot turn into a native dereference.
                    var downloadImage = buildListObject.GetComponent<Image>();
                    var downloadProfileImage = FirstImageOf(downloadProfileButton);
                    if (NativeRefGuard.IsUsable(downloadProfileImage) && NativeRefGuard.IsUsable(downloadImage))
                    {
                        downloadProfileImage.sprite = downloadImage.sprite;
                    }
                }

                var animatedToggle = downloadProfileGameObject.GetComponentInChildren<AnimatedToggle>();
                if (NativeRefGuard.IsProvablyUsable(animatedToggle, "AnimatedToggle", out string toggleDetail))
                {
                    
                    // Both of these are writes into serialized fields of a live object that the
                    // taskbar re-uses on every screen transition; they only happen once the
                    // object proved to be the expected component.
                    animatedToggle.group = null;
                    animatedToggle.onValueChanged = new Toggle.ToggleEvent();
                    
                    animatedToggle.onValueChanged.AddListener(async (selected) =>
                    {
                        if (!selected) return;
                        var elapsed = DateTime.Now - _lastDownload;
                        if (elapsed.TotalMinutes <= 1)
                        {
                            FikaNotifications.DisplayMessageNotification(
                                $"Please wait {(int)Math.Ceiling(60 - elapsed.TotalSeconds)} seconds before attempting to download your profile again!",
                                iconType: ENotificationIconType.Alert, textColor: new Il2CppSystem.Nullable<Color>(Color.red));
                            return;
                        }

                        try
                        {
                            var profileResponse = await FikaRequestHandler.GetProfile();
                            var responseHasError = !string.IsNullOrEmpty(profileResponse.ErrorMessage);
                            var error = responseHasError ? profileResponse.ErrorMessage : "Failed to retrieve profile";
                            if (!responseHasError && profileResponse?.Profile != null)
                            {
                                Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonBottomBarClick);
                                var installDir = Environment.CurrentDirectory;
                                var fikaDir = installDir + @"\SPT\user\fika";

                                if (!string.IsNullOrEmpty(installDir))
                                {
                                    if (!Directory.Exists(fikaDir))
                                    {
                                        Directory.CreateDirectory(fikaDir);
                                    }

                                    // create dir, put profile in dir and moddata, change to dictionary, key = modName

                                    var profileId = Singleton<ClientApplication<IEftSession>>.Instance.Session.Profile.ProfileId;

                                    var profileDir = Path.Combine(fikaDir, profileId);
                                    if (!Directory.Exists(profileDir))
                                    {
                                        Directory.CreateDirectory(profileDir);
                                    }

                                    if (File.Exists(@$"{profileDir}\{profileId}.json"))
                                    {
                                        File.Copy(Path.Combine(profileDir, $"{profileId}.json"), Path.Combine(profileDir, $"{profileId}.json.BAK"), true);
                                    }

                                    if (profileResponse.ModData != null)
                                    {
                                        foreach ((var modName, var modData) in profileResponse.ModData)
                                        {
                                            File.WriteAllText(Path.Combine(profileDir, $"{modName}.json"), modData);
                                        }
                                    }

                                    File.WriteAllText(Path.Combine(profileDir, $"{profileId}.json"), profileResponse.Profile.ToString());
                                    FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.SAVED_PROFILE.Localized(),
                                        [ColorizeText(EColor.BLUE, profileId), fikaDir]));

                                    /* ____toggleButtons.Remove(EMenuType.NewsHub);
                                     ____hoverTooltipAreas.Remove(EMenuType.NewsHub);
                                     GameObject.Destroy(downloadProfileGameObject);*/
                                }
                            }
                            else
                            {
                                NotificationManager.DisplayWarningNotification(error);
                            }
                        }
                        catch (Exception ex)
                        {
                            NotificationManager.DisplayWarningNotification(LocaleUtils.UNKNOWN_ERROR.Localized());
                            FikaGlobals.LogError(ex.Message);
                        }

                        _lastDownload = DateTime.Now;
                    });

                    FikaGlobals.LogInfo("[MenuTaskBar] 2/2 rewiring the news hub entry");
                    if (!__instance._hoverTooltipAreas.TryGetValue(EMenuType.NewsHub, out var surveyButton) ||
                        !__instance._toggleButtons.ContainsKey(EMenuType.NewsHub))
                    {
                        FikaGlobals.LogWarning("[MenuTaskBar] the SPT 5 taskbar has no NewsHub entry; the download-profile button is left unwired");
                        return;
                    }

                    __instance._toggleButtons.Remove(EMenuType.NewsHub);
                    __instance._hoverTooltipAreas.Remove(EMenuType.NewsHub);
                    if (NativeRefGuard.IsUsable(surveyButton))
                    {
                        surveyButton.gameObject.SetActive(false);
                    }

                    // These two objects are handed to the live taskbar, which calls into them
                    // on every screen transition. Registering a reference that is not a real
                    // native object would make the menu dereference garbage later, so the
                    // registration only happens once both proved usable.
                    var tooltip = downloadProfileGameObject.GetComponentInChildren<HoverTooltipArea>(true);
                    if (!NativeRefGuard.IsProvablyUsable(tooltip, "HoverTooltipArea", out _))
                    {
                        tooltip = downloadProfileGameObject.AddComponent<HoverTooltipArea>();
                    }

                    bool toggleUsable = NativeRefGuard.IsProvablyUsable(animatedToggle, "AnimatedToggle", out string toggleCheck);
                    bool tooltipUsable = NativeRefGuard.IsProvablyUsable(tooltip, "HoverTooltipArea", out string tooltipCheck);
                    if (!toggleUsable || !tooltipUsable)
                    {
                        FikaGlobals.LogError($"[MenuTaskBar] the download-profile button is not wired: toggle={toggleCheck} tooltip={tooltipCheck}; the taskbar is left untouched");
                        return;
                    }

                    // The taskbar keeps these two and calls into them on every screen
                    // transition: it only receives references that proved to be the expected
                    // components, so the engine never ends up holding a value like 0x0A.
                    __instance._toggleButtons.Add(EMenuType.NewsHub, animatedToggle);
                    __instance._hoverTooltipAreas.Add(EMenuType.NewsHub, tooltip);
                    _registeredToggle = animatedToggle;
                    _registeredTooltip = tooltip;
                    FikaGlobals.LogInfo($"[MenuTaskBar] download-profile button wired: toggle={toggleCheck} tooltip={tooltipCheck} surveyButton={NativeRefGuard.Describe(surveyButton)}");
                }
            }
        }
    }
}
