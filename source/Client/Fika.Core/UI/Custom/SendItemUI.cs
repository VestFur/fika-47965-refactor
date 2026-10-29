using EFT.UI;
using Fika.Core.Main.Utils;
using TMPro;
using EFT;
using UnityEngine.UI;

public class SendItemUI : MonoBehaviour
{
    public SendItemUI(System.IntPtr pointer) : base(pointer) { }


    public SendItemUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<SendItemUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> CloseButton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> SendButton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Dropdown> PlayersDropdown;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_InputField> PlayersFilter;
    
#pragma warning disable CS0649
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> _headerText;
    
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> _sendText;
#pragma warning restore CS0649

    protected void Awake()
    {
        _headerText.Value.SetText(LocaleUtils.UI_SENDITEM_HEADER.Localized());
        _sendText.Value.SetText(LocaleUtils.UI_SENDITEM_BUTTON.Localized());
        var gameObjectToAdd = gameObject.transform.GetChild(0).GetChild(0).gameObject;
        var rectTransform = gameObjectToAdd.RectTransform();
        gameObjectToAdd.AddComponent<UIDragComponent>().Init(rectTransform, true);
    }
}



