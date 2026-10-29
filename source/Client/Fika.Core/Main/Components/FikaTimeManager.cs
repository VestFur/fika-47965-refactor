using Comfort.Common;
using EFT;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;

namespace Fika.Core.Main.Components;

public class FikaTimeManager : MonoBehaviour
{
    public FikaTimeManager(System.IntPtr pointer) : base(pointer) { }


    public FikaTimeManager() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<FikaTimeManager>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public CoopGame CoopGame;
    public GameTimer GameTimer;

    public static FikaTimeManager Create(CoopGame game)
    {
        var timeManager = game.gameObject.AddComponent<FikaTimeManager>();
        timeManager.CoopGame = game;
        timeManager.GameTimer = game.GameTimer;
        return timeManager;
    }

    /// <summary>
    /// (analysis/perf-round2) The session-time expiry check is one slot of the frame-cost probe: it reads a
    /// native Nullable field by name on every frame, so its real cost is worth measuring instead of assuming.
    /// </summary>
    protected void Update()
    {
        
        try
        {
            UpdateMeasured();
        }
        finally
        {
            
        }
    }

    /// <summary>
    /// (analysis/perf-round2) How often the native session clock is read: four times a second instead of
    /// once per frame. See the note in <see cref="UpdateMeasured"/>.
    /// </summary>
    private const float SessionCheckInterval = 0.25f;

    private float _nextSessionCheck;

    private void UpdateMeasured()
    {
        if (CoopGame.Status != GameStatus.Started || GameTimer == null)
            return;
        if (FikaFinalMissionDirector.DeferStop) return;

        // The native read below is a field lookup by name plus a freshly allocated Nullable wrapper, and the
        // only thing it decides is whether the raid's session clock (50 minutes by default) has run out. That
        // answer changes once per raid, so sampling it four times a second instead of once per frame removes
        // 59 of every 60 wrapper allocations and the same share of the il2cpp field reads, while the outcome
        // is unchanged: the MIA extraction it triggers is a multi-second sequence and cannot be noticed
        // 250 ms early. realtimeSinceStartup, not Time.time, so a pause or a time scale of zero cannot park
        // the check (Time.time freezes in both cases; this must not).
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextSessionCheck)
            return;

        _nextSessionCheck = now + SessionCheckInterval;

        var sessionTime = NativeNullable<Il2CppSystem.TimeSpan>.Read(GameTimer, "<SessionTime>k__BackingField");
        if (sessionTime.HasValue && GameTimer.PastTime >= sessionTime.Value)
        {
            CoopGame.ExitStatus = ExitStatus.MissingInAction;
            if (Singleton<GameWorld>.Instance?.MainPlayer?.TryCast<FikaPlayer>() is { } fikaPlayer)
                CoopGame.Extract(fikaPlayer, null);
            enabled = false;
        }
    }
}
