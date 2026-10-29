using UnityEngine.UI;

public class AdminSettingsUI : MonoBehaviour
{
    public AdminSettingsUI(System.IntPtr pointer) : base(pointer) { }


    public AdminSettingsUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<AdminSettingsUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Toggle> FriendlyFireCheck;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Toggle> FreecamCheck;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Toggle> SpectateFreecamCheck;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Toggle> SharedQuestProgressionCheck;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Toggle> AverageLevelCheck;

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> ApplyButton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> CloseButton;
}