using EFT;
using EFT.UI;
using Fika.Core.Main.Utils;
using TMPro;
using UnityEngine.UI;

public class MatchMakerUI : MonoBehaviour
{
    public MatchMakerUI(System.IntPtr pointer) : base(pointer) { }


    public MatchMakerUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<MatchMakerUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> ServerBrowserPanel;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> RefreshButton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> RaidGroupHostButton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> RaidGroupDefaultToClone;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> DediSelection;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> StartButton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> CloseButton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Toggle> DedicatedToggle;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> LoadingScreen;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> LoadingImage;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Toggle> SpectatorToggle;

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> RaidsText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> JoinText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> HostRaidText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> SelectAmountText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> UseDedicatedHostText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> StartText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> LoadingAnimationText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> LoadingScreenHeaderText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> LoadingScreenInfoText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> JoinAsSpectatorText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Dropdown> HeadlessSelection;

    protected void Awake()
    {
        RaidsText.Value.SetText(LocaleUtils.UI_MM_RAIDSHEADER.Localized());
        JoinText.Value.SetText(LocaleUtils.UI_MM_JOIN_BUTTON.Localized());
        HostRaidText.Value.SetText(LocaleUtils.UI_MM_HOST_BUTTON.Localized());
        SelectAmountText.Value.SetText(LocaleUtils.UI_MM_SESSION_SETTINGS_HEADER.Localized());
        UseDedicatedHostText.Value.SetText(LocaleUtils.UI_MM_USE_DEDICATED_HOST.Localized());
        StartText.Value.SetText(LocaleUtils.UI_MM_START_BUTTON.Localized());
        LoadingScreenHeaderText.Value.SetText(LocaleUtils.UI_MM_LOADING_HEADER.Localized());
        LoadingScreenInfoText.Value.SetText(LocaleUtils.UI_MM_LOADING_DESCRIPTION.Localized());

        JoinAsSpectatorText.Value.SetText(LocaleUtils.UI_MM_JOIN_AS_SPECTATOR.Localized().ToUpper());

        var spectateTooltip = SpectatorToggle.Value.gameObject.AddComponent<HoverTooltipArea>();
        spectateTooltip.SetMessageText(LocaleUtils.UI_MM_JOIN_AS_SPECTATOR_DESCRIPTION.Localized());
    }
}
