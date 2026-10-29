using BepInEx.Configuration;
using EFT;
using EFT.UI;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.UI;
using TMPro;

namespace Fika.Core.Main.Components;

internal sealed class Bleedout : MonoBehaviour
{
    public Bleedout(System.IntPtr pointer) : base(pointer) { }


    public Bleedout() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<Bleedout>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    private GameUI GameUI => MonoBehaviourSingleton<GameUI>.Instance;
    private KeyboardShortcut GiveUpKey => FikaPlugin.Instance.Settings.GiveUpKey.Value;

    private ClientHealthController _healthController;
    private bool _shouldBleed;
    private bool _beingRevived;
    private bool _finished;

    private float _giveUpTimer;
    private bool _givingUp;
    private TextMeshProUGUI _giveUpText;

    private const float _giveUpTime = 3f;

    internal void Init(ClientHealthController healthController)
    {
        _healthController = healthController;
        _shouldBleed = healthController.Player?.TryCast<FikaPlayer>()?.CaptureRevivalState().BleedoutRemaining >= 0;
        _giveUpText = FikaUIGlobals.CreateOverlayText(string.Format(LocaleUtils.UI_REVIVING_GIVE_UP.Localized(), $"'{GiveUpKey.MainKey}'"),
            new Vector4(0f, 0f, 0f, -400f));

        FikaPlayer.OnPlayerDeath += OnPlayerDeath;
        FikaPlayer.OnPlayerDownedChanged += OnPlayerDownedChange;

        InvokeRepeating(nameof(AgonySFX), 10f, 10f);
    }

    private void OnPlayerDownedChange(FikaPlayer player, bool downed)
    {
        if (downed)
        {
            OnPlayerDeath(player);
        }
    }

    private void OnPlayerDeath(FikaPlayer player)
    {
        if (player.IsYourPlayer || player.IsAI)
        {
            return;
        }

        if (CoopHandler.TryGetCoopHandler(out var coopHandler) && coopHandler.AreAllHumanPlayersDead())
        {
            FikaPlayer.OnPlayerDeath -= OnPlayerDeath;
            FikaPlayer.OnPlayerDownedChanged -= OnPlayerDownedChange;
            BleedOut();
        }
    }

    private void OnDestroy()
    {
        FikaPlayer.OnPlayerDeath -= OnPlayerDeath;
        FikaPlayer.OnPlayerDownedChanged -= OnPlayerDownedChange;

        if (_giveUpText != null)
        {
            Destroy(_giveUpText);
        }
        HideUI();
        CancelInvoke(nameof(AgonySFX));
    }

    private void Update()
    {
        if (_finished)
        {
            return;
        }

        if (_healthController.Player?.TryCast<FikaPlayer>()?.CaptureRevivalState() is not { } state) return;
        if (state.RevivePauseRemaining > 0) return;
        if (_beingRevived)
        {
            HideRevive();
            ShowUI();
        }
        var unscaledDeltaTime = Time.unscaledDeltaTime;
        CheckForKeys();

        if (_givingUp)
        {
            _giveUpTimer += unscaledDeltaTime;
            if (_giveUpTimer >= 3f)
            {
                BleedOut();
                return;
            }
        }

        if (_shouldBleed && state.BleedoutRemaining <= 0)
        {
            BleedOut();
        }
    }

    private void AgonySFX()
    {
        if (_healthController.Player == null || _healthController.Player.Speaker == null)
        {
            return;
        }

        _healthController.Player.Speaker.Play(EPhraseTrigger.OnAgony, _healthController.Player.HealthStatus, true, new Il2CppSystem.Nullable<int>());
    }

    private void BleedOut()
    {
        if (_finished) return;
        _finished = true;
        if (_healthController != null)
        {
            _healthController.BleedOut();
        }
        Destroy(this);
    }

    private void CheckForKeys()
    {
        if (Input.GetKeyDown(GiveUpKey.MainKey))
        {
            _givingUp = true;
            _giveUpTimer = 0f;
            GameUI.BattleUiPanelExtraction.Show(LocaleUtils.UI_REVIVING_GIVING_UP.Localized(),
            _giveUpTime);
        }

        if (Input.GetKeyUp(GiveUpKey.MainKey))
        {
            _givingUp = false;
            _giveUpTimer = 0f;
            HideUI();
            ShowUI();
        }
    }

    public void ShowRevive(string nickname)
    {
        _beingRevived = true;
        _givingUp = false;
        _giveUpTimer = 0f;

        GameUI.BattleUiPanelExtraction.Show(string.Format(LocaleUtils.UI_REVIVING_BEING_REVIVED_BY.Localized(), nickname));
    }

    public void HideRevive()
    {
        _beingRevived = false;
        HideUI();
    }

    public void ShowUI()
    {
        if (!_shouldBleed)
        {
            return;
        }

        GameUI.BattleUiPanelExtraction.Show(LocaleUtils.UI_REVIVING_BLEEDING_OUT.Localized(),
            _healthController.Player?.TryCast<FikaPlayer>()?.CaptureRevivalState().BleedoutRemaining ?? 0f);
    }

    public void HideUI()
    {
        GameUI.BattleUiPanelExtraction.Close();
    }
}
