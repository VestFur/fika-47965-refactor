using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Replaces the invoker Il2CppInterop generates for an injected method with a pure managed shim.
///
/// Why: the generated invoker body ends in an indirect call (<c>calli</c>, ClassInjector.decompiled.cs:1469)
/// into the method's own trampoline thunk. CoreCLR treats exactly that combination -- managed code
/// calling a reverse P/Invoke / UnmanagedCallersOnly entry -- as an uncatchable fail fast
/// (0x80131506, "Invalid Program: attempted to call a UnmanagedCallersOnly method from managed code",
/// coreclr+0x2C7085 behind JIT_ReversePInvokeEnterRare, coreclr+0x19BBD6), which killed the
/// 2026-10-10 host raids (analysis/crash-clr, analysis/injected-invoker-fix, analysis/managed-invoker).
/// The calli cannot be repaired from inside the body (the rewrite provably produced byte-identical
/// code), so this class replaces the *entry*: <c>Il2CppMethodInfo::InvokerMethod</c> is pointed at
/// <see cref="Shim"/>, a managed static method over the same five argument ABI -- the construction the
/// injector already trusts for constructors (StaticVoidIntPtrInvoker_MetadataV29,
/// ClassInjector.decompiled.cs:1541-1544).
///
/// Coverage: the shim reproduces the generated invoker's conversion for an arbitrary parameter count
/// (references, pointers, primitives, enums, by-value structs, by-ref/out of all of those) and for
/// void / reference / pointer / primitive / enum / struct returns. Everything it cannot convert keeps
/// the injector's own invoker (the status quo) and is named once in the diagnostics with the reason.
///
/// Scope: the shim covers *Fika's own injected types only* - the "every method info of every injected
/// class" invariant of round 10 was withdrawn, because it also rewrote other plugins' types (see the
/// scope region below). Within that scope the invariant holds: every method info of every Fika class the
/// shim can serve is served by it, whichever path found the method info. Registration alone cannot
/// deliver that - the injector also converts methods from its own native generic hook at runtime, and
/// the coroutine wrapper this port's own coroutine helper needs is registered after the port's
/// registration scope is gone. The sweep, the registration hook and the runtime guard that close those
/// paths are described in NativeManagedInvoker.Sweep.cs.
///
/// Three hard rules, in this order:
///   1. registration must never fail because of this class (every entry point is wrapped);
///   2. a callout must always return normally -- an exception may never cross back into il2cpp;
///   3. a shape that cannot be reproduced exactly is not rewritten (and says so).
///
/// The argument ABI is not inferred: it was read out of the interop wrappers of the installed build
/// (analysis/managed-invoker/arg-abi.txt), which are the callers of <c>il2cpp_runtime_invoke</c>:
///   * reference type (string / Il2CppObjectBase / interface):
///     <c>ldloc args; ldarg.n; call Il2CppObjectBaseToPtr|ManagedStringToIl2Cpp; stind.i</c>
///     -> the slot holds the object pointer itself (EFT.BaseLocalGame`1::SessionRun at IL_0013);
///   * pointer type (<c>T*</c>): <c>ldloc args; ldarg.n; stind.i</c>
///     -> the slot holds the pointer itself (AreaLight::FillShadowCubePlanes at IL_001F);
///   * by-ref (<c>ref</c>/<c>out</c>): <c>ldloc args; ldarg.n; conv.i; stind.i</c>
///     -> the slot holds the ADDRESS of the storage (EFT.MovementContext::Flash at IL_0014);
///   * by-value value type (primitive / enum / struct): <c>ldloc args; ldarga.s p; stind.i</c>
///     -> the slot holds the ADDRESS of the storage (AmplifyMotionEffectBase::CreateRenderTexture at IL_0026);
/// and the injector's own invoker dereferences a slot exactly when the native type is not <c>nint</c>
/// (ClassInjector.decompiled.cs:1451-1459, TrampolineHelpers.NativeType), which is the same split.
/// </summary>
internal static unsafe partial class NativeManagedInvoker
{
    /// <summary>
    /// The Harmony instance that owns this class's hooks. Deliberately *not* the instance of
    /// <see cref="NativeInjectionScope"/>: that one unpatchs its targets one by one when its scope is
    /// disposed (<c>NativeInjectionScope.Dispose</c>), which silently removed the
    /// <c>ConvertMethodInfo</c> postfix while the injector kept converting the methods registered
    /// afterwards - the gap this class closes (analysis/managed-invoker/REPORT-sweep.md).
    /// A separate owner id cannot be unpatched by another scope's dispose.
    /// </summary>
    internal const string OwnHarmonyId = "com.fika.core.managed-invoker";

    private static Harmony _hooks;

    /// <summary>
    /// Method infos that need no second look: the engine's own (never ours to touch), and the ones with
    /// no invoker bound (abstract methods, generic definitions). Only the runtime guard reads it, so an
    /// invoke never pays for a resolution twice.
    /// </summary>
    private static readonly ConcurrentDictionary<nint, byte> NotOurs = new();

    /// <summary>
    /// The entry shape il2cpp calls with for every injected method in metadata v29+
    /// (<c>InvokerDelegateMetadataV29</c>, ClassInjector.decompiled.cs:31).
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void InvokerShim(nint methodPointer, Il2CppMethodInfo* methodInfo, nint obj, nint* args,
        nint* returnValue);

    /// <summary>A static field is a GC root: the delegate behind the exported pointer stays alive.</summary>
    private static readonly InvokerShim ShimDelegate = Shim;

    /// <summary>Bytes reserved for struct round trips while a codec is validated.</summary>
    private const int ScratchBytes = 256;

    private static nint _shimPointer;
    private static nint _scratch;
    private static bool _unavailable;
    private static bool _installed;
    private static int _shimCount;
    private static int _stockCount;
    private static int _summaryReported;
    private static MethodInfo _reportTrampolineException;

    /// <summary>Il2CppMethodInfo* -> what the shim needs to run the managed method.</summary>
    private static readonly ConcurrentDictionary<nint, Plan> Plans = new();

    /// <summary>
    /// Safety net for a callout whose MethodInfo is not the struct this class filled: il2cpp may hand a
    /// *copy* of it around. The method pointer (one thunk per injected method) identifies the same plan.
    /// Only pointers that were rewritten are in here.
    /// </summary>
    private static readonly ConcurrentDictionary<nint, Plan> PlansByMethodPointer = new();

    /// <summary>Pointers already written to the warm table, so the log holds one line per method.</summary>
    private static readonly ConcurrentDictionary<nint, byte> Reported = new();

    /// <summary>Diagnostics that must not repeat per call (they run on the engine's thread).</summary>
    private static readonly ConcurrentDictionary<nint, byte> ReportedAtRuntime = new();

    /// <summary>Conversion codecs per managed type (a null <see cref="Codec.Read"/> marks "unsupported").</summary>
    private static readonly ConcurrentDictionary<Type, Codec> Codecs = new();

    private static readonly Codec Unsupported = new Codec { Kind = "unsupported" };

    private enum SlotKind
    {
        Reference,
        Pointer,
        Value,
        ByRef,
    }

    /// <summary>How one argument is read from its slot (and, for by-ref, written back).</summary>
    private sealed class ArgumentPlan
    {
        internal string Name;
        internal string Type;
        internal SlotKind Slot;
        internal Func<nint, object> Read;
        internal Action<nint, object> WriteBack;
    }

    private sealed class ReturnPlan
    {
        internal bool IsVoid;
        internal Action<object, nint> Write;
    }

    private sealed class Plan
    {
        internal MethodInfo Target;
        internal string Name;
        internal bool Static;
        internal ArgumentPlan[] Arguments;
        internal ReturnPlan Return;

        internal object[] NewArguments() =>
            Arguments.Length == 0 ? Array.Empty<object>() : new object[Arguments.Length];
    }

    /// <summary>Reads a managed value from an address and writes it back there.</summary>
    private sealed class Codec
    {
        internal string Kind;
        internal Func<nint, object> Read;
        internal Action<nint, object> Write;
        internal object Default;
    }

    // ------------------------------------------------------------------ scope of the shim
    //
    // The shim replaces the entry the engine reaches reflectively (Il2CppMethodInfo::InvokerMethod) of
    // every injected method the injector serves. Before this round that was *every* injected method of
    // *every* plugin: the registration hook fires for whatever the injector converts, and the sweep
    // enumerates every injected class it can name. A tester's log of round 10 showed what that costs -
    // the sweep covered ArmorClassIcon.RefreshBehaviour, and the engine reached
    // AmandsGraphics.AmandsGraphicsClass.Awake/.Start/.Update through Fika's managed shim, i.e. a third
    // party mod's engine callbacks were routed through this port's reflection path. That is a
    // compatibility surface this port introduced and never needed: another plugin's injected method is
    // entered by *its* code path, and the invoker the injector generated for it is the entry its author
    // wrote and tested against (analysis/managed-invoker/REPORT-sweep.md is the round-10 document that
    // widened the net; this region narrows it again).
    //
    // The rule is therefore a scope, checked at every door into the shim (TryAdopt is the authoritative
    // one; the registration hook, the sweep and the runtime guard all pass through it or check it
    // first). Three tiers, in this order:
    //
    //   1. own ............ the type is one of Fika's: a "Fika.*" assembly, the "Fika.Core" namespace, or
    //                       one of the global-namespace Fika UI types in OwnGlobalNamespaceTypes (every
    //                       entry names why it is there);
    //   2. port-reached ... a non-Fika type that Fika's *own* runtime path reaches reflectively and for
    //                       which the injector's invoker was measured to be fatal (PortReachedTypes, one
    //                       measured case per entry). Switch: ScopePortReachedKey, default on;
    //   3. out of scope ... everything else. Its methods and its vtable slots are left exactly as the
    //                       injector built them, and the type is named once in the diagnostics.
    //
    // Two switches widen the scope for a tester who needs the old behaviour, both needing a restart
    // (what the shim rewrote is decided once per process and cannot be un-rewritten):
    // ScopeEverythingKey restores the round-10 net wholesale, ScopeExtraNamesKey adds single types by
    // name so a specific mod can be covered without covering all of them.
    //
    // Fika's own types are unaffected by all of this: 135 of the 141 types a local run registers are
    // declared in Fika.Core.dll (analysis/shim-scope/injected-types-by-assembly.txt), and the six that
    // are not are the third-party types listed in PortReachedTypes or reported as kept.

    /// <summary>Section of the scope switches; they live next to the diagnostics switches.</summary>
    internal const string ScopeConfigSection = "Diagnostics";

    /// <summary>
    /// Wholesale switch. True restores the round-10 behaviour (every injected type the injector serves is
    /// rewritten, whichever plugin registered it). False - the default - keeps third-party types on the
    /// invoker the injector generated for them.
    /// </summary>
    internal const string ScopeEverythingKey = "Compat: Shim Third-Party Injected Types";

    /// <summary>Switch of the <see cref="PortReachedTypes"/> compatibility list (default on).</summary>
    internal const string ScopePortReachedKey = "Compat: Shim Port-Reached Third-Party Types";

    /// <summary>Operator escape hatch: additional type full names to rewrite, comma separated (default empty).</summary>
    internal const string ScopeExtraNamesKey = "Compat: Additional Shimmed Type Names";

    /// <summary>The assembly name prefix of Fika's own assemblies (Fika.Core, Fika.Headless, ...).</summary>
    private const string OwnAssemblyPrefix = "Fika.";

    /// <summary>The namespace of every Fika type that is not declared in the global namespace.</summary>
    private const string OwnNamespace = "Fika.Core";

    /// <summary>
    /// Fika's own injected types that live in the *global* namespace, where neither the assembly rule nor
    /// the namespace rule can name them by shape alone. Every entry carries why it is listed. Declared in
    /// Fika.Core.dll - verified against the installed build with
    /// analysis/shim-scope/scan-injected-types.ps1 (see analysis/shim-scope/injected-types-by-assembly.txt).
    /// A type that is neither in this table nor a Fika.Core type keeps the injector's invoker.
    /// </summary>
    private static readonly Dictionary<string, string> OwnGlobalNamespaceTypes = new(StringComparer.Ordinal)
    {
        { "LoadingScreenUI", "Fika's loading screen: the client/server callbacks drive it (LoadingScreenUI.Instance.AddPlayer/SetProgress) and the engine enters its Awake/OnDestroy" },
        { "LoadingScreenPlayer", "the per-player row that screen instantiates; the engine drives its lifecycle" },
        { "MainMenuUI", "the Fika panel Fika injects into EFT's main menu; engine-driven Awake/OnDestroy" },
        { "MainMenuUIPlayer", "the animated player plate of the main-menu decoration" },
        { "MatchMakerUI", "the Fika matchmaker panel; engine-driven Awake/Update/OnDestroy" },
        { "PlayerPlateUI", "the plate shown next to a player while the raid loads" },
        { "SendItemUI", "the Fika 'send item to player' dialog" },
        { "ListPlayer", "one row of the Fika player list" },
        { "IntUpDown", "the numeric stepper the Fika admin/limit windows use" },
        { "ButtonHandler", "the Fika menu button; it drives its own canvas by coroutine" },
        { "DebugUI", "Fika's debug overlay" },
        { "AdminSettingsUI", "the headless/admin settings window (no injectable member today; listed so a future one is covered)" },
        { "AdminSettingsUIScript", "the script of that window" },
        { "FikaChatUI", "the Fika chat window (no injectable member today)" },
        { "FreecamUI", "the free-camera overlay (no injectable member today)" },
        { "RaidAdminUI", "the raid-admin window (no injectable member today)" },
    };

    /// <summary>
    /// The non-Fika types Fika's *own* runtime path reaches reflectively, for which the injector's invoker
    /// was measured to be fatal. One entry is one measured case, never a blanket "this mod looks useful":
    /// an entry may only be added with an observation that names the Fika frame that reaches it.
    /// The switch <see cref="ScopePortReachedKey"/> turns the whole table off.
    /// </summary>
    private static readonly Dictionary<string, string> PortReachedTypes = new(StringComparer.Ordinal)
    {
        {
            "BepInEx.Unity.IL2CPP.Utils.Collections.Il2CppManagedEnumerator",
            "the carrier of Fika's own coroutines: EngineCoroutines.Wrap(...).WrapToIl2Cpp() hands them to the engine, "
            + "BepInEx registers this type lazily inside that first Wrap - i.e. after the port's registration scope is gone - "
            + "and the engine enters get_Current/MoveNext/Reset through the invoker. Round 10 measured the fatal shape exactly "
            + "there (Invoker_IntPtrThis, IL2CPP-to-Managed trampoline; LogOutput.log:1791-1806, "
            + "4745/7791/18405, and the round-11 session entered get_Current 954 times through this shim)"
        },
    };

    /// <summary>Resolved scope policy, bound once while the injector installs (see <see cref="ConfigureScope"/>).</summary>
    private static bool _scopeEverything;
    private static bool _scopePortReached = true;
    private static string[] _scopeExtraNames = Array.Empty<string>();

    /// <summary>Types whose "kept out of scope" line was already written (one line per type, never per method).</summary>
    private static readonly ConcurrentDictionary<string, byte> ReportedOutOfScope = new();

    /// <summary>
    /// Binds the three scope switches into the plugin's config file and records the resolved policy.
    /// Called while the injector installs, i.e. before the first type is registered: what the shim rewrites
    /// is decided once per process, so a flipped switch needs a restart (an already adopted invoker cannot
    /// be un-adopted). Never throws - a failure leaves the built-in narrow rule in force.
    /// </summary>
    private static void ConfigureScope()
    {
        try
        {
            var config = FikaPlugin.Instance?.Config;
            if (config == null)
            {
                
                return;
            }

            _scopeEverything = config.Bind(ScopeConfigSection, ScopeEverythingKey, false, new ConfigDescription(
                "Also rewrite the invoker of third-party injected types (the round-10 behaviour). Off: another plugin's "
                + "engine callbacks stay on the invoker the injector generated for them. Needs a restart.")).Value;
            _scopePortReached = config.Bind(ScopeConfigSection, ScopePortReachedKey, true, new ConfigDescription(
                "Keep the shim on the non-Fika types Fika's own runtime path reaches; every entry in PortReachedTypes "
                + "(NativeManagedInvoker.cs) is a measured fatal path. Needs a restart.")).Value;
            var extra = config.Bind(ScopeConfigSection, ScopeExtraNamesKey, string.Empty, new ConfigDescription(
                "Comma separated full names of extra injected types to rewrite (for example AmandsGraphics.AmandsGraphicsClass). "
                + "Use it when one specific mod needs the shim without the wholesale switch. Needs a restart.")).Value;
            _scopeExtraNames = ParseExtraNames(extra);

            
        }
        catch (Exception exception)
        {
            
        }
    }

    private static string[] ParseExtraNames(string configured)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configured)) return Array.Empty<string>();
            var parts = configured.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var names = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                var name = part.Trim();
                if (name.Length != 0 && !names.Contains(name)) names.Add(name);
            }

            return names.ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The resolved policy as one line, for the diagnostics header.</summary>
    private static string DescribeScope() =>
        (_scopeEverything
            ? "every injected type is rewritten (the third-party switch is ON: the round-10 behaviour)"
            : "only Fika's own injected types are rewritten (" + OwnAssemblyPrefix + "* assemblies, the " + OwnNamespace
              + " namespace and " + OwnGlobalNamespaceTypes.Count + " whitelisted global-namespace types); every other injection keeps the invoker the injector generated for it")
        + "; port-reached compatibility list " + (_scopePortReached
            ? "on (" + PortReachedTypes.Count + "): " + string.Join(", ", PortReachedTypes.Keys)
            : "off")
        + (_scopeExtraNames.Length == 0
            ? "; no extra type names configured"
            : "; extra type names: " + string.Join(", ", _scopeExtraNames));

    /// <summary>
    /// True when this injected type is inside the shim's scope, i.e. when the shim may replace its invoker.
    /// Everything else keeps the invoker the injector generated, which is what those types had before this
    /// class existed. Never throws: a type that cannot be inspected is out of scope.
    /// </summary>
    internal static bool IsShimScope(Type type)
    {
        if (type == null) return false;
        if (_scopeEverything) return true;
        try
        {
            var name = type.FullName;
            if (string.IsNullOrEmpty(name)) return false;
            if (OwnGlobalNamespaceTypes.ContainsKey(name)) return true;
            if (string.Equals(name, OwnNamespace, StringComparison.Ordinal)
                || name.StartsWith(OwnNamespace + ".", StringComparison.Ordinal)) return true;
            var assembly = type.Assembly?.GetName().Name;
            if (assembly != null && assembly.StartsWith(OwnAssemblyPrefix, StringComparison.Ordinal)) return true;
            if (_scopePortReached && PortReachedTypes.ContainsKey(name)) return true;
            for (var i = 0; i < _scopeExtraNames.Length; i++)
                if (string.Equals(_scopeExtraNames[i], name, StringComparison.Ordinal)) return true;
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// One line per type that stays on the injector's invoker because of the scope - deliberately per type
    /// and never per method: the point of the narrowing is that a tester can read the list of what was left
    /// alone. Deduplicated for the lifetime of the process.
    /// </summary>
    private static bool ReportOutOfScope(Type owner, string origin)
    {
        try
        {
            var name = owner == null ? "<unknown type>" : (owner.FullName ?? owner.Name);
            if (!ReportedOutOfScope.TryAdd(name, 0)) return false;
            var assembly = "<unknown assembly>";
            try
            {
                assembly = owner?.Assembly?.GetName().Name ?? assembly;
            }
            catch (Exception)
            {
            }

            
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ installation

    /// <summary>
    /// Adds the postfix that rewrites the invoker of every injected method. Called from
    /// <see cref="NativeInjectionScope"/> while it installs its own hooks, i.e. before the first type is
    /// registered. Never throws: a failure here means "no rewrite at all", never "a broken load".
    /// </summary>
    internal static void Install()
    {
        if (_installed || _unavailable) return;
        try
        {
            _hooks = new Harmony(OwnHarmonyId);
            var converter = AccessTools.DeclaredMethod(typeof(ClassInjector), "ConvertMethodInfo");
            if (converter == null)
            {
                _unavailable = true;
                
                return;
            }

            _scratch = Marshal.AllocHGlobal(ScratchBytes);
            // The scope switches decide what this class rewrites at all, so they are bound before any
            // hook can adopt a method info (never throws; see ConfigureScope).
            ConfigureScope();
            if (!SelfTest())
            {
                // A body the JIT refuses to validate must not be published as an entry point: whatever
                // raised the failure would be raised again inside a native callout, where no managed
                // frame can handle it. "No rewrite at all" is the safe answer (hard rule 2).
                _unavailable = true;
                return;
            }
            WarmInstanceFastPath();
            _hooks.Patch(converter, postfix: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeManagedInvoker), nameof(CaptureMethodInfo))));
            InstallRegistrationHook();
            InstallInvokeGuard();
            _installed = true;
            
            Sweep(null, "install");
        }
        catch (Exception exception)
        {
            _unavailable = true;
            
        }
    }

    /// <summary>
    /// Postfix on <c>ClassInjector.RegisterTypeInIl2CppInternal</c>: when a type registration returns,
    /// its method infos exist and can be replaced. This is the net for every *in-scope* type registered
    /// while this port's own registration scope is long gone - above all the coroutine carrier
    /// <c>Il2CppManagedEnumerator</c> (PortReachedTypes), which BepInEx registers lazily inside the first
    /// <c>WrapToIl2Cpp()</c> of this port's coroutine helper. A type outside the scope is named once in
    /// the diagnostics and keeps the invoker the injector generated (see the scope region).
    /// </summary>
    private static void InstallRegistrationHook()
    {
        try
        {
            var registration = AccessTools.DeclaredMethod(typeof(ClassInjector), "RegisterTypeInIl2CppInternal");
            if (registration == null)
            {
                
                return;
            }

            _hooks.Patch(registration, postfix: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeManagedInvoker), nameof(CaptureRegisteredType))));
        }
        catch (Exception exception)
        {
            
        }
    }

    private static void CaptureRegisteredType(Type type)
    {
        try
        {
            RememberType(type);
            SweepType(type, "registered after the port's own scope");
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// Prefix on the managed entry points of the engine's reflection invoke: the last moment before the
    /// native side dereferences <c>Il2CppMethodInfo::InvokerMethod</c>. A method info that still carries
    /// a stock invoker (converted while no hook was installed, or created by the injector for a generic
    /// instantiation at runtime) is repaired here instead of being entered. The prefix only observes and
    /// repairs and always lets the original invoke run, so it cannot change a call's meaning.
    /// </summary>
    private static void InstallInvokeGuard()
    {
        GuardInvoke("il2cpp_runtime_invoke", nameof(GuardRuntimeInvoke));
        GuardInvoke("il2cpp_runtime_invoke_convert_args", nameof(GuardRuntimeInvokeConvertArgs));
    }

    private static void GuardInvoke(string name, string patch)
    {
        try
        {
            var method = AccessTools.DeclaredMethod(typeof(IL2CPP), name);
            if (method == null)
            {
                
                return;
            }

            _hooks.Patch(method, prefix: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeManagedInvoker), patch)));
        }
        catch (Exception exception)
        {
            
        }
    }

    private static bool GuardRuntimeInvoke(nint __0) => GuardInvokeTarget(__0, "runtime invoke");

    private static bool GuardRuntimeInvokeConvertArgs(nint __0) => GuardInvokeTarget(__0, "runtime invoke (argument conversion)");

    private static bool GuardInvokeTarget(nint method, string origin)
    {
        try
        {
            if (method == 0) return true;
            if (Plans.ContainsKey(method)) return true;
            if (NotOurs.ContainsKey(method)) return true;
            // The first method info the guard does not know is the moment to look at everything the
            // injector has registered with one sweep - a type can only appear once, and the sweep is
            // the only place that sees the types whose registration nobody hooked.
            SweepOnce(origin);
            if (Plans.ContainsKey(method)) return true;
            EnsureAdopted((Il2CppMethodInfo*)method, origin, null);
        }
        catch (Exception exception)
        {
            
        }

        // Always run the original invoke.
        return true;
    }

    /// <summary>
    /// Touches the native class the instance fast path compares against, once, while this port is still in
    /// managed context. The lookup is a static field of the interop's class-pointer store, so without this
    /// the first callout would run its initializer on the engine's thread - inside a native callout, where
    /// nothing may escape. The value it resolved is recorded: a zero there is the one case in which the
    /// fast path keeps the interop's own lookup for every zero handle instead of skipping it.
    ///
    /// Never throws (Installing must not fail because of a diagnostic).
    /// </summary>
    private static void WarmInstanceFastPath()
    {
        try
        {
            var pointer = Il2CppClassPointerStore<Il2CppSystem.MulticastDelegate>.NativeClassPtr;
            
        }
        catch (Exception exception)
        {
            
        }
    }

    /// <summary>
    /// Enters the shim once from managed context with an empty call, so the body (and the failure path)
    /// is JIT validated at load instead of inside the first native callout.
    /// </summary>
    private static bool SelfTest()
    {
        try
        {
            Shim(0, null, 0, null, null);
            
            return true;
        }
        catch (Exception exception)
        {
            
            return false;
        }
    }

    /// <summary>
    /// Harmony postfix on <c>ClassInjector.ConvertMethodInfo</c>: runs for every method of an injected
    /// type, right after the injector filled its method info - including the inflated generic methods
    /// the injector converts from its own native hook at runtime. The work itself lives in
    /// <see cref="TryAdopt"/>, which the sweep and the runtime guard call with the same rules, so all
    /// three paths publish the same kind of entry.
    /// </summary>
    private static void CaptureMethodInfo(MethodInfo monoMethod, Il2CppMethodInfo* __result)
    {
        if (__result == null) return;
        try
        {
            TryAdopt(__result, monoMethod, "registration", null, out _);
        }
        catch (Exception exception)
        {
            
        }
    }

    // ------------------------------------------------------------------ the one adoption path

    /// <summary>
    /// Points the invoker of one method info at the managed shim, or says why it cannot.
    ///
    /// The three callers - the registration postfix, the sweep and the runtime guard - differ only in how
    /// they found the method info; the rule they apply is the one registration always applied:
    ///   * the shape has to be reproducible (<see cref="BuildPlan"/>), otherwise the injector's invoker
    ///     stays and the reason is written once;
    ///   * the method pointer is registered as a second key, because the engine can hand the shim a
    ///     *copy* of a method info;
    ///   * one <c>[WarmTable]</c> line per method info, in the format the earlier rounds parse: the
    ///     pointer triple, the method name, and either <c>[shim]</c> with the replaced stock pointer or
    ///     <c>[stock: reason]</c>.
    ///
    /// Behaviour after the rewrite is the call the injector's own invoker would have made: the plan reads
    /// a reference slot as the object pointer itself, a value/by-ref slot as an address, and writes the
    /// return back at its own width (NativeManagedInvoker, class comment).
    /// </summary>
    internal static bool TryAdopt(Il2CppMethodInfo* methodInfo, MethodInfo monoMethod, string origin, string detail, out string reason)
    {
        reason = null;
        if (methodInfo == null) return false;
        var key = (nint)methodInfo;
        var name = Describe(monoMethod);
        try
        {
            // The authoritative scope gate (class comment, "scope of the shim"): registration, sweep and
            // runtime guard all land here, so a third-party method info is never adopted even if some
            // other caller finds it. A null declaring type is *not* a scope verdict - the plan builder
            // below still names the real reason for it.
            var owner = monoMethod?.DeclaringType;
            if (owner != null && !IsShimScope(owner))
            {
                reason = "outside the shim scope (only Fika's own injected types are rewritten)";
                ReportOutOfScope(owner, origin);
                return false;
            }

            Plan plan = null;
            string why = null;
            if (!UnityVersionHandler.IsMetadataV29OrHigher)
                why = "this install does not use the metadata v29 invoker ABI";
            else
                plan = BuildPlan(monoMethod, out why);

            var info = UnityVersionHandler.Wrap(methodInfo);
            var methodPointer = info.MethodPointer;
            var stockInvoker = info.InvokerMethod;
            var status = "stock";
            if (plan != null)
            {
                var shim = ShimPointer();
                if (shim == 0)
                {
                    plan = null;
                    why = "the managed shim pointer could not be marshalled";
                }
                else
                {
                    try
                    {
                        info.InvokerMethod = shim;
                        Plans[key] = plan;
                        if (methodPointer != 0) PlansByMethodPointer[methodPointer] = plan;
                        status = "shim";
                    }
                    catch (Exception exception)
                    {
                        
                        plan = null;
                        why = "the managed shim pointer could not be marshalled";
                    }
                }
            }
            else if (why == null)
            {
                why = "the injector bound no invoker to this method";
            }

            reason = why;
            var first = Report(key, "methodInfo=0x" + key.ToString("X") + " methodPointer=0x" + methodPointer.ToInt64().ToString("X")
                + " invokerMethod=0x" + info.InvokerMethod.ToInt64().ToString("X") + " " + name
                + (status == "shim"
                    ? " [shim] stock-invoker=0x" + stockInvoker.ToInt64().ToString("X")
                    : " [stock: " + why + "]")
                + (string.Equals(origin, "registration", StringComparison.Ordinal) ? string.Empty : " origin=" + origin)
                + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail));
            if (first)
            {
                if (status == "shim") System.Threading.Interlocked.Increment(ref _shimCount);
                else System.Threading.Interlocked.Increment(ref _stockCount);
            }

            
            return status == "shim";
        }
        catch (Exception exception)
        {
            reason = "the method info could not be inspected (" + exception.GetType().Name + ")";
            
            return false;
        }
    }

    // ------------------------------------------------------------------ plan building

    /// <summary>
    /// Decides whether the shim can reproduce this method exactly. Returns null (never throws) with a
    /// reason for every shape that stays with the injector's own invoker.
    /// </summary>
    private static Plan BuildPlan(MethodInfo method, out string reason)
    {
        reason = null;
        try
        {
            if (method == null)
            {
                reason = "the injector reported no method";
                return null;
            }

            if (method.IsAbstract)
            {
                reason = "abstract";
                return null;
            }

            if (method.ContainsGenericParameters || method.IsGenericMethod)
            {
                reason = "generic (the injector binds no invoker, or binds it per instantiation)";
                return null;
            }

            if (method.IsConstructor)
            {
                reason = "constructor";
                return null;
            }

            if (method.CallingConvention == CallingConventions.VarArgs)
            {
                reason = "varargs";
                return null;
            }

            if (!NativeInjectionScope.IsNativeSignatureType(method.ReturnType))
            {
                reason = "the injector binds no native ABI to the return type " + method.ReturnType.Name;
                return null;
            }

            var returnPlan = BuildReturn(method.ReturnType);
            if (returnPlan == null)
            {
                reason = "the return type " + method.ReturnType.Name + " cannot be written back";
                return null;
            }

            var parameters = method.GetParameters();
            var arguments = new ArgumentPlan[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var argument = BuildArgument(parameters[i]);
                if (argument == null)
                {
                    reason = "parameter " + i + " (" + parameters[i].Name + ": "
                        + parameters[i].ParameterType.Name + ") cannot be reproduced";
                    return null;
                }

                arguments[i] = argument;
            }

            return new Plan
            {
                Target = method,
                Name = Describe(method),
                Static = method.IsStatic,
                Arguments = arguments,
                Return = returnPlan,
            };
        }
        catch (Exception exception)
        {
            reason = "the shape could not be inspected (" + exception.GetType().Name + ")";
            return null;
        }
    }

    private static ArgumentPlan BuildArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type.IsByRef)
        {
            var element = type.GetElementType();
            var codec = ResolveCodec(element);
            if (codec == null || codec.Write == null) return null;
            var isOut = parameter.IsOut;
            return new ArgumentPlan
            {
                Name = parameter.Name,
                Type = type.Name,
                Slot = SlotKind.ByRef,
                Read = slot => isOut ? codec.Default : codec.Read(slot),
                WriteBack = codec.Write,
            };
        }

        if (type.IsPointer)
        {
            var element = type.GetElementType();
            return new ArgumentPlan
            {
                Name = parameter.Name,
                Type = type.Name,
                Slot = SlotKind.Pointer,
                // A pointer slot holds the pointer itself (arg-abi.txt, pointer category).
                Read = slot => Pointer.Box((void*)slot, element),
                WriteBack = null,
            };
        }

        if (IsNativeReference(type))
        {
            var wrap = ReferenceWrapper(type);
            if (wrap == null) return null;
            return new ArgumentPlan
            {
                Name = parameter.Name,
                Type = type.Name,
                Slot = SlotKind.Reference,
                // A reference slot holds the object pointer itself: do NOT dereference it.
                Read = slot => slot == 0 ? null : wrap(slot),
                WriteBack = null,
            };
        }

        var value = ResolveCodec(type);
        if (value == null) return null;
        return new ArgumentPlan
        {
            Name = parameter.Name,
            Type = type.Name,
            Slot = SlotKind.Value,
            // A value-type slot holds the address of the storage.
            Read = value.Read,
            WriteBack = null,
        };
    }

    private static ReturnPlan BuildReturn(Type type)
    {
        if (type == typeof(void)) return new ReturnPlan { IsVoid = true };
        if (type.IsPointer)
        {
            return new ReturnPlan
            {
                Write = (value, buffer) =>
                {
                    *(nint*)buffer = value == null ? 0 : (nint)Pointer.Unbox(value);
                },
            };
        }

        var codec = ResolveCodec(type);
        if (codec == null || codec.Write == null) return null;
        return new ReturnPlan { Write = (value, buffer) => codec.Write(buffer, value) };
    }

    private static bool IsNativeReference(Type type) =>
        type == typeof(string) || typeof(Il2CppObjectBase).IsAssignableFrom(type) || type.IsInterface;

    // ------------------------------------------------------------------ codecs

    /// <summary>
    /// The conversion of one managed type at an *address*: <c>Read(address)</c> gives the boxed managed
    /// value, <c>Write(address, value)</c> stores one back. Null when the type cannot be converted.
    /// </summary>
    private static Codec ResolveCodec(Type type)
    {
        var codec = Codecs.GetOrAdd(type, BuildCodec);
        return codec.Read == null ? null : codec;
    }

    private static Codec BuildCodec(Type type)
    {
        try
        {
            if (IsNativeReference(type))
            {
                var wrap = ReferenceWrapper(type);
                if (wrap == null) return Unsupported;
                return new Codec
                {
                    Kind = type.Name,
                    Read = address =>
                    {
                        var pointer = *(nint*)address;
                        return pointer == 0 ? null : wrap(pointer);
                    },
                    Write = (address, value) => *(nint*)address = NativePointer(value),
                    Default = null,
                };
            }

            if (type.IsPointer)
            {
                var element = type.GetElementType();
                return new Codec
                {
                    Kind = type.Name,
                    Read = address => Pointer.Box((void*)*(nint*)address, element),
                    Write = (address, value) => *(nint*)address = value == null ? 0 : (nint)Pointer.Unbox(value),
                    Default = Pointer.Box(null, element),
                };
            }

            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                var underlyingCodec = ResolveCodec(underlying);
                if (underlyingCodec == null) return Unsupported;
                return new Codec
                {
                    Kind = type.Name,
                    Read = address => Enum.ToObject(type, underlyingCodec.Read(address)),
                    Write = (address, value) => underlyingCodec.Write(address,
                        Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture)),
                    Default = Enum.ToObject(type, underlyingCodec.Default),
                };
            }

            if (type == typeof(bool))
                return new Codec { Kind = "bool", Read = a => *(byte*)a != 0, Write = (a, v) => *(byte*)a = (bool)v ? (byte)1 : (byte)0, Default = false };
            if (type == typeof(byte))
                return new Codec { Kind = "byte", Read = a => *(byte*)a, Write = (a, v) => *(byte*)a = (byte)v, Default = default(byte) };
            if (type == typeof(sbyte))
                return new Codec { Kind = "sbyte", Read = a => *(sbyte*)a, Write = (a, v) => *(sbyte*)a = (sbyte)v, Default = default(sbyte) };
            if (type == typeof(short))
                return new Codec { Kind = "short", Read = a => *(short*)a, Write = (a, v) => *(short*)a = (short)v, Default = default(short) };
            if (type == typeof(ushort))
                return new Codec { Kind = "ushort", Read = a => *(ushort*)a, Write = (a, v) => *(ushort*)a = (ushort)v, Default = default(ushort) };
            if (type == typeof(char))
                return new Codec { Kind = "char", Read = a => (char)*(ushort*)a, Write = (a, v) => *(ushort*)a = (char)v, Default = default(char) };
            if (type == typeof(int))
                return new Codec { Kind = "int", Read = a => *(int*)a, Write = (a, v) => *(int*)a = (int)v, Default = 0 };
            if (type == typeof(uint))
                return new Codec { Kind = "uint", Read = a => *(uint*)a, Write = (a, v) => *(uint*)a = (uint)v, Default = 0u };
            if (type == typeof(long))
                return new Codec { Kind = "long", Read = a => *(long*)a, Write = (a, v) => *(long*)a = (long)v, Default = 0L };
            if (type == typeof(ulong))
                return new Codec { Kind = "ulong", Read = a => *(ulong*)a, Write = (a, v) => *(ulong*)a = (ulong)v, Default = 0UL };
            if (type == typeof(float))
                return new Codec { Kind = "float", Read = a => *(float*)a, Write = (a, v) => *(float*)a = (float)v, Default = 0f };
            if (type == typeof(double))
                return new Codec { Kind = "double", Read = a => *(double*)a, Write = (a, v) => *(double*)a = (double)v, Default = 0d };
            if (type == typeof(IntPtr))
                return new Codec { Kind = "nint", Read = a => *(nint*)a, Write = (a, v) => *(nint*)a = (nint)v, Default = IntPtr.Zero };
            if (type == typeof(UIntPtr))
                return new Codec { Kind = "nuint", Read = a => *(nuint*)a, Write = (a, v) => *(nuint*)a = (nuint)v, Default = UIntPtr.Zero };

            if (type.IsValueType && !type.ContainsGenericParameters) return BuildStructCodec(type);
        }
        catch (Exception exception)
        {
            
        }

        return Unsupported;
    }

    /// <summary>
    /// Structs are moved as raw bytes through generated methods: <c>ldobj T; box T</c> to read,
    /// <c>unbox T; cpblk min(managed size, il2cpp value size)</c> to write. Both are executed once here,
    /// in managed context, so a JIT problem shows up at registration instead of inside a callout.
    /// </summary>
    private static Codec BuildStructCodec(Type type)
    {
        var readMethod = new DynamicMethod("FikaReadStruct_" + type.Name, typeof(object), new[] { typeof(nint) },
            typeof(NativeManagedInvoker), true);
        var readBody = readMethod.GetILGenerator();
        readBody.Emit(OpCodes.Ldarg_0);
        readBody.Emit(OpCodes.Ldobj, type);
        readBody.Emit(OpCodes.Box, type);
        readBody.Emit(OpCodes.Ret);
        var read = (Func<nint, object>)readMethod.CreateDelegate(typeof(Func<nint, object>));

        var managedSize = ManagedSize(type);
        var nativeSize = NativeValueSize(type, managedSize);
        var copy = Math.Min(managedSize, nativeSize);
        var writeMethod = new DynamicMethod("FikaWriteStruct_" + type.Name, typeof(void),
            new[] { typeof(nint), typeof(object) }, typeof(NativeManagedInvoker), true);
        var writeBody = writeMethod.GetILGenerator();
        writeBody.Emit(OpCodes.Ldarg_0);
        writeBody.Emit(OpCodes.Ldarg_1);
        writeBody.Emit(OpCodes.Unbox, type);
        writeBody.Emit(OpCodes.Conv_U);
        writeBody.Emit(OpCodes.Ldc_I4, copy);
        writeBody.Emit(OpCodes.Cpblk);
        writeBody.Emit(OpCodes.Ret);
        var write = (Action<nint, object>)writeMethod.CreateDelegate(typeof(Action<nint, object>));

        // Execute both once against scratch memory: JIT validation plus a default instance for 'out'.
        var defaultValue = read(_scratch);
        write(_scratch, defaultValue);
        return new Codec { Kind = type.Name, Read = read, Write = write, Default = defaultValue };
    }

    private static int ManagedSize(Type type)
    {
        var method = new DynamicMethod("FikaSizeOf_" + type.Name, typeof(int), Type.EmptyTypes,
            typeof(NativeManagedInvoker), true);
        var body = method.GetILGenerator();
        body.Emit(OpCodes.Sizeof, type);
        body.Emit(OpCodes.Conv_I4);
        body.Emit(OpCodes.Ret);
        return ((Func<int>)method.CreateDelegate(typeof(Func<int>)))();
    }

    private static int NativeValueSize(Type type, int fallback)
    {
        try
        {
            var align = 0u;
            var size = (int)IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore.GetNativeClassPointer(type), ref align);
            if (size > 0 && size < ScratchBytes) return size;
        }
        catch (Exception)
        {
        }

        return fallback;
    }

    /// <summary>The native pointer of a boxed managed value (object, string, or null).</summary>
    private static nint NativePointer(object value)
    {
        if (value == null) return 0;
        if (value is string text) return IL2CPP.ManagedStringToIl2Cpp(text);
        if (value is Il2CppObjectBase wrapped) return IL2CPP.Il2CppObjectBaseToPtr(wrapped);
        return 0;
    }

    /// <summary>
    /// <c>pointer -> Il2CppObjectPool.Get&lt;T&gt;(pointer)</c> bound once per type, so the hot path is a
    /// delegate call. Null when the wrapper cannot be bound (the shape then keeps the stock invoker).
    /// </summary>
    private static Func<nint, object> ReferenceWrapper(Type type)
    {
        if (type == typeof(string)) return pointer => IL2CPP.Il2CppStringToManaged(pointer);
        if (!typeof(Il2CppObjectBase).IsAssignableFrom(type) && !type.IsInterface) return null;
        try
        {
            MethodInfo get = null;
            foreach (var candidate in typeof(Il2CppObjectPool).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (candidate.Name != "Get" || !candidate.IsGenericMethodDefinition) continue;
                if (candidate.GetGenericArguments().Length != 1) continue;
                var parameters = candidate.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(nint)) continue;
                get = candidate;
                break;
            }

            if (get == null) return null;
            var converter = get.MakeGenericMethod(type).CreateDelegate(typeof(Func<nint, object>));
            return (Func<nint, object>)converter;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static nint ShimPointer()
    {
        var pointer = _shimPointer;
        if (pointer != 0) return pointer;
        lock (ShimDelegate)
        {
            if (_shimPointer == 0) _shimPointer = Marshal.GetFunctionPointerForDelegate(ShimDelegate);
            return _shimPointer;
        }
    }

    // ------------------------------------------------------------------ the entry

    /// <summary>
    /// The managed entry the engine now reaches instead of the generated invoker. Runs on the engine's
    /// thread inside a native callout: it must return normally under every circumstance.
    /// </summary>
    private static void Shim(nint methodPointer, Il2CppMethodInfo* methodInfo, nint obj, nint* args, nint* returnValue)
    {
        Plan plan = null;
        try
        {
            var key = (nint)methodInfo;
            if (!Plans.TryGetValue(key, out plan))
            {
                if (methodPointer != 0 && PlansByMethodPointer.TryGetValue(methodPointer, out plan))
                {
                    // The engine handed a copy of the method info; its thunk identified it.
                }
                else if (methodInfo != null && EnsureAdopted(methodInfo, "callout", null)
                                               && Plans.TryGetValue(key, out plan))
                {
                    
                }
                else
                {
                    return;
                }
            }

            object instance = null;
            if (!plan.Static)
            {
                // Resolved before the entry trace on purpose: a callout that has nothing to run never
                // enters managed code, so it must not publish the "entered" half of the pair whose
                // unmatched half is what names the method the process died inside (TraceEntered/TraceLeft).
                if (!TryResolveInstance(key, obj, plan, out instance))
                {
                    return;
                }
            }

            var arguments = plan.NewArguments();
            if (arguments.Length != 0)
            {
                if (args == null) throw new InvalidOperationException("the engine passed no argument array");
                for (var i = 0; i < arguments.Length; i++) arguments[i] = plan.Arguments[i].Read(args[i]);
            }

            var result = plan.Target.Invoke(instance, arguments);

            // By-ref/out: the callee wrote into the argument slots of the array; hand those values back to
            // the addresses the engine gave us.
            for (var i = 0; i < arguments.Length; i++)
            {
                var write = plan.Arguments[i].WriteBack;
                if (write != null) write(args[i], arguments[i]);
            }

            var writeReturn = plan.Return.Write;
            if (writeReturn != null && returnValue != null) writeReturn(result, (nint)returnValue);

            // The entry line is written before the call, so "entered" without a matching "left" names the
            // method the process died inside. A fail fast kills the process mid-call, which is exactly the
            // signal: the diagnostic tail is the last thing written before the fatal.
        }
        catch (Exception exception)
        {
            ReportRuntimeFailure(plan, exception);
        }
    }

    /// <summary>
    /// Resolves the managed instance of one callout without ever letting the interop's lookup throw.
    ///
    /// <c>ClassInjectorBase.GetMonoObjectFromIl2CppPointer</c> ends in
    /// <c>GCHandle.FromIntPtr(handle).Target</c> (ildump of the installed Il2CppInterop.Runtime.dll:
    /// <c>GetGcHandlePtrFromIl2CppObject</c>, then the delegate fallback, then <c>GCHandle.FromIntPtr</c>).
    /// For an object whose managed side is already gone that handle is zero, and the call throws
    /// <c>InvalidOperationException: "Handle is not initialized."</c> - measured, not assumed: it is the
    /// exception behind the seventeen finalizer entries of the 2026-10-10 raid
    /// (analysis/log-noise-cleanup/REPORT.md). The handle pointer itself can be read without throwing, so
    /// the condition is decided *before* the throwing call: a zero handle means "this callout has nothing
    /// to run", which is a normal end of the callout and not a failure.
    ///
    /// Returns false when there is nothing to run (reported once per method). Never throws.
    /// </summary>
    private static bool TryResolveInstance(nint key, nint obj, Plan plan, out object instance)
    {
        instance = null;
        if (obj == 0)
        {
            ReportNothingToRun(plan, "the callout carries no object pointer");
            return false;
        }

        // The interop has a second source for the handle - a delegate keeps the one of its target, see
        // ClassInjectorBase.FallbackGetGcHandlePtrFromIl2CppDelegateMTarget - and that shape is handed to
        // the interop lookup unchanged. Every other object with a zero handle has no managed side at all,
        // and asking for it would only produce the exception the first-chance probe has to report.
        if (ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(obj) == 0 && !IsMulticastDelegate(obj))
        {
            ReportNothingToRun(plan, "Handle is not initialized.");
            return false;
        }

        try
        {
            instance = ClassInjectorBase.GetMonoObjectFromIl2CppPointer(obj);
        }
        catch (Exception exception) when (IsNothingToRun(exception))
        {
            // The same verdict through the interop's own path: nothing to run, never a failure.
            ReportNothingToRun(plan, exception.Message);
            return false;
        }

        if (instance == null)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when the object is - or may be - an <c>Il2CppSystem.MulticastDelegate</c>, the one shape whose
    /// managed handle lives on its target instead of on itself. Deliberately true whenever the check cannot
    /// be made (no native class, or the interop's class pointer is not materialized yet): the caller then
    /// keeps the interop's own lookup, i.e. exactly the behaviour this port had before the fast path
    /// existed, instead of silently skipping a callout it cannot classify.
    /// </summary>
    private static bool IsMulticastDelegate(nint obj)
    {
        try
        {
            var delegateClass = Il2CppClassPointerStore<Il2CppSystem.MulticastDelegate>.NativeClassPtr;
            var klass = IL2CPP.il2cpp_object_get_class(obj);
            if (delegateClass == IntPtr.Zero || klass == IntPtr.Zero) return true;
            return IL2CPP.il2cpp_class_is_assignable_from(delegateClass, klass);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// The one line the shim writes for a callout that has nothing to run. This is the finalizer path:
    /// il2cpp collects the native side of an injected object and then calls <c>Finalize</c> once more,
    /// while the managed side - and with it the GC handle the instance is resolved through - is already
    /// gone. Deduplicated per method and reached without throwing anything: the same condition used to be
    /// discovered by letting the interop's lookup throw, which turned every collection into one
    /// first-chance exception and one detailed probe entry (17 of them in a single raid).
    /// </summary>
    private static void ReportNothingToRun(Plan plan, string reason)
    {
        try
        {
            var name = plan == null ? "<unknown>" : plan.Name;
            if (!_reportedNothingToRun.TryAdd(name, 0)) return;
            
        }
        catch (Exception)
        {
        }
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>
    /// One line per injected method: the pointer triple that turns a dump's calli target or invoker stub
    /// back into a method name. The 2026-10-10 host raid needed a loader record to do this by hand.
    /// </summary>
    private static bool Report(nint key, string line)
    {
        try
        {
            if (!Reported.TryAdd(key, 0)) return false;
            
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ReportRuntimeFailure(Plan plan, Exception exception)
    {
        try
        {
            var name = plan == null ? "<unknown>" : plan.Name;

            // One line per method. The finalizer path used to print the same failure seventeen times per
            // collection (see IsNothingToRun below), which buried every other diagnostic.
            if (!_reportedFailures.TryAdd(name, 0)) return;

            if (IsNothingToRun(exception))
            {
                ReportNothingToRun(plan, exception.Message);
                return;
            }

            
            _reportTrampolineException ??= AccessTools.DeclaredMethod(typeof(ClassInjector), "ReportTrampolineException");
            _reportTrampolineException?.Invoke(null, new object[] { exception, name });
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// True when the failure means "this callout has nothing to run" rather than "the shim is broken": the
    /// object no longer has a managed instance, which is what the finalizer path looks like (il2cpp collects
    /// the il2cpp side and then calls Finalize once more).
    /// </summary>
    private static bool IsNothingToRun(Exception exception) =>
        exception is InvalidOperationException
        && exception.Message.Contains("Handle is not initialized", StringComparison.Ordinal);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _reportedFailures = new();

    /// <summary>
    /// Methods whose "nothing to run" end (the finalizer path, see ReportNothingToRun) was already written.
    /// A separate table from <see cref="_reportedFailures"/> on purpose: a method can legitimately end both
    /// ways in one session, and one must not silence the other.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _reportedNothingToRun = new();

    private static string Describe(MethodInfo method) =>
        method == null ? "<unknown method>" : method.DeclaringType?.FullName + "." + method.Name;
}
