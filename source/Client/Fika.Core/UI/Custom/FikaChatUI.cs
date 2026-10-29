using TMPro;
using UnityEngine.UI;

public class FikaChatUI : MonoBehaviour
{
    public FikaChatUI(System.IntPtr pointer) : base(pointer) { }


    public FikaChatUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaChatUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> ChatText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> MessageText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_InputField> InputField;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<ScrollRect> ScrollView;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> SendButton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> CloseButton;
}
