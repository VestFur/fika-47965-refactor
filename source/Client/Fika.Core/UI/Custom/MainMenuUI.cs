using EFT;
using Fika.Core.Main.Utils;
using TMPro;
using UnityEngine.UI;
using static Fika.Core.UI.FikaUIGlobals;

public class MainMenuUI : MonoBehaviour
{
    public MainMenuUI(System.IntPtr pointer) : base(pointer) { }


    public MainMenuUI() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<MainMenuUI>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> RefreshButton;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> Label;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<GameObject> PlayerTemplate;

    /// <summary>
    /// Engine callbacks on a component the engine created from the bundle. They are written to
    /// the stable diagnostics file so the order in which the engine starts and destroys these
    /// injected instances survives the run: the crash this build chases happens while the
    /// engine releases a scripting object, so "engine called OnDestroy" being the last line is
    /// what identifies that object as the one under release.
    /// </summary>
    private void Awake()
    {
        
    }

    private void OnDestroy()
    {
        
    }

    private bool _labelReported;

    /// <summary>
    /// Writes the online player count into the label the prefab serialized.
    ///
    /// This overload takes the label the caller already cached and proved, so the panel does
    /// not have to re-read the serialized reference field (and allocate a wrapper for it) on
    /// every refresh tick.
    ///
    /// Returns false when the write was skipped.
    /// </summary>
    public bool UpdateLabel(TextMeshProUGUI label, int amount)
    {
        if (!NativeRefGuard.IsProvablyUsable(label, "TextMeshProUGUI", out string detail))
        {
            if (!_labelReported)
            {
                _labelReported = true;
                FikaGlobals.LogWarning($"[MainMenuUI] the online player counter was not written: MainMenuUI.Label is not a provably usable native object ({detail})");
            }

            return false;
        }

        label.SetFormattedText(LocaleUtils.UI_MMUI_ONLINE_PLAYERS.Localized(), amount);
        return true;
    }

    /// <summary>
    /// Reads the serialized label reference and writes into it.
    ///
    /// This is the only native write the panel performs on an object it did not create
    /// itself, so the reference is proven before it is used: the pointer has to point into a
    /// committed readable page, the object has to carry a readable il2cpp class name and that
    /// class has to be (or derive from) TextMeshProUGUI. The 47888 dumps fault on 0xA inside
    /// GameAssembly while such a write is handed a value that is not an object at all, and a
    /// value that merely *looks* like a pointer is not good enough - writing into the wrong
    /// object corrupts whatever really lives there.
    ///
    /// Returns false when the write was skipped, so the caller can log the raw pointer.
    /// </summary>
    public bool UpdateLabel(int amount)
    {
        var label = Label is null ? null : Label.Value;
        return UpdateLabel(label, amount);
    }
}
