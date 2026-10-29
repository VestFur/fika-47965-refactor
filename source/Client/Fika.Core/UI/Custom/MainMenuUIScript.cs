using EFT.Communications;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using Fika.Core.Bundles;
using Fika.Core.Main.Components;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Http;
using Fika.Core.Networking.Models;
using Fika.Core.Networking.Models.Presence;
using Fika.Core.Networking.Websocket;
using HarmonyLib;
using JsonType;
using TMPro;
using UnityEngine.UI;
using static Fika.Core.UI.FikaUIGlobals;

namespace Fika.Core.UI.Custom;

public class MainMenuUIScript : MonoBehaviour
{
    public MainMenuUIScript(System.IntPtr pointer) : base(pointer) { }


    public MainMenuUIScript() : this(Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorPointer<MainMenuUIScript>())
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.DerivedConstructorBody(this);
    }

    public static bool Exist
    {
        get
        {
            return _instance != null;
        }
    }

    public static MainMenuUIScript Instance
    {
        get
        {
            return _instance;
        }
    }

    /// <summary>
    /// True once the panel itself exists. <see cref="Exist"/> only says "a presence reporter is
    /// alive", which is also true while 'Coop / Enable Online Players' is off: the six non-UI
    /// call sites use <see cref="Exist"/> and only ever report a presence, so they must keep
    /// working without the panel. Nothing may be written into the panel unless this is true.
    /// </summary>
    public bool PanelReady
    {
        get
        {
            return _panelCreated && IsAlive(_userInterface) && IsAlive(_mainMenuUI);
        }
    }

    public bool JoinInProgress
    {
        get => _joinInProgress;
        set
        {
            lock (_joinInProgressLock)
            {
                _joinInProgress = value;
            }
        }
    }

    private static MainMenuUIScript _instance;

    // [fix] The panel pump. The panel used to run as managed coroutines; the engine crashed
    // while it processed exactly that object (an Il2CppManagedEnumerator, the wrapper
    // StartCoroutine(IEnumerator) hands the engine), so the panel is driven from Update
    // instead and never hands the engine an enumerator to drive again.
    private bool _pumpArmed;
    private bool _pumpAttached;
    private int _pumpStage;
    private int _pumpPolls;
    private float _pumpElapsed;
    private bool _pumpWasReady;
    private IntPtr _lastPumpInstance;
    private IntPtr _lastPumpMenuScreen;
    private float _nextRefreshTime;
    // True while the roster is empty *and* the notification websocket - the socket whose
    // connection is what puts this player into the server's online list - has not connected
    // yet. The next refresh then runs a second later instead of ten, so the local player's row
    // appears as soon as the server knows about it instead of one tick later.
    private bool _waitingForPresenceSocket;

    // [refresh] Who asked for the refresh that is about to run: "open" when the engine
    // re-activated the panel, "manual" for the refresh button, null for the ordinary cycle.
    // Only the diagnostic line reads it.
    private string _pendingRefreshSource;

    // [refresh] What the panel currently shows: the identity of the roster the rows were built
    // from (RosterSignature) and the count written into the counter label (-1 while nothing has
    // been written). A cycle whose answer has the same identity keeps the rows instead of
    // rebuilding them, which is what makes a short visible cycle affordable.
    private string _lastRosterSignature;
    private int _labelCount = -1;

    // [refresh] The previous diagnostic line, so a cycle that changed nothing does not repeat it.
    private string _lastRefreshReport;

    private int _watchdogStartFrame = -1;
    private int _watchdogMark;
    private static readonly int[] _watchdogMarks = [1, 2, 3, 5, 10, 30, 60, 120];

    private MainMenuUI _mainMenuUI;
    private GameObject _playerTemplate;
    private GameObject _userInterface;
    private List<GameObject> _players;
    private DateTime _lastRefresh;
    private DateTime _lastSet;
    private RectTransform _transformToScale;
    private bool _labelUsable;
    private IMatchmakerSession<RaidSettings> _backendSession;
    private bool _initialized;

    private bool _joinInProgress;
    private readonly object _joinInProgressLock = new();

    // Reported once each so a broken prefab reference is visible without spamming the log
    // every refresh cycle, and so the next crash log pinpoints which reference is bad.
    private bool _labelReported;
    private bool _templateReported;
    private bool _rowReported;
    private bool _refreshReported;
    private bool _panelReported;
    private bool _tooltipTraceReported;
    private bool _counterReported;
    private bool _presenceSocketReported;
    private int _refreshTick;
    private string _labelDetail = "not checked yet";

    // [life] Cached native wrappers: read from the prefab once, proved again on the way out,
    // instead of allocating a fresh wrapper through Il2CppReferenceField.Value every tick.
    private GameObject _panelObject;
    private Button _refreshButton;
    private TextMeshProUGUI _label;
    private Transform _menuScreenTransform;
    private Transform _panelTransform;

    private bool _panelCreated;
    private bool _engineViewReported;

    // [life] The two engine-owned objects that carry a *Fika-injected* class and used to be
    // locals: the panel's drag component and one MainMenuUIPlayer per roster row. The engine
    // instantiates them from the bundle, so their managed side has no GC handle of its own
    // (FikaNativeTypes / NativeInjectedFinalizer) and is reachable only through these fields.
    // A row the engine still holds while its managed side is collected is exactly the
    // engine -> managed call into a dead object this build chases.
    private UIDragComponent _dragComponent;
    private readonly List<MainMenuUIPlayer> _rowComponents = [];

    /// <summary>
    /// Strong roots for every native object this panel hands to the engine.
    ///
    /// The engine holds the panel, its children, the injected MainMenuUI component and the
    /// registered listeners as raw il2cpp pointers. A raw pointer on the native side does not
    /// keep a managed object alive, so anything the engine can still call into has to stay
    /// reachable from managed code as well - that is the difference between "the engine calls
    /// my object" and "the engine calls a freed object" (the 47888 dump reads a value that is
    /// not an object pointer at all, so at least one such handover was already lost).
    /// Everything that is handed over goes through <see cref="Root"/> and is dropped again in
    /// <see cref="DestroyPanel"/>, so the list cannot outlive one panel.
    /// </summary>
    private static readonly List<RootEntry> _roots = [];

    private readonly struct RootEntry
    {
        internal readonly string Tag;
        internal readonly object Target;

        internal RootEntry(string tag, object target)
        {
            Tag = tag;
            Target = target;
        }
    }

    /// <summary>
    /// Managed delegates that are currently registered in a *native* UnityEvent. The native
    /// event keeps its trampoline alive; the CLR delegate behind it, and with it its target,
    /// is kept reachable here until it is removed again.
    /// </summary>
    private readonly List<(Button Button, Action Listener)> _rowListeners = [];
    private Action _refreshListener;

    private const int _minSecondsToWait = 2;

    /// <summary>
    /// Registers the injected component the engine calls into (Start/OnEnable/OnDisable) before
    /// any coroutine or panel work starts. Called by the MenuScreen patch, which is the only
    /// creator.
    /// </summary>
    internal static void Attach(MainMenuUIScript instance)
    {
        if (instance is null)
        {
            return;
        }

        if (_instance != null && !ReferenceEquals(_instance, instance))
        {
            
            
            _instance.DestroyPanel();
        }

        _instance = instance;
        Root("behaviour", instance);
        
    }

    private static void Root(string tag, object target)
    {
        if (target is null)
        {
            return;
        }

        lock (_roots)
        {
            foreach (var entry in _roots)
            {
                if (ReferenceEquals(entry.Target, target))
                {
                    return;
                }
            }

            _roots.Add(new RootEntry(tag, target));
        }

        
    }

    private void Release(params object[] targets)
    {
        lock (_roots)
        {
            foreach (var target in targets)
            {
                if (target is null)
                {
                    continue;
                }

                for (var index = _roots.Count - 1; index >= 0; index--)
                {
                    if (ReferenceEquals(_roots[index].Target, target))
                    {
                        _roots.RemoveAt(index);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Appends the concrete reason a ping could not be started to the dialog body, so the player does
    /// not have to open the log to tell "the host never published an address" from "we cannot reach
    /// the server". Empty when there is no extra detail.
    /// </summary>
    private static string PingingFailureDetail(FikaPingingClient pingingClient) =>
        string.IsNullOrEmpty(pingingClient?.FailureReason) ? string.Empty : "\n\n" + pingingClient.FailureReason;


    /// <summary>
    /// Tear-down of everything this instance handed to the engine: the listeners are removed
    /// from the native events first (so the engine cannot call a delegate whose target is gone),
    /// then the panel hierarchy is destroyed and every root is dropped.
    /// </summary>
    internal void DestroyPanel()
    {
        TeardownPanel("the menu screen released the panel", stopCoroutines: true, protecting: false);
    }

    /// <summary>
    /// The one teardown path. The order is the point of it: the native listeners go first (the
    /// engine must not be able to call a delegate whose target is on its way out), then the
    /// coroutines are stopped (the engine must not keep driving an enumerator this script no
    /// longer tracks), then the panel hierarchy is destroyed and every root is dropped.
    ///
    /// <paramref name="protecting"/> marks the self-protecting call: the panel noticed that the
    /// engine no longer holds what it was given and destroys itself in the same frame. A broken
    /// panel can then at worst disappear instead of making the engine call into a dead object.
    /// </summary>
    private void TeardownPanel(string reason, bool stopCoroutines, bool protecting)
    {
        RemoveRowListeners();
        DestroyRows();

        if (_refreshButton != null && _refreshListener != null && IsAlive(_refreshButton))
        {
            try
            {
                Fika.Core.UnityEventCompatExtensions.RemoveListener(_refreshButton.onClick, _refreshListener);
            }
            catch (Exception exception)
            {
                
            }
        }

        if (stopCoroutines)
        {
            StopCoroutines();
        }
        else
        {
            // The behaviour itself is being destroyed: the pump is disarmed without touching
            // the engine (there is no engine coroutine left, so nothing has to be stopped).
            StopCoroutines();
        }

        _refreshButton = null;
        _refreshListener = null;
        _label = null;
        _transformToScale = null;
        _dragComponent = null;

        if (IsAlive(_panelObject))
        {
            GameObject.Destroy(_panelObject);
        }

        _panelObject = null;
        _playerTemplate = null;
        _userInterface = null;
        _mainMenuUI = null;
        _panelTransform = null;
        _panelCreated = false;
        _engineViewReported = false;
        _pendingRefreshSource = null;
        _lastRosterSignature = null;
        _labelCount = -1;
        _players?.Clear();
        Release(_panelObject, _userInterface, _mainMenuUI, _transformToScale, _panelTransform, _refreshButton,
            _label, _playerTemplate, _dragComponent, _refreshListener);
        
        
    }

    /// <summary>
    /// Disarms the panel pump.
    ///
    /// There is no engine coroutine left to stop: the panel is driven from Update, so nothing
    /// the engine holds has to be tracked here. That is the fix for the menu fault - the
    /// engine crashed while it processed the wrapper of a panel coroutine and resolved its
    /// managed object to the bogus pointer 0xA (caught in the engine's frames by the
    /// first-chance probe).
    /// </summary>
    private void StopCoroutines()
    {
        _pumpArmed = false;
        _nextRefreshTime = 0f;
        _watchdogStartFrame = -1;
    }

    /// <summary>
    /// Cheap self protection that runs on every refresh tick, whether or not the raw pointer
    /// trace is on: are the objects this panel handed to the engine still usable? When one of
    /// them is not, the panel is destroyed in the same frame instead of being used.
    /// </summary>
    private bool EnsurePanelUsable()
    {
        if (!_panelCreated)
        {
            // Nothing handed over (or already torn down): nothing to protect.
            return false;
        }

        string reason = null;
        if (!NativeRefGuard.IsUsable(_panelObject))
        {
            reason = "the panel root is no longer a usable native object";
        }
        else if (!NativeRefGuard.IsUsable(_userInterface))
        {
            reason = "the panel content object is no longer a usable native object";
        }
        else if (!NativeRefGuard.IsUsable(_mainMenuUI))
        {
            reason = "the panel component is no longer a usable native object";
        }

        if (reason is null)
        {
            return true;
        }

        FikaGlobals.LogWarning("[MainMenuUI] " + reason + "; the panel is destroyed in the same frame instead of being used");
        TeardownPanel(reason, stopCoroutines: true, protecting: true);
        return false;
    }

    /// <summary>
    /// Gives up on building the panel. The engine already owns the instantiated objects by then,
    /// so they are destroyed again instead of being left parented under the menu screen with no
    /// owner (an orphan the engine still holds is exactly the state this panel must never leave
    /// behind).
    /// </summary>
    private void AbortPanelCreation(string reason)
    {
        FikaGlobals.LogError(reason);
        
        DestroyPanel();
    }

    private void RemoveRowListeners()
    {
        foreach ((var button, var listener) in _rowListeners)
        {
            try
            {
                if (button != null && listener != null && IsAlive(button))
                {
                    Fika.Core.UnityEventCompatExtensions.RemoveListener(button.onClick, listener);
                }
            }
            catch (Exception exception)
            {
                
            }

            // The native listener is gone, so the CLR delegate - and with it the closure that
            // captures this behaviour and a native location object - no longer has to stay
            // rooted. Without this the roots list grew by one delegate per refresh tick.
            Release(listener);
        }

        _rowListeners.Clear();
    }

    /// <summary>
    /// Destroys the roster rows and drops every root that belonged to them, including the
    /// Fika-injected <see cref="MainMenuUIPlayer"/> of each row: the engine created that instance
    /// from the bundle, so its managed side has to stay reachable for as long as the engine holds
    /// the row.
    /// </summary>
    private void DestroyRows()
    {
        if (_players != null)
        {
            foreach (var item in _players)
            {
                Release(item);
                if (IsAlive(item))
                {
                    GameObject.Destroy(item);
                }
            }

            _players.Clear();
        }

        foreach (var component in _rowComponents)
        {
            Release(component);
        }

        _rowComponents.Clear();
    }

    /// <summary>
    /// The panel's own transform, its parent and its component, re-read from the engine on
    /// every tick. If the engine no longer holds what this panel handed it, that has to be
    /// visible in the log *before* the engine calls into it - this is the check that separates
    /// "Fika wrote something bad" from "the engine lost the reference".
    /// </summary>
    private void VerifyEngineView()
    {
        if (!IsAlive(_panelObject))
        {
            return;
        }

        try
        {
            var parent = _panelObject.transform.parent;
            var parentMatches = IsAlive(parent) && IsAlive(_menuScreenTransform)
                && parent.Pointer == _menuScreenTransform.Pointer;

            var component = _panelObject.GetComponent<MainMenuUI>();
            var componentMatches = IsAlive(component) && IsAlive(_mainMenuUI)
                && component.Pointer == _mainMenuUI.Pointer;

            var active = _panelObject.activeInHierarchy;

            if (parentMatches && componentMatches && active)
            {
                if (!_engineViewReported)
                {
                    _engineViewReported = true;
                    
                }

                return;
            }

            // Self protection: the engine dropped a reference this script handed it. The panel
            // is destroyed in the same frame - before the engine gets another chance to call
            // into it - and the line says exactly what changed.
            FikaGlobals.LogWarning("[MainMenuUI] the engine no longer holds the panel this script handed it "
                + $"(parentMatches={parentMatches}, componentMatches={componentMatches}, active={active}); "
                + "the panel is destroyed in this frame instead of being used");
            
            TeardownPanel("engine view changed: parentMatches=" + parentMatches
                + ", componentMatches=" + componentMatches + ", active=" + active,
                stopCoroutines: true, protecting: true);
        }
        catch (Exception exception)
        {
            
        }
    }

    // SPT 5's menu canvas applies a larger scale factor than the Mono menu
    // used by the original Fika prefab. Keep the user setting as a relative
    // scale, but apply the corrected prefab baseline so existing config files
    // (which contain the old default of 1.0) are migrated automatically.
    private static float GetOnlinePlayersScale()
    {
        var configured = FikaPlugin.Instance.Settings.OnlinePlayersScale.Value;
        return Math.Clamp(configured * 0.75f, 0.4f, 1.0f);
    }

    private DateTime BackendTime
    {
        get
        {
            if (_backendSession == null)
            {
                return DateTime.MinValue;
            }
            return _backendSession.GetCurrentLocationTime.ToManaged();
        }
    }

    private DateTime StaticTime
    {
        get
        {
            return new DateTime(2016, 8, 4, 15, 28, 0);
        }
    }

    protected void Start()
    {
        
        PumpStart();
    }

    /// <summary>
    /// Engine callback: the engine's first touch of a freshly added behaviour. The step goes to
    /// the stable file (not only to BepInEx's, which the next start truncates) so the order
    /// "engine Awake -> patch Attach -> engine Start -> Initialize" stays provable after a fault.
    /// </summary>
    private void Awake()
    {
        
    }

    private int _engineTicks;

    /// <summary>
    /// Engine heartbeat (bounded: first frame, then every 300th), written to the stable file.
    /// The engine calls into this injected behaviour every frame; if the heartbeat is the last
    /// line before a fault, the engine still drives the behaviour and the panel objects are the
    /// remaining suspects, and if it keeps ticking while the panel objects go bad, the handover
    /// of those objects is what broke.
    /// </summary>
    private void Update()
    {
        _engineTicks++;
        if (_engineTicks == 1 || _engineTicks % 300 == 0)
        {
            
        }

        // The panel itself runs here (no engine coroutine is involved any more).
        try
        {
            Pump();
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// Native il2cpp object that cannot be dereferenced. Il2CppInterop hands these out for
    /// unrealised Unity objects and for references wired up by a bundle or prefab, and every
    /// member access on one faults inside GameAssembly (0xC0000005) instead of throwing a
    /// managed exception. The 47888 dumps fault at GameAssembly+0x2BE60 ("mov rax,[rcx]")
    /// reading address 0x0A - a small integer, not null, where an object pointer was expected -
    /// so the guard rejects both null and those non-pointer values (NativeRefGuard).
    /// </summary>
    private static bool IsAlive(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase instance)
    {
        return NativeRefGuard.IsUsable(instance);
    }

    /// <summary>
    /// Panel step written to the stable diagnostics file as well as to BepInEx's log.
    ///
    /// BepInEx/LogOutput.log is truncated on every game start, so a step that was only written
    /// there is gone by the time a crash is reported - the panel's progress therefore has to be
    /// in Fika-diagnostics.log as well: the last line before a fault is what names the step that
    /// broke.
    /// </summary>
    private static void PanelStep(string message)
    {
        FikaGlobals.LogInfo(message);
        
    }

    // A cosmetic panel must not fill the log with the same complaint every ten seconds.
    private static void ReportOnce(ref bool alreadyReported, string message)
    {
        if (alreadyReported)
        {
            return;
        }

        alreadyReported = true;
        FikaGlobals.LogWarning(message);
    }

    /// <summary>
    /// One poll of the menu canvas, with the raw pointer of every native object it reads.
    ///
    /// The fault this panel chases is an engine -> il2cpp call with a bogus object pointer, so
    /// the objects are written to the stable diagnostics file *before* they are used: the last
    /// line before a fault then names the object the engine was holding. Nothing is cached
    /// between polls - the singleton, the instance and the menu screen are re-read and re-proved
    /// on every iteration, so a canvas the engine recreated is never followed: until the fresh
    /// chain proves itself the poll answers "not ready" and says why.
    /// </summary>
    private static bool PollMenuCanvas(out string verdict, out IntPtr commonUiPointer, out IntPtr menuScreenPointer)
    {
        commonUiPointer = IntPtr.Zero;
        menuScreenPointer = IntPtr.Zero;
        CommonUI commonUi;

        try
        {
            if (!Singleton<CommonUI>.Instantiated)
            {
                verdict = "CommonUI is not instantiated yet";
                return false;
            }

            commonUi = Singleton<CommonUI>.Instance;
        }
        catch (Exception exception)
        {
            verdict = "reading the CommonUI singleton failed: " + exception.Message;
            return false;
        }

        if (commonUi is not null)
        {
            commonUiPointer = commonUi.Pointer;
        }

        // A null check is not enough: "destroyed but not null" is exactly the state the engine
        // side fault produces, so the reference has to be a readable il2cpp object of the
        // expected type before any member of it is touched.
        if (!NativeRefGuard.IsProvablyUsable(commonUi, "CommonUI", out string commonUiDetail))
        {
            verdict = "CommonUI instance is not provably usable: " + commonUiDetail;
            return false;
        }

        MenuScreen menuScreen;
        try
        {
            menuScreen = commonUi.MenuScreen;
        }
        catch (Exception exception)
        {
            verdict = "reading CommonUI.MenuScreen failed: " + exception.Message;
            return false;
        }

        if (menuScreen is not null)
        {
            menuScreenPointer = menuScreen.Pointer;
        }

        if (!NativeRefGuard.IsProvablyUsable(menuScreen, "MenuScreen", out string menuScreenDetail))
        {
            verdict = "MenuScreen is not provably usable: " + menuScreenDetail;
            return false;
        }

        verdict = "commonUi=" + commonUiDetail + ", menuScreen=" + menuScreenDetail;
        return true;
    }

    /// <summary>
    /// Arms the panel pump and writes the same first step line the coroutine used to write.
    ///
    /// The panel deliberately does not run as a managed coroutine any more. The engine crashed
    /// while it processed exactly that object: an <c>Il2CppManagedEnumerator</c> - the wrapper
    /// <c>StartCoroutine(IEnumerator)</c> hands the engine for a managed coroutine - and the
    /// first-chance probe caught it inside the engine's own frames while the engine dereferenced
    /// the bogus object 0xA for it. Nothing the panel does needs the engine's coroutine
    /// machinery, so the whole panel is driven by this behaviour's Update instead.
    /// </summary>
    private void PumpStart()
    {
        _pumpArmed = true;
        _pumpAttached = false;
        _pumpStage = 0;
        _pumpPolls = 0;
        _pumpElapsed = 0f;
        _pumpWasReady = false;
        _lastPumpInstance = IntPtr.Zero;
        _lastPumpMenuScreen = IntPtr.Zero;
        _nextRefreshTime = 0f;
        _watchdogStartFrame = -1;
        _watchdogMark = 0;
        _players = [];
        _lastRefresh = DateTime.Now;
        _lastSet = DateTime.Now;
        _pendingRefreshSource = null;
        _lastRosterSignature = null;
        _labelCount = -1;
        _lastRefreshReport = null;
        
    }

    /// <summary>
    /// One pump step per engine frame. Stage 0 waits for the locales and the menu canvas and
    /// builds the panel; stage 1 repeats the refresh on the panel's own cycle (see
    /// <see cref="NextRefreshDelay"/>: a few seconds while the panel is visible, the ten seconds
    /// it always used while it is not) and writes the watchdog marks. This is the same code the
    /// coroutine ran - only "wait for the next frame" became "return and be called again by
    /// Update".
    /// </summary>
    private void Pump()
    {
        if (!_pumpArmed)
        {
            return;
        }

        if (!_pumpAttached)
        {
            _pumpAttached = true;
            // Root the injected behaviour before anything waits or allocates: the engine may
            // call OnEnable/OnDisable on it at any time from now on.
            Attach(this);
        }

        if (_pumpStage == 0)
        {
            if (!FikaPlugin.Instance.LocalesLoaded)
            {
                if (FikaPlugin.Instance.InitializationError != null)
                {
                    _pumpArmed = false;
                }

                return;
            }

            _pumpPolls++;
            _pumpElapsed += Time.unscaledDeltaTime;
            var ready = PollMenuCanvas(out string verdict, out var commonUiPointer, out var menuScreenPointer);
            var instanceChanged = _lastPumpInstance != IntPtr.Zero && commonUiPointer != IntPtr.Zero && _lastPumpInstance != commonUiPointer;
            var menuScreenChanged = _lastPumpMenuScreen != IntPtr.Zero && menuScreenPointer != IntPtr.Zero && _lastPumpMenuScreen != menuScreenPointer;

            if (_pumpPolls <= 5 || ready != _pumpWasReady || instanceChanged || menuScreenChanged || _pumpPolls % 60 == 0)
            {
                
            }

            _lastPumpInstance = commonUiPointer != IntPtr.Zero ? commonUiPointer : _lastPumpInstance;
            _lastPumpMenuScreen = menuScreenPointer != IntPtr.Zero ? menuScreenPointer : _lastPumpMenuScreen;
            _pumpWasReady = ready;

            if (!ready)
            {
                if (_pumpElapsed > 30f)
                {
                    FikaGlobals.LogError("[MainMenuUI] CommonUI/MenuScreen never became available; the online players panel is disabled");
                    
                    _pumpArmed = false;
                }

                return;
            }

            PanelStep("[MainMenuUI] menu canvas is ready, building the online players panel");

            // The class layout the reference guard reads is confirmed on a reference whose type
            // is known before any serialized prefab reference is trusted with it.
            if (Singleton<CommonUI>.Instantiated)
            {
                NativeRefGuard.VerifyTypeProbe(Singleton<CommonUI>.Instance, "CommonUI");
            }

            // Coop / 'Enable Online Players' is the panel's own kill switch: with it off nothing
            // of the prefab is instantiated and no bundle reference is touched at all.
            if (!(FikaPlugin.Instance.Settings.EnableOnlinePlayers?.Value ?? true))
            {
                FikaGlobals.LogWarning("[MainMenuUI] the online players panel is disabled in the config (Coop / 'Enable Online Players' = false); the panel prefab is not created");
                _pumpArmed = false;
                return;
            }

            if (TarkovApplication.Exist(out var tarkovApplication) && tarkovApplication.Session != null)
            {
                _backendSession = tarkovApplication.Session;
            }

            CreateMainMenuUI();
            _initialized = true;
            OnEnable();
            _pumpStage = 1;
            _nextRefreshTime = 0f;
            return;
        }

        // Stage 1: the refresh timer and the post-refresh watchdog marks (the old watchdog
        // coroutine) - both are plain frame counters now.
        if (_watchdogStartFrame >= 0 && _watchdogMark < _watchdogMarks.Length)
        {
            var elapsedFrames = Time.frameCount - _watchdogStartFrame;
            if (elapsedFrames >= _watchdogMarks[_watchdogMark])
            {
                
                _watchdogMark++;
                if (_watchdogMark >= _watchdogMarks.Length)
                {
                    _watchdogStartFrame = -1;
                }
            }
        }

        if (Time.unscaledTime < _nextRefreshTime)
        {
            return;
        }

        // [refresh] The cycle belongs to the panel now: a few seconds while it is visible (Coop /
        // 'Online Players Refresh Seconds', default 3) and the ten seconds it always used while it
        // is not (Coop / 'Online Players Idle Refresh Seconds'). The timer is moved forward *before*
        // the request, so an answer that takes a while - the funnel was measured between 12 ms and
        // 1.6 s - cannot start a second request in the same frame.
        var visible = IsPanelVisible();
        var delay = NextRefreshDelay(visible);
        var source = _pendingRefreshSource ?? "poll";
        _pendingRefreshSource = null;
        _nextRefreshTime = Time.unscaledTime + delay;
        _waitingForPresenceSocket = false;
        _refreshTick++;
        _watchdogStartFrame = Time.frameCount;
        _watchdogMark = 0;

        try
        {
            ClearAndQueryPlayers(_refreshTick, source, delay, false);
        }
        catch (Exception exception)
        {
            // A managed failure here (server down, missing profile) must not kill the pump.
            ReportOnce(ref _refreshReported, $"[MainMenuUI] the player list could not be refreshed: {exception}");
        }
    }

    /// <summary>
    /// The presence list is the server's online list, and a player only enters that list while
    /// the <c>/fika/notification/</c> websocket is connected (its connection handler registers
    /// the player) - so an empty answer right after the menu opened is expected while that
    /// socket is still connecting, and the panel must not sit on the "0 players" result for a
    /// whole tick. Logged once: the reason is the same for every retry.
    /// </summary>
    private void ScheduleFasterRefreshWhenEmpty(int received)
    {
        _waitingForPresenceSocket = received == 0
            && FikaNotificationManager.Exists
            && !FikaNotificationManager.IsConnected;
        if (!_waitingForPresenceSocket)
        {
            return;
        }

        ReportOnce(ref _presenceSocketReported, "[MainMenuUI] no one is listed yet and the notification websocket is still connecting; "
            + "the list is asked again in a second (the server registers a player when that socket connects)");
    }

    /// <summary>
    /// True while the panel this behaviour built is on screen.
    ///
    /// Update only runs while the engine has this behaviour enabled and active, so the case left
    /// here is the content object the engine re-activates in <see cref="OnEnable"/>: while a raid
    /// or a loading screen is up the whole menu screen is inactive and the panel does not poll at
    /// all - that is where the "no requests while it cannot be seen" half comes from.
    /// </summary>
    private bool IsPanelVisible()
    {
        if (!_panelCreated || !IsAlive(_userInterface))
        {
            return false;
        }

        try
        {
            return _userInterface.activeInHierarchy;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The delay until the next poll.
    ///
    /// 'Coop / Online Players Refresh Seconds' (default 3) is the cycle while the panel is
    /// visible: the list is a live roster, so a player who joins or leaves has to appear within a
    /// few seconds. An unchanged cycle costs one HTTP GET - the rows are only rebuilt when the
    /// roster really changed (see <see cref="ClearAndQueryPlayers"/>) - which is what keeps that
    /// cycle cheap for the server and for the frame. 'Coop / Online Players Idle Refresh Seconds'
    /// (default 10) is the cycle while the panel is not visible and keeps the ten seconds the
    /// panel always used.
    ///
    /// 'Coop / Online Players Fast Refresh' off returns the old fixed ten second cycle: one flip
    /// puts the panel back on the pre-change cadence.
    /// </summary>
    private float NextRefreshDelay(bool visible)
    {
        // Unchanged: an empty roster while the presence websocket - the socket whose connection
        // is what puts this player into the server's online list - has not connected yet is asked
        // again after a second instead of after a whole cycle.
        if (_waitingForPresenceSocket)
        {
            return 1f;
        }

        if (!FastRefreshEnabled())
        {
            return 10f;
        }

        var settings = SettingsOrNull();
        return visible
            ? Math.Clamp(SettingInt(settings is null ? null : settings.OnlinePlayersRefreshSeconds, 3), 1, 60)
            : Math.Clamp(SettingInt(settings is null ? null : settings.OnlinePlayersIdleRefreshSeconds, 10), 1, 300);
    }

    /// <summary>
    /// 'Coop / Online Players Fast Refresh': the one switch that puts the panel back on the
    /// pre-change path (the fixed ten second cycle and a full rebuild on every cycle). A missing
    /// entry - an older config file, a setting that is not bound yet - keeps the shipped default.
    /// </summary>
    private static bool FastRefreshEnabled()
    {
        var settings = SettingsOrNull();
        return settings is null || (settings.OnlinePlayersFastRefresh?.Value ?? true);
    }

    private static FikaConfig SettingsOrNull()
    {
        var plugin = FikaPlugin.Instance;
        return plugin is null ? null : plugin.Settings;
    }

    private static int SettingInt(BepInEx.Configuration.ConfigEntry<int> entry, int fallback)
    {
        return entry is null ? fallback : entry.Value;
    }

    /// <summary>
    /// A stable identity of the roster the server just answered: everything a row of the panel
    /// shows (who is online, their level, what they are doing and - for someone in a raid - which
    /// raid, on which side, in which location and whether it started yet).
    ///
    /// The answer is a dictionary dump on the server ("/fika/presence/get" returns
    /// PresenceService.AllPlayersPresence), so its order is not stable; the entries are sorted
    /// before they are hashed, otherwise a reordered answer would look like a change and rebuild
    /// every row - and re-register every join listener - for nothing.
    /// </summary>
    private static string RosterSignature(FikaPlayerPresence[] responses)
    {
        if (responses is null || responses.Length == 0)
        {
            return "0";
        }

        var entries = new List<string>(responses.Length);
        foreach (var presence in responses)
        {
            entries.Add(presence.RaidInformation is { } raid
                ? $"{presence.Nickname}|{presence.Level}|{presence.Activity}|{raid.MatchId}|{raid.Started}|{raid.Location}|{raid.Side}"
                : $"{presence.Nickname}|{presence.Level}|{presence.Activity}|-");
        }

        entries.Sort(StringComparer.Ordinal);

        var hash = 2166136261u;
        foreach (var entry in entries)
        {
            foreach (var character in entry)
            {
                hash = (hash ^ (uint)character) * 16777619u;
            }

            hash = (hash ^ 0x1Fu) * 16777619u;
        }

        return responses.Length.ToString() + ":" + hash.ToString("X8");
    }

    /// <summary>
    /// Whether the rows have to be built again. The rows are what the panel shows, so they are
    /// only kept when they provably *are* the answer: the roster identity is the same, the panel
    /// holds one row per listed player and every one of those rows is still a usable native
    /// object. A row the engine destroyed, a row that could not be wired or a roster that was
    /// never drawn sends the cycle through the full path.
    /// </summary>
    private bool RebuildNeeded(bool changed, int players)
    {
        // The pre-change path behind the master switch: every cycle rebuilds everything.
        if (!FastRefreshEnabled())
        {
            return true;
        }

        if (changed || _players is null || _players.Count != players)
        {
            return true;
        }

        foreach (var player in _players)
        {
            if (!IsAlive(player))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One greppable line per refresh state change:
    /// <c>[OnlinePlayers] refresh source=open|poll|change|manual players=N took=..ms next=..s render=..</c>.
    ///
    /// It answers what the next round has to settle - is the list timely, and what costs what -
    /// with the trigger, the roster size, the time the refresh held the main thread and the delay
    /// scheduled for the next poll. It is written only when it differs from the previous line (the
    /// timing is excluded from the comparison), so a steady roster on a three second cycle writes
    /// it once instead of twenty times a minute.
    /// </summary>
    private void ReportRefresh(string source, int players, long tookMs, float nextSeconds, string detail)
    {
        var fields = "source=" + source + " players=" + players + " next=" + nextSeconds.ToString("0.#") + "s " + detail;
        if (fields == _lastRefreshReport)
        {
            return;
        }

        _lastRefreshReport = fields;
        var line = "[OnlinePlayers] refresh " + fields + " took=" + tookMs + "ms";
        FikaGlobals.LogInfo(line);
        
    }

    protected void OnEnable()
    {
        // Unity calls OnEnable before Start, when config/UI may not exist yet.
        if (!_initialized) return;
        // Panel creation can fail (see CreateMainMenuUI); there is nothing to toggle then.
        if (_userInterface is null) return;
        if (!IsAlive(_userInterface))
        {
            ReportOnce(ref _panelReported, "[MainMenuUI] the panel content object is no longer a usable native object; the online players panel stays off");
            return;
        }

        if (!FikaPlugin.Instance.Settings.EnableOnlinePlayers.Value)
        {
            _userInterface.SetActive(false);
            return;
        }

        _userInterface.SetActive(true);
        

        // Everything the engine now owns: the panel root, the content the engine activates, the
        // injected component and the transform the scale is applied to.
        Root("panel", _panelObject);
        Root("content", _userInterface);
        Root("panel component", _mainMenuUI);
        Root("scale target", _transformToScale);

        if (IsAlive(_transformToScale))
        {
            var scale = GetOnlinePlayersScale();
            _transformToScale.localScale = new(scale, scale, scale);
        }
        else
        {
            ReportOnce(ref _panelReported, "[MainMenuUI] the panel content transform is no longer usable; the configured scale is not applied");
        }

        if (!IsAlive(_mainMenuUI))
        {
            ReportOnce(ref _panelReported, "[MainMenuUI] the panel component is no longer usable; the player list is not refreshed");
            return;
        }

        // Arm the refresh pump: it runs from this behaviour's Update, so the panel does not
        // hand the engine an enumerator to drive (that wrapper is the object the engine
        // crashed on).
        //
        // [fix] The engine disables this behaviour whenever the menu screen it lives on leaves
        // the screen (a raid, a loading screen, a screen switch) and re-enables it on the way
        // back, and OnDisable had disarmed the pump: only the stage was restored here, so the
        // panel polled until the first disable and then sat on the rows it had for the rest of
        // the session - the reported "the online player list does not refresh". Update only runs
        // while this behaviour is enabled and active, so re-arming it here is exactly the state
        // the engine is asking for.
        _pumpArmed = true;
        _pumpStage = 1;
        // [refresh] The panel was just (re)opened: the next pump tick refreshes it at once
        // instead of finishing the current cycle, and the diagnostic line says so (source=open).
        _pendingRefreshSource = "open";
        _nextRefreshTime = 0f;

        PanelStep("[MainMenuUI] refresh pump armed (Update driven, no engine coroutine)");
    }

    protected void OnDisable()
    {
        // Disarm the pump; there is no engine coroutine to stop any more.
        StopCoroutines();
    }

    protected void OnDestroy()
    {
        // The menu screen (and with it this behaviour) is destroyed on every screen change; 
        // The behaviour is being destroyed: its coroutines go with it, and calling StopCoroutine
        // here would touch a dying native object.
        TeardownPanel("the menu screen behaviour was destroyed", stopCoroutines: false, protecting: false);
    }

    // The post-refresh watchdog marks are written by Pump (stage 1) from a frame counter; the
    // coroutine that used to write them is gone with the rest of the engine-driven coroutines.

    private void CreateMainMenuUI()
    {
        // Every access below used to be unconditional. A missing asset or an unrealised
        // Unity object turns into a native access violation inside il2cpp rather than a
        // managed exception, so each step is verified, reported and allowed to fail
        // without taking the client down: this panel is cosmetic.
        try
        {
            PanelStep("[MainMenuUI] 1/8 loading the prefab");
            var mainMenuUIPrefab = InternalBundleLoader.Instance.GetFikaAsset(InternalBundleLoader.EFikaAsset.MainMenuUI);
            
            if (!IsAlive(mainMenuUIPrefab))
            {
                FikaGlobals.LogError("[MainMenuUI] MainMenuUI.prefab is unavailable; the online players panel is disabled");
                return;
            }

            PanelStep("[MainMenuUI] 2/8 instantiating the panel");
            var mainMenuUI = GameObject.Instantiate(mainMenuUIPrefab);
            // Keep the engine-owned panel and its content strongly rooted from the moment they
            // exist: from here on the engine can be the only other holder.
            _panelObject = IsAlive(mainMenuUI) ? mainMenuUI : null;
            Root("panel", _panelObject);
            
            if (!IsAlive(mainMenuUI))
            {
                FikaGlobals.LogError("[MainMenuUI] the panel prefab could not be instantiated; the online players panel is disabled");
                return;
            }

            PanelStep("[MainMenuUI] 3/8 binding the MainMenuUI component");
            _mainMenuUI = mainMenuUI.GetComponent<MainMenuUI>();
            
            if (!IsAlive(_mainMenuUI))
            {
                AbortPanelCreation("[MainMenuUI] the panel prefab has no bound MainMenuUI component; the online players panel is disabled");
                return;
            }

            Root("panel component", _mainMenuUI);

            PanelStep("[MainMenuUI] 4/8 reading the serialized references");
            var playerTemplateField = _mainMenuUI.PlayerTemplate;
            if (playerTemplateField is null)
            {
                AbortPanelCreation("[MainMenuUI] MainMenuUI.PlayerTemplate was not injected; the online players panel is disabled");
                return;
            }

            var playerTemplate = playerTemplateField.Value;
            
            _playerTemplate = IsAlive(playerTemplate) ? playerTemplate : null;
            Root("player template", _playerTemplate);
            if (_playerTemplate is null)
            {
                FikaGlobals.LogWarning("[MainMenuUI] MainMenuUI.PlayerTemplate is empty in the prefab; the player list stays empty");
            }

            PanelStep("[MainMenuUI] 5/8 checking the panel label");
            // MainMenuUI.UpdateLabel writes straight into this TextMeshProUGUI. A field that
            // holds something which is not an object pointer (the dumps show 0x0A) turns that
            // write into a native dereference, so it is verified here - and again on every
            // refresh - instead of being trusted.
            var labelField = _mainMenuUI.Label;
            var label = labelField is null ? null : labelField.Value;
            // Plausibility is not enough: the value has to be a readable il2cpp object of the
            // expected type, otherwise the write corrupts whatever it really points at.
            _labelUsable = NativeRefGuard.IsProvablyUsable(label, "TextMeshProUGUI", out _labelDetail);
            // Cache the wrapper the count is written into, so no tick has to read the serialized
            // reference field (and allocate a wrapper) again.
            _label = _labelUsable ? label : null;
            Root("label", _label);
            
            if (!_labelUsable)
            {
                FikaGlobals.LogWarning($"[MainMenuUI] MainMenuUI.Label is not a provably usable native object ({_labelDetail}); the online player counter is skipped instead of being written");
            }

            PanelStep("[MainMenuUI] 6/8 attaching the panel to the menu screen");
            var commonUiForScreen = Singleton<CommonUI>.Instance;
            if (!NativeRefGuard.IsProvablyUsable(commonUiForScreen, "CommonUI", out var screenDetail))
            {
                AbortPanelCreation("[MainMenuUI] the CommonUI instance is not provably usable (" + screenDetail + "); the online players panel is disabled");
                return;
            }

            var menuScreen = commonUiForScreen.MenuScreen;
            
            if (!IsAlive(menuScreen))
            {
                AbortPanelCreation("[MainMenuUI] MenuScreen disappeared before the panel could be attached; the online players panel is disabled");
                return;
            }

            _menuScreenTransform = menuScreen.gameObject.transform;
            Root("menu screen transform", _menuScreenTransform);

            // Both the attachment target and the prefab hierarchy are walked one object at a
            // time: the previous version passed menuScreen.gameObject.transform straight to
            // SetParent and then indexed two children without checking either of them.
            var menuScreenTransform = menuScreen.gameObject.transform;
            
            if (!IsAlive(menuScreenTransform))
            {
                AbortPanelCreation("[MainMenuUI] the menu screen has no usable transform; the online players panel is disabled");
                return;
            }

            mainMenuUI.transform.SetParent(menuScreenTransform, false);

            var mainMenuUITransform = _mainMenuUI.gameObject.transform;
            
            _panelTransform = mainMenuUITransform;
            Root("panel transform", _panelTransform);
            var panelChildCount = IsAlive(mainMenuUITransform) ? mainMenuUITransform.childCount : -1;
            
            var contentParent = IsAlive(mainMenuUITransform) && mainMenuUITransform.childCount > 0
                ? mainMenuUITransform.GetChild(0)
                : null;
            if (!IsAlive(contentParent) || contentParent.childCount < 1)
            {
                AbortPanelCreation("[MainMenuUI] the panel prefab hierarchy no longer matches (expected two nested children); the online players panel is disabled");
                return;
            }

            var contentChild = contentParent.GetChild(0);
            var objectToAttach = IsAlive(contentChild) ? contentChild.gameObject : null;
            
            if (!IsAlive(objectToAttach))
            {
                AbortPanelCreation("[MainMenuUI] the panel content object is missing; the online players panel is disabled");
                return;
            }

            _userInterface = objectToAttach;
            Root("content", _userInterface);

            PanelStep("[MainMenuUI] 7/8 preparing the draggable content");
            _transformToScale = objectToAttach.RectTransform();
            
            if (!IsAlive(_transformToScale))
            {
                AbortPanelCreation("[MainMenuUI] the panel content has no RectTransform; the online players panel is disabled");
                return;
            }

            Root("scale target", _transformToScale);

            var scale = GetOnlinePlayersScale();
            _transformToScale.localScale = new(scale, scale, scale);

            var dragComponent = objectToAttach.AddComponent<UIDragComponent>();
            
            // The engine owns the component; the managed side of the instance it created stays
            // reachable through this field (see the field declaration).
            _dragComponent = dragComponent;
            Root("drag component", _dragComponent);
            if (IsAlive(dragComponent))
            {
                dragComponent.Init(_transformToScale, true);
            }
            else
            {
                FikaGlobals.LogWarning("[MainMenuUI] UIDragComponent could not be added; the panel stays fixed");
            }

            PanelStep("[MainMenuUI] 8/8 wiring the refresh button");
            var refreshButtonField = _mainMenuUI.RefreshButton;
            if (refreshButtonField is null)
            {
                AbortPanelCreation("[MainMenuUI] MainMenuUI.RefreshButton was not injected; the panel is read-only");
                return;
            }

            var refreshButton = refreshButtonField.Value;
            
            if (!IsAlive(refreshButton))
            {
                AbortPanelCreation("[MainMenuUI] MainMenuUI.RefreshButton is empty in the prefab; the panel cannot be refreshed manually");
                return;
            }

            // The engine holds this listener in a native UnityEvent: keep the CLR delegate (and
            // therefore its target) rooted until DestroyPanel removes it again.
            _refreshButton = refreshButton;
            _refreshListener = ManualRefresh;
            Fika.Core.UnityEventCompatExtensions.AddListener(refreshButton.onClick, _refreshListener);
            Root("refresh button", _refreshButton);
            Root("refresh listener", _refreshListener);
            _panelCreated = true;
            PanelStep("[MainMenuUI] online players panel created");
            
        }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"[MainMenuUI] the online players panel could not be created: {exception}");
        }
    }

    private void ManualRefresh()
    {
        if ((DateTime.Now - _lastRefresh).TotalSeconds < 5)
        {
            return;
        }

        _lastRefresh = DateTime.Now;
        FikaGlobals.LogInfo("[MainMenuUI] refresh: manual refresh requested");
        // [refresh] The click is a refresh like any other one: it does not queue a second request,
        // and the cycle restarts from it instead of firing again a moment later. The full rebuild
        // this button always did is kept - a manual refresh rebuilds the rows even when the answer
        // is the one the panel already shows.
        _pendingRefreshSource = null;
        var delay = NextRefreshDelay(IsPanelVisible());
        _nextRefreshTime = Time.unscaledTime + delay;
        try
        {
            ClearAndQueryPlayers(0, "manual", delay, true);
        }
        catch (Exception exception)
        {
            ReportOnce(ref _refreshReported, $"[MainMenuUI] the player list could not be refreshed: {exception}");
        }
    }

    /// <summary>
    /// One refresh: ask the server for the online list and put it on screen.
    ///
    /// Exactly one request belongs to one refresh - <see cref="Pump"/> fires one per cycle and
    /// <see cref="ManualRefresh"/> is the player's own click - and the pump moves its timer
    /// forward before the request is sent, so a slow answer cannot start a second one.
    ///
    /// The answer is reduced to a roster identity first (<see cref="RosterSignature"/>). Building
    /// the rows is the expensive half of a refresh (measured on 2026-10-12: 579-704 ms for the
    /// rows, 137-172 ms for the counter write, against 11-1614 ms for the request itself,
    /// analysis/online-players-refresh), so the rows are only rebuilt when the roster the panel
    /// shows is not the roster the server just answered. An unchanged cycle therefore costs one
    /// HTTP GET and keeps its rows - nobody's join button or tooltip is re-registered for a list
    /// that did not move - while a change is rendered in the very tick it was observed, never one
    /// cycle later.
    /// </summary>
    private void ClearAndQueryPlayers(int tick, string source, float nextSeconds, bool rebuildAlways)
    {
        // Self protection on every tick, independent of the trace flag: a panel whose objects
        // the engine dropped is destroyed instead of being used.
        if (!EnsurePanelUsable())
        {
            return;
        }

        var clock = Stopwatch.StartNew();

        _refreshTick = tick;
        
        if (tick <= 2)
        {
            PanelStep($"[MainMenuUI] refresh: requesting the online player presences (tick {tick}, frame {Time.frameCount})");
        }

        var response = FikaRequestHandler.GetPlayerPresences();
        if (response is null)
        {
            ReportOnce(ref _refreshReported, "[MainMenuUI] the server returned no player presence list; the panel keeps the previous content");
            
            // The rows are not touched before the answer arrives any more, so "the panel keeps the
            // previous content" is what actually happens here.
            ReportRefresh(source, -1, clock.ElapsedMilliseconds, nextSeconds, "render=none note=no-response");
            return;
        }

        
        if (tick <= 2)
        {
            PanelStep($"[MainMenuUI] refresh: {response.Length} presence(s) received (tick {tick}, frame {Time.frameCount})");
        }
        ScheduleFasterRefreshWhenEmpty(response.Length);

        var signature = RosterSignature(response);
        var changed = signature != _lastRosterSignature;
        var rebuild = rebuildAlways || RebuildNeeded(changed, response.Length);
        // A cycle that found a change is reported as the change it is; "open" and "manual" stay in
        // front of it because they say who asked for the refresh in the first place.
        var reportedSource = changed && source == "poll" ? "change" : source;

        if (rebuild)
        {
            _lastRosterSignature = signature;

            // Remove the native listeners first: the rows are about to be destroyed, and a native
            // event that still holds a delegate to a destroyed row is exactly the "engine calls an
            // object whose managed side is gone" state this build is chasing.
            RemoveRowListeners();
            DestroyRows();
        }

        // The counter is written when the rows are rebuilt (the previous behaviour) and when the
        // number it shows is not the number the server just returned - a write that failed, or a
        // roster that did not change but was never counted.
        if (rebuild || _labelCount != response.Length)
        {
            _labelCount = UpdateLabel(response.Length) ? response.Length : -1;
            
        }

        if (rebuild)
        {
            SetupPlayers(ref response);
            
        }
        else
        {
            
        }

        if (tick <= 2)
        {
            PanelStep($"[MainMenuUI] refresh: the refresh funnel returned (tick {tick}, frame {Time.frameCount}); the coroutine is about to yield");
        }
        // The watchdog marks are driven by Pump from now on; this is still the last line of the
        // funnel: what the *engine* still holds for this panel.
        VerifyEngineView();

        ReportRefresh(reportedSource, response.Length, clock.ElapsedMilliseconds, nextSeconds,
            (rebuild ? "render=rows rows=" : "render=kept rows=") + (_players?.Count ?? -1));
    }

    /// <summary>
    /// Writes the online player count into the panel label. Returns false when nothing was written
    /// (the component or the label is not usable, or the counter is switched off), so the caller
    /// can tell "the counter shows this number" from "the counter is unknown" and writes it again
    /// on the next cycle instead of assuming it did.
    /// </summary>
    private bool UpdateLabel(int amount)
    {
        // Two barriers: the component itself, then the label the write goes into. The label
        // is re-verified on every tick - it is a serialized prefab reference and the only
        // native write this panel performs after the prefab was instantiated.
        if (!IsAlive(_mainMenuUI))
        {
            return false;
        }

        if (!(FikaPlugin.Instance.Settings.WriteOnlinePlayerCounter?.Value ?? true))
        {
            ReportOnce(ref _counterReported, "[MainMenuUI] the online player counter write is disabled in the config (Coop / 'Write Online Player Counter' = false)");
            return false;
        }

        // The label wrapper is cached (read from the prefab once at creation time): reading the
        // serialized reference field again here would allocate another wrapper every tick.
        var label = _label;
        _labelUsable = NativeRefGuard.IsProvablyUsable(label, "TextMeshProUGUI", out _labelDetail);
        if (!_labelUsable)
        {
            ReportOnce(ref _labelReported, $"[MainMenuUI] the online player counter is not updated: MainMenuUI.Label is not provably usable ({_labelDetail})");
            
            return false;
        }

        
        var written = _mainMenuUI.UpdateLabel(label, amount);
        
        return written;
    }

    private string ConvertToTime(EDateTime dateTime, bool staticTime)
    {
        if (staticTime)
        {
            var staticDate = StaticTime;
            return dateTime == EDateTime.CURR ? staticDate.ToString("HH:mm:ss") : staticDate.AddHours(-12).ToString("HH:mm:ss");
        }

        var backendTime = BackendTime;
        if (backendTime == DateTime.MinValue)
        {
            return "ERROR";
        }
        return dateTime == EDateTime.CURR ? backendTime.ToString("HH:mm:ss") : backendTime.AddHours(-12).ToString("HH:mm:ss");
    }

    private bool IsStaticTimeLocation(string location)
    {
        return location is "factory4_day" or "factory4_night" or "laboratory";
    }

    private void SetupPlayers(ref FikaPlayerPresence[] responses)
    {
        if (responses is null || responses.Length == 0)
        {
            FikaGlobals.LogInfo("[MainMenuUI] refresh: no players online, the panel shows the counter only");
            
            return;
        }

        // The label is enough when the prefab carries no player template.
        if (!IsAlive(_playerTemplate))
        {
            ReportOnce(ref _templateReported, $"[MainMenuUI] the player template is not a usable native object ({NativeRefGuard.Describe(_playerTemplate)}); the panel shows the counter only");
            return;
        }

        var playerParent = ResolveTemplateParent();
        var created = 0;
        var partial = 0;
        var skipped = 0;

        foreach (var presence in responses)
        {
            GameObject newPlayer;
            try
            {
                // Handing the old prefab parent to Instantiate is only safe once it proved to
                // be a real object pointer. A parent reference that is not (the dumps show
                // 0x0A) makes the engine store garbage for the new hierarchy and dereference
                // it later, which is the 47888 crash pattern, so a broken parent is dropped
                // instead of being passed on.
                newPlayer = playerParent is null
                    ? GameObject.Instantiate(_playerTemplate)
                    : GameObject.Instantiate(_playerTemplate, playerParent);
            }
            catch (Exception exception)
            {
                skipped++;
                ReportOnce(ref _rowReported, $"[MainMenuUI] a player row could not be instantiated: {exception}");
                continue;
            }

            if (!IsAlive(newPlayer))
            {
                skipped++;
                ReportOnce(ref _rowReported, "[MainMenuUI] a player row was created but is not a usable native object; it is not shown");
                continue;
            }

            var mainMenuUIPlayer = newPlayer.GetComponent<MainMenuUIPlayer>();
            if (!IsAlive(mainMenuUIPlayer))
            {
                skipped++;
                GameObject.Destroy(newPlayer);
                ReportOnce(ref _rowReported, "[MainMenuUI] a player row has no bound MainMenuUIPlayer component; it is not shown");
                continue;
            }

            // The engine created this component from the bundle, so it is a Fika-injected class
            // instance without a GC handle of its own: without this root its managed side could
            // be collected while the engine still holds the row.
            _rowComponents.Add(mainMenuUIPlayer);
            Root("row component", mainMenuUIPlayer);

            if (!mainMenuUIPlayer.SetActivity(presence.Nickname, presence.Level, presence.Activity))
            {
                partial++;
                ReportOnce(ref _rowReported, "[MainMenuUI] a player row has unwired references; only the usable fields are filled in");
            }

            if (presence.Activity is EFikaPlayerPresence.IN_RAID && presence.RaidInformation.HasValue)
            {
                var information = presence.RaidInformation.Value;
#if DEBUG
                FikaGlobals.LogInfo($"Got presense: {information.Started}, {information.MatchId}");
#endif
                var side = information.Side == ESideType.Pmc ? "RaidSidePmc".Localized() : "RaidSideScav".Localized();
                var time = ConvertToTime(information.Time, IsStaticTimeLocation(information.Location));
                AttachTooltip(newPlayer, string.Format(LocaleUtils.UI_MMUI_RAID_DETAILS.Localized(), side,
                    ColorizeText(EColor.BLUE, information.Location.Localized()),
                    BoldText(time)));

                if (!information.Started)
                {
                    var joinButton = mainMenuUIPlayer.JoinButton is null ? null : mainMenuUIPlayer.JoinButton.Value;
                    if (!IsAlive(joinButton))
                    {
                        partial++;
                        ReportOnce(ref _rowReported, $"[MainMenuUI] the join button of a player row is not a usable native object ({NativeRefGuard.Describe(joinButton)}); joining from the panel is skipped");
                        newPlayer.SetActive(true);
                        _players.Add(newPlayer);
                        created++;
                        continue;
                    }

                    var buttonGo = joinButton.transform.parent.gameObject;
                    if (!IsAlive(buttonGo))
                    {
                        partial++;
                        ReportOnce(ref _rowReported, "[MainMenuUI] the join button has no usable parent object; joining from the panel is skipped");
                        newPlayer.SetActive(true);
                        _players.Add(newPlayer);
                        created++;
                        continue;
                    }

                    buttonGo.SetActive(true);
                    AttachTooltip(buttonGo, LocaleUtils.UI_JOIN_RAID.Localized());
                    System.Action joinListener = async () =>
                    {
                        if (JoinInProgress)
                        {
                            return;
                        }

                        JoinInProgress = true;

                        // A new attempt starts clean; the previous attempt's failure is reported (and
                        // logged) here instead of aborting this one at the raid start with a stale reason.
                        FikaBackendUtils.ClearLeftoverJoinFailure();

                        if (!TarkovApplication.Exist(out var tarkovApplication))
                        {
                            FikaGlobals.LogError("OnFikaStartRaid: Could not find TarkovApplication");
                            JoinInProgress = false;
                            return;
                        }

                        var session = tarkovApplication.Session;
                        if (session == null)
                        {
                            Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                "Session was null when starting the raid");
                            FikaGlobals.LogError("Session was null when starting the raid");
                            JoinInProgress = false;
                            return;
                        }

                        FikaBackendUtils.Profile = session.Profile;

                        var location = session.LocationSettings.locations.Values.AsManaged()
                            .FirstOrDefault(l => l.Id == information.Location);

                        if (location == null)
                        {
                            Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                $"Failed to find location {information.Location}");
                            FikaGlobals.LogError($"Failed to find location {information.Location}");
                            JoinInProgress = false;
                            return;
                        }
                        var mmc = tarkovApplication._menuOperation;

#if !DEBUG
                        var access = location.GetAccessRequirements(EGameMode.Pve, JsonType.LocationSettings.Location.EAccessUsageType.Menu);
                        if (access != null && access.Count > 0 && information.Side is ESideType.Pmc)
                        {
                            var playerLevel = session.Profile.Info.Level;
                            if (playerLevel >= location.MinPlayerLvlAccessKeys)
                            {
                                var playerItems = mmc.InventoryController.Inventory.GetPlayerItems(EPlayerItems.Equipment);
                                if (!LocationAccessRequirementsUtility.HasAnyValidAccessRequirement(playerItems, access,
                                    (Il2CppSystem.Func<EFT.InventoryLogic.Item, bool>)(item => mmc.InventoryController.Examined(item))))
                                {
                                    FikaNotifications.DisplayMessageNotification(string.Format(LocaleUtils.MISSING_KEY_FOR_LOCATION.Localized(),
                                        string.Join(", ", access.Keys.AsManaged().Select(key => $"{key} Name".Localized()))),
                                        iconType: EFT.Communications.ENotificationIconType.Alert);
                                    JoinInProgress = false;
                                    return;
                                }
                            }
                        }
#endif

                        FikaNotifications.DisplayMessageNotification(LocaleUtils.CONNECTING_TO_SESSION.Localized(),
                            iconType: EFT.Communications.ENotificationIconType.EntryPoint);

                        FikaBackendUtils.HostLocationId = information.Location;

                        using var pingingClient = await NetManagerUtils.CreatePingingClient();

                        if (pingingClient.Init(information.MatchId))
                        {
                            FikaGlobals.LogInfo("Attempting to connect to host session...");
                            var knockMessage = FikaBackendUtils.ServerGuid.ToString();
                            var success = await pingingClient.AttemptToPingHost(knockMessage);

                            if (!success)
                            {
                                Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                                LocaleUtils.UI_ERROR_CONNECTING.Localized(),
                                LocaleUtils.UI_UNABLE_TO_CONNECT.Localized() + PingingFailureDetail(pingingClient),
                                ErrorScreen.EButtonType.OkButton, 10f);
                                JoinInProgress = false;
                                return;
                            }
                        }
                        else
                        {
                            // A server that answered without a host record is the host's side (their raid
                            // was never published); a transport failure is ours.
                            Singleton<PreloaderUI>.Instance.ShowCriticalErrorScreen(
                                LocaleUtils.UI_ERROR_CONNECTING.Localized(),
                                (pingingClient.HostAddressMissing
                                    ? LocaleUtils.UI_ERROR_CONNECTING_TO_RAID.Localized()
                                    : LocaleUtils.UI_UNABLE_TO_CONNECT.Localized()) + PingingFailureDetail(pingingClient),
                                ErrorScreen.EButtonType.OkButton, 10f);
                            JoinInProgress = false;
                            return;
                        }

                        if (FikaBackendUtils.JoinMatch(FikaBackendUtils.Profile.ProfileId, information.MatchId, out var result, out var errorMessage))
                        {
                            FikaBackendUtils.GroupId = result.ServerId;
                            FikaBackendUtils.ClientType = EClientType.Client;

                            AddPlayerRequest data = new(FikaBackendUtils.GroupId, FikaBackendUtils.Profile.ProfileId, FikaBackendUtils.IsSpectator);
                            FikaRequestHandler.UpdateAddPlayer(data);

                            // -1 keeps the world clock; a default struct here would
                            // mean hour 0 (midnight) and force every joined raid to
                            // night. The host's own choice arrives from the server a
                            // moment later and overwrites this.
                            var hostTimeAndWeather = new TimeAndWeatherSettings
                            {
                                HourOfDay = -1,
                                TimeFlowType = ETimeFlowType.x1
                            };

                            RaidSettings raidSettings = new()
                            {
                                Side = information.Side,
                                PlayersSpawnPlace = default,
                                MetabolismDisabled = default,
                                BotSettings = default,
                                WavesSettings = default,
                                TimeAndWeatherSettings = hostTimeAndWeather,
                                SelectedDateTime = information.Time,
                                SelectedLocation = location,
                                isInTransition = false,
                                RaidMode = ERaidMode.Local,
                                IsPveOffline = true,
                                OnlinePveRaid = false
                            };

                            FikaBackendUtils.IsScav = raidSettings.IsScav;

                            var controller = mmc.Matchmaker.MatchmakerPlayersController;

                            if (raidSettings.Side is ESideType.Savage && ScavBlocked(session, out var minutesLeft))
                            {
                                Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                    string.Format(LocaleUtils.UI_SCAV_NOT_READY.Localized(), minutesLeft.ToString("F0")));
                                JoinInProgress = false;
                                return;
                            }

                            tarkovApplication._raidSettings = raidSettings;
                            controller.MatchingProgress.SetStartTime(DateTimeExtensions.Now);

                            try
                            {
                                await tarkovApplication.LocalGameMatching(raidSettings.TimeAndWeatherSettings).AsManaged();
                            }
                            catch (Exception ex)
                            {
                                Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING",
                                    $"Exception caught during raid init: {ex.Message}", (System.Action)Application.Quit);
                                JoinInProgress = false;
                            }
                        }
                        else
                        {
                            Singleton<PreloaderUI>.Instance.ShowErrorScreen("ERROR JOINING", errorMessage, null);
                        }

                        JoinInProgress = false;
                    };

                    // The engine holds this listener in the button's native UnityEvent. Keep the
                    // CLR delegate rooted and remember it, so it can be removed again before the
                    // row is destroyed.
                    _rowListeners.Add((joinButton, joinListener));
                    Root("row join listener", joinListener);
                    Fika.Core.UnityEventCompatExtensions.AddListener(joinButton.onClick, joinListener);
                }
            }
            newPlayer.SetActive(true);
            Root("row", newPlayer);
            _players.Add(newPlayer);
            created++;
        }

        FikaGlobals.LogInfo($"[MainMenuUI] refresh complete: {created} row(s) shown, {partial} partially wired, {skipped} skipped");
    }

    /// <summary>
    /// The prefab's own parent is reused so the rows keep the template's layout, but that
    /// reference comes out of the bundle: it is validated before Instantiate sees it.
    /// </summary>
    private Transform ResolveTemplateParent()
    {
        try
        {
            var templateTransform = _playerTemplate.transform;
            if (!IsAlive(templateTransform))
            {
                ReportOnce(ref _templateReported, $"[MainMenuUI] the player template transform is not a usable native object ({NativeRefGuard.Describe(templateTransform)}); the rows are created without a parent");
                return null;
            }

            var parent = templateTransform.parent;
            if (!IsAlive(parent))
            {
                ReportOnce(ref _templateReported, "[MainMenuUI] the player template parent is not a usable native object; the rows are created without a parent");
                return null;
            }

            return parent;
        }
        catch (Exception exception)
        {
            ReportOnce(ref _templateReported, $"[MainMenuUI] the player template parent could not be read: {exception}");
            return null;
        }
    }

    /// <summary>
    /// A tooltip component is added at runtime, so its own references are unwired by design.
    /// Only a component that turned out to be a real native object is configured and used.
    /// </summary>
    private void AttachTooltip(GameObject target, string message)
    {
        if (!IsAlive(target))
        {
            return;
        }

        if (!_tooltipTraceReported)
        {
            _tooltipTraceReported = true;
            FikaGlobals.LogInfo("[MainMenuUI] player row: attaching a HoverTooltipArea");
        }

        var tooltip = target.AddComponent<HoverTooltipArea>();
        if (!IsAlive(tooltip))
        {
            ReportOnce(ref _rowReported, $"[MainMenuUI] a HoverTooltipArea could not be added ({NativeRefGuard.Describe(tooltip)}); the row is shown without its tooltip");
            return;
        }

        tooltip.enabled = true;
        tooltip.SetMessageText(message);
    }

    private bool ScavBlocked(IEftSession session, out double remainingMinutes)
    {
        var utcNowUnixInt = DateTimeExtensions.UtcNowUnixInt;
        var diffSeconds = session.ProfileOfPet.Info.SavageLockTime - (double)utcNowUnixInt;

        if (diffSeconds > 0.0d)
        {
            remainingMinutes = diffSeconds / 60.0d;
            return true;
        }

        remainingMinutes = 0.0d;
        return false;
    }

    public void UpdatePresence(EFikaPlayerPresence presence)
    {
        // Prevent spamming going back and forth to the main menu causing server lag for no reason
        if ((DateTime.Now - _lastSet).TotalSeconds < _minSecondsToWait)
        {
            return;
        }

        _lastSet = DateTime.Now;
        FikaSetPresence data = new(presence);
        FikaRequestHandler.SetPresence(data);
    }
}


