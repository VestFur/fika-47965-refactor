using TMPro;

public class FreecamUI : MonoBehaviour
{
    public FreecamUI(System.IntPtr pointer) : base(pointer) { }


    public FreecamUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FreecamUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> OverlayGroup;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> JumpText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> SwapMode;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> DetachText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> TeleportText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> VisionText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> CullingText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> InputText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> TurboText;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> PlayerList;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Transform> ListOfPlayers;

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> ListPlayerPrefab;
}
