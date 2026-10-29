using Fika.Core.Main.Utils;
using TMPro;
using EFT;
using UnityEngine.UI;
using static Fika.Core.UI.FikaUIGlobals;

public class MainMenuUIPlayer : MonoBehaviour
{
    public MainMenuUIPlayer(System.IntPtr pointer) : base(pointer) { }


    public MainMenuUIPlayer() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<MainMenuUIPlayer>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> PlayerName;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> PlayerLevel;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TextMeshProUGUI> PlayerStatus;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> StatusImage;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Button> JoinButton;

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

    private bool _wiringReported;

    /// <summary>
    /// Fills one roster row. Every field comes from the player template inside the bundle, so
    /// each one is proven before it is written to: a readable page, a readable il2cpp class
    /// name and the expected base class. A field that holds something which is not an object
    /// at all (the dumps show 0x0A) or an object of the wrong type would otherwise turn this
    /// row into a native access violation or corrupt an unrelated object.
    ///
    /// Returns false when at least one field had to be skipped, so the caller can count
    /// partially wired rows.
    /// </summary>
    public bool SetActivity(string nickname, int level, EFikaPlayerPresence presence)
    {
        var complete = true;

        var playerName = PlayerName is null ? null : PlayerName.Value;
        if (NativeRefGuard.IsProvablyUsable(playerName, "TextMeshProUGUI", out _))
        {
            playerName.SetText(nickname);
        }
        else
        {
            complete = false;
        }

        

        var playerLevel = PlayerLevel is null ? null : PlayerLevel.Value;
        
        if (NativeRefGuard.IsProvablyUsable(playerLevel, "TextMeshProUGUI", out _))
        {
            playerLevel.SetFormattedText("({0})", level);
        }
        else
        {
            complete = false;
        }

        

        var status = presence switch
        {
            EFikaPlayerPresence.IN_MENU => LocaleUtils.UI_MMUI_IN_MENU.Localized(),
            EFikaPlayerPresence.IN_RAID => LocaleUtils.UI_MMUI_IN_RAID.Localized(),
            EFikaPlayerPresence.IN_STASH => LocaleUtils.UI_MMUI_IN_STASH.Localized(),
            EFikaPlayerPresence.IN_HIDEOUT => LocaleUtils.UI_MMUI_IN_HIDEOUT.Localized(),
            EFikaPlayerPresence.IN_FLEA => LocaleUtils.UI_MMUI_IN_FLEA.Localized(),
            _ => LocaleUtils.UI_MMUI_IN_MENU.Localized(),
        };

        var playerStatus = PlayerStatus is null ? null : PlayerStatus.Value;
        
        if (NativeRefGuard.IsProvablyUsable(playerStatus, "TextMeshProUGUI", out _))
        {
            playerStatus.SetText(status);
        }
        else
        {
            complete = false;
        }

        

        if (!SetImageColor(presence))
        {
            complete = false;
        }

        var joinButton = JoinButton is null ? null : JoinButton.Value;
        

        if (!complete && !_wiringReported)
        {
            _wiringReported = true;
            FikaGlobals.LogWarning("[MainMenuUIPlayer] at least one field of the player template is not a provably usable native object; the remaining fields are still filled in");
        }

        return complete;
    }

    private bool SetImageColor(EFikaPlayerPresence presence)
    {
        var statusImage = StatusImage is null ? null : StatusImage.Value;
        
        if (!NativeRefGuard.IsProvablyUsable(statusImage, "Image", out _))
        {
            return false;
        }

        statusImage.color = presence switch
        {
            EFikaPlayerPresence.IN_MENU => Color.green,
            EFikaPlayerPresence.IN_RAID => Color.red + (Color.black * 0.3f),
            EFikaPlayerPresence.IN_STASH or EFikaPlayerPresence.IN_HIDEOUT or EFikaPlayerPresence.IN_FLEA => Color.blue + (Color.white * 0.3f),
            _ => Color.green,
        };
        return true;
    }
}
