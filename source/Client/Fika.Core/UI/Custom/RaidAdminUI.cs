using TMPro;
using UnityEngine.UI;

public class RaidAdminUI : MonoBehaviour
{
    public RaidAdminUI(System.IntPtr pointer) : base(pointer) { }


    public RaidAdminUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<RaidAdminUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Dropdown> ClientSelection;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> CloseButton;

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> InfoPane;

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> HeaderText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> SentPacketsText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> ReceivedPacketsText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> SentDataText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> ReceivedDataText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> PacketLossText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> PacketLossPercentText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> KickButton;
}