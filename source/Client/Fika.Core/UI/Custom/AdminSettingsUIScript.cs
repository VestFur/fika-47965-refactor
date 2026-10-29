using EFT.Communications;
using System;
using Comfort.Common;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models.Admin;

public class AdminSettingsUIScript : MonoBehaviour
{
    public AdminSettingsUIScript(System.IntPtr pointer) : base(pointer) { }


    public AdminSettingsUIScript() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<AdminSettingsUIScript>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private AdminSettingsUI _adminSettingsUI;

    private void Start()
    {
        _adminSettingsUI = gameObject.GetComponent<AdminSettingsUI>();
        if (_adminSettingsUI == null)
        {
            throw new NullReferenceException("Could not find AdminSettingsUI");
        }

        var currentSettings = FikaRequestHandler.GetServerSettings();
        _adminSettingsUI.FriendlyFireCheck.Value.isOn = currentSettings.FriendlyFire;
        _adminSettingsUI.FreecamCheck.Value.isOn = currentSettings.FreeCam;
        _adminSettingsUI.SpectateFreecamCheck.Value.isOn = currentSettings.SpectateFreeCam;
        _adminSettingsUI.SharedQuestProgressionCheck.Value.isOn = currentSettings.SharedQuestProgression;
        _adminSettingsUI.AverageLevelCheck.Value.isOn = currentSettings.AverageLevel;

        _adminSettingsUI.ApplyButton.Value.onClick.AddListener(() =>
        {
            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick);
            ApplySettings();
        });

        _adminSettingsUI.CloseButton.Value.onClick.AddListener(() =>
        {
            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.ButtonClick);

            Destroy(this);
        });
    }

    private void OnDestroy()
    {
        Destroy(_adminSettingsUI.gameObject);
    }

    private void ApplySettings()
    {
        SetSettingsRequest req = new(_adminSettingsUI.FriendlyFireCheck.Value.isOn, _adminSettingsUI.FreecamCheck.Value.isOn, _adminSettingsUI.SpectateFreecamCheck.Value.isOn,
            _adminSettingsUI.SharedQuestProgressionCheck.Value.isOn, _adminSettingsUI.AverageLevelCheck.Value.isOn);
        var resp = FikaRequestHandler.SaveServerSettings(req);

        FikaNotifications.DisplayMessageNotification(resp.Success.ToString());

        Destroy(this);
    }

    internal static void Create()
    {
        var gameObject = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.AdminUI);
        var obj = Instantiate(gameObject);
        obj.AddComponent<AdminSettingsUIScript>();
        var rectTransform = obj.transform.GetChild(0).GetChild(0).RectTransform();
        if (rectTransform == null)
        {
            FikaGlobals.LogError("Could not get the RectTransform!");
            Destroy(obj);
            return;
        }
        rectTransform.gameObject.AddComponent<UIDragComponent>().Init(rectTransform, true);
        obj.SetActive(true);
    }
}