using DG.Tweening;
using TMPro;
using UnityEngine.UI;

public class LoadingScreenPlayer : MonoBehaviour
{
    public LoadingScreenPlayer(System.IntPtr pointer) : base(pointer) { }


    public LoadingScreenPlayer() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<LoadingScreenPlayer>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> Nickname;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<TMP_Text> Percentage;
    public Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField<Image> ProgressBar;

    public float Progress
    {
        get
        {
            return _progress;
        }
    }

    private const float _division = 100f;
    private const float _tweenDuration = 0.25f;

    private float _progress;

    public void SetNickname(string nickname)
    {
        Nickname.Value.SetText(nickname);
    }

    public void SetProgress(float progress)
    {
        _progress = progress;
        ProgressBar.Value.DOFillAmount(progress / _division, _tweenDuration);
        Percentage.Value.SetFormattedText("{0}%", Mathf.CeilToInt(progress));
    }
}
