using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The sweep: every injected method info has to be on the shim, not just the ones the registration time
/// postfix saw.
///
/// Why the registration time postfix is not enough (analysis/managed-invoker/REPORT-sweep.md): the
/// postfix lives on the Harmony instance of <see cref="NativeInjectionScope"/>, and that instance
/// unpatchs its targets when its scope is disposed (<c>NativeInjectionScope.Dispose</c>). Fika's own
/// registration finishes long before the game starts, so from that moment on the injector converts
/// methods with the stock - calli shaped, JIT rejected - invoker again:
///
///   * an *in-scope* type another plugin registers later (BepInEx's <c>Il2CppManagedEnumerator</c> is
///     registered lazily on the first <c>WrapToIl2Cpp()</c>, which is exactly <c>Fika.Core</c>'s coroutine
///     helper, and it is in scope as a port-reached compatibility type),
///   * an *in-scope* type registered before this class was installed (Fika's own <c>FikaPluginBehaviour</c>),
///   * out-of-scope types (the interop's own delegate holder, ConfigurationManager, SPTushonka) are
///     enumerated and named once by <see cref="ReportOutOfScope"/>, but never rewritten - the scope
///     region in NativeManagedInvoker.cs is the rule, and it is checked before any method info is touched,
///   * a generic instantiation the engine asks for at runtime
///     (<c>Hooks.GenericMethod_GetMethod_Hook</c> / <c>..._Unity6_Hook</c> call
///     <c>ClassInjector.ConvertMethodInfo</c> directly, from native code, at any time).
///
/// Three independent nets close that, in the order the engine meets them:
///
///   1. <b>registration hook</b> (postfix on <c>ClassInjector.RegisterTypeInIl2CppInternal</c>, on this
///      class's own Harmony instance): a type that is registered after the port's scope is swept while
///      its registration returns;
///   2. <b>sweep</b> (<see cref="Sweep"/>): every injected class known to the injector is enumerated
///      through <c>il2cpp_class_get_methods</c> and every method info with a stock invoker is adopted;
///      it runs at install, after Fika's own registration, and once when the engine first reaches a
///      method info the shim does not know;
///   3. <b>runtime guard</b> (prefix on <c>IL2CPP.il2cpp_runtime_invoke</c>, see
///      <see cref="EnsureAdopted"/>): the last moment before the native side dereferences
///      <c>InvokerMethod</c> - an unknown method info is adopted there, and the original invoke runs
///      unchanged either way.
///
/// Nothing here throws and nothing here changes a call's meaning: it either replaces an entry the
/// engine has not entered yet, or it writes one diagnostic line.
/// </summary>
internal static unsafe partial class NativeManagedInvoker
{
    private sealed class ClassEntry
    {
        internal Type Type;
        internal nint Pointer;
    }

    /// <summary>Injected types this class has seen, from the registration hook, the sweep or Fika itself.</summary>
    private static readonly ConcurrentDictionary<Type, byte> KnownTypes = new();

    /// <summary>Native class pointer to the managed type it was registered from.</summary>
    private static readonly ConcurrentDictionary<nint, Type> TypesByClass = new();

    /// <summary>Full name to managed type, for the injector's own registry (which stores names only).</summary>
    private static readonly ConcurrentDictionary<string, Type> TypesByName = new();

    private static int _sweepRewritten;
    private static int _sweepKept;
    private static int _sweepEnumerated;
    private static int _sweepAlreadyShim;
    private static int _sweepNoInvoker;
    private static int _sweepTypes;
    private static int _sweepUnresolvedNames;
    private static int _vtableSlots;
    private static int _vtableStockInvokers;
    private static int _vtableRepointed;
    private static int _runtimeRepairs;
    private static int _runtimeSweep;
    private static int _unresolvedReports;
    private static int _scopeForeignTypes;

    /// <summary>
    /// Re-entrancy guard for the thread that is sweeping. Diagnostics write a frame number, which is an
    /// il2cpp callout, which reaches the runtime guard, which would otherwise start a second sweep from
    /// inside the first one. One sweep at a time per thread is enough: the inner one would enumerate the
    /// same classes.
    /// </summary>
    [ThreadStatic]
    private static bool _sweeping;

    /// <summary>Remembers an injected type. Called from the registration hook, before the type is swept.</summary>
    internal static void RememberType(Type type)
    {
        if (type == null) return;
        try
        {
            KnownTypes.TryAdd(type, 0);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Sweeps every injected class: enumerates its method infos, adopts the ones still on the stock
    /// invoker, and reports what the vtable slots hold. <paramref name="explicitTypes"/> are swept even
    /// if the injector's registry cannot be read.
    /// </summary>
    internal static void Sweep(Type[] explicitTypes, string reason)
    {
        if (_sweeping) return;
        _sweeping = true;
        try
        {
            var classes = CollectClasses(explicitTypes, out var unresolved);
            var enumerated = 0;
            var rewritten = 0;
            var kept = 0;
            var noInvoker = 0;
            var already = 0;
            foreach (var entry in classes)
                SweepClass(entry.Type, entry.Pointer, reason, ref enumerated, ref rewritten, ref kept, ref noInvoker, ref already);

            Interlocked.Add(ref _sweepEnumerated, enumerated);
            Interlocked.Add(ref _sweepRewritten, rewritten);
            Interlocked.Add(ref _sweepKept, kept);
            Interlocked.Add(ref _sweepAlreadyShim, already);
            Interlocked.Add(ref _sweepNoInvoker, noInvoker);
            Interlocked.Add(ref _sweepTypes, classes.Count);
            Interlocked.Add(ref _sweepUnresolvedNames, unresolved);

            
            
        }
        catch (Exception exception)
        {
            
        }
        finally
        {
            _sweeping = false;
        }
    }

    /// <summary>
    /// Sweeps the injected types of one type. Called right after a registration, so a type that appears
    /// while the port's own registration scope is long gone is covered in the same instant it appears.
    /// </summary>
    internal static void SweepType(Type type, string reason)
    {
        if (_sweeping) return;
        _sweeping = true;
        try
        {
            if (type == null) return;
            RememberType(type);
            var pointer = ClassPointerOf(type);
            if (pointer == 0) return;
            TypesByClass[pointer] = type;
            var enumerated = 0;
            var rewritten = 0;
            var kept = 0;
            var noInvoker = 0;
            var already = 0;
            SweepClass(type, pointer, reason, ref enumerated, ref rewritten, ref kept, ref noInvoker, ref already);
            Interlocked.Add(ref _sweepEnumerated, enumerated);
            Interlocked.Add(ref _sweepRewritten, rewritten);
            Interlocked.Add(ref _sweepKept, kept);
            Interlocked.Add(ref _sweepAlreadyShim, already);
            Interlocked.Add(ref _sweepNoInvoker, noInvoker);
            if (rewritten != 0 || kept != 0)
            {
                
            }
        }
        catch (Exception exception)
        {
            
        }
        finally
        {
            _sweeping = false;
        }
    }

    /// <summary>
    /// The whole sweep, once, the first time the engine reaches a method info the shim does not know.
    /// Bounded by design: after the first call this does nothing.
    /// </summary>
    internal static void SweepOnce(string reason)
    {
        // Not while a sweep is already running on this thread, and not twice: the token is only spent
        // when the sweep is really attempted.
        if (_sweeping) return;
        if (Interlocked.Exchange(ref _runtimeSweep, 1) != 0) return;
        Sweep(null, reason);
    }

    /// <summary>
    /// Adopts a method info the shim has never seen: resolves the managed method from the method info
    /// itself (its declaring class and name) and builds a plan for it. Returns true when the method info
    /// is now served by the shim - whether it already was, or was adopted just now.
    /// </summary>
    internal static bool EnsureAdopted(Il2CppMethodInfo* info, string origin, string detail)
    {
        var key = (nint)info;
        if (key == 0) return false;
        try
        {
            if (Plans.ContainsKey(key)) return true;
            if (NotOurs.ContainsKey(key)) return false;
            var wrapper = UnityVersionHandler.Wrap(info);
            if (wrapper == null) return false;
            if (!IsInjectedMethodInfo(wrapper))
            {
                // The engine's own method infos are never ours to touch; remember that so the guard
                // stays a dictionary lookup on every later invoke.
                NotOurs[key] = 0;
                return false;
            }

            if (wrapper.InvokerMethod == 0)
            {
                // Abstract methods and generic definitions: the injector binds no invoker at all.
                NotOurs[key] = 0;
                return false;
            }

            var declaringType = TypeOfClass(wrapper);
            if (declaringType != null && !IsShimScope(declaringType))
            {
                // The runtime guard's own scope gate: this method info belongs to another plugin, so the
                // injector's invoker stays. Memoized like every other "not ours" verdict, so the guard
                // remains a dictionary lookup on the invoke path.
                NotOurs[key] = 0;
                ReportOutOfScope(declaringType, origin);
                return false;
            }

            var name = Marshal.PtrToStringUTF8(wrapper.Name);
            var method = ResolveManagedMethod(declaringType, name, wrapper);
            if (method == null)
            {
                ReportUnresolved(key, declaringType, name, wrapper, origin);
                return false;
            }

            if (!string.Equals(origin, "registration", StringComparison.Ordinal))
                Interlocked.Increment(ref _runtimeRepairs);
            return TryAdopt(info, method, origin, detail, out _);
        }
        catch (Exception exception)
        {
            NotOurs[key] = 0;
            
            return false;
        }
    }

    private static void ReportUnresolved(nint key, Type declaringType, string name, INativeMethodInfoStruct info, string origin)
    {
        try
        {
            if (Interlocked.Increment(ref _unresolvedReports) > 32) return;
            var native = declaringType?.FullName ?? NativeFullName(UnityVersionHandler.Wrap(info.Class));
            
        }
        catch (Exception)
        {
        }
    }

    // ------------------------------------------------------------------ enumeration

    private static List<ClassEntry> CollectClasses(Type[] explicitTypes, out int unresolved)
    {
        var result = new List<ClassEntry>();
        var seen = new HashSet<nint>();
        var missing = 0;

        void Consider(Type type)
        {
            if (type == null) return;
            RememberType(type);
            var pointer = ClassPointerOf(type);
            if (pointer == 0) return;
            if (!seen.Add(pointer)) return;
            TypesByClass[pointer] = type;
            result.Add(new ClassEntry { Type = type, Pointer = pointer });
        }

        foreach (var type in KnownTypes.Keys)
        {
            try
            {
                Consider(type);
            }
            catch (Exception)
            {
            }
        }

        if (explicitTypes != null)
        {
            foreach (var type in explicitTypes)
            {
                try
                {
                    Consider(type);
                }
                catch (Exception)
                {
                }
            }
        }

        foreach (var name in InjectorTypeNames())
        {
            var type = ResolveTypeByName(name);
            if (type == null)
            {
                missing++;
                continue;
            }

            try
            {
                Consider(type);
            }
            catch (Exception)
            {
            }
        }

        unresolved = missing;
        return result;
    }

    /// <summary>
    /// The injector's own registry of registered type names. It is the only source that also holds the
    /// types registered before this class was installed, and the only one that names them without a
    /// native call.
    /// </summary>
    private static string[] InjectorTypeNames()
    {
        try
        {
            var field = AccessTools.Field(typeof(ClassInjector), "InjectedTypes");
            if (field?.GetValue(null) is not IEnumerable<string> names) return Array.Empty<string>();
            var copy = new List<string>();
            // The set belongs to the injector and is not thread safe: a registration running in parallel
            // can make this enumeration throw, which is why it is bounded and caught.
            var enumerator = names.GetEnumerator();
            while (true)
            {
                try
                {
                    if (!enumerator.MoveNext()) break;
                }
                catch (Exception)
                {
                    break;
                }

                if (enumerator.Current != null) copy.Add(enumerator.Current);
            }

            return copy.ToArray();
        }
        catch (Exception exception)
        {
            
            return Array.Empty<string>();
        }
    }

    private static void SweepClass(Type type, nint pointer, string reason, ref int enumerated, ref int rewritten, ref int kept,
        ref int noInvoker, ref int already)
    {
        // The scope gate (NativeManagedInvoker, "scope of the shim"): a class that is not Fika's own is
        // left exactly as the injector built it - its method infos *and* its vtable slots, because the
        // scan below repoints slots, which is a change to another plugin's call path as well.
        if (!IsShimScope(type))
        {
            if (ReportOutOfScope(type, reason)) Interlocked.Increment(ref _scopeForeignTypes);
            return;
        }

        var shim = ShimPointer();
        var iterator = nint.Zero;
        while (true)
        {
            nint method;
            try
            {
                method = IL2CPP.il2cpp_class_get_methods(pointer, ref iterator);
            }
            catch (Exception exception)
            {
                
                break;
            }

            if (method == 0) break;
            enumerated++;
            try
            {
                var info = UnityVersionHandler.Wrap((Il2CppMethodInfo*)method);
                if (info == null) continue;
                if (Plans.ContainsKey(method))
                {
                    already++;
                    continue;
                }

                if (info.InvokerMethod == 0)
                {
                    // Abstract methods and generic definitions: the injector binds no invoker, so there
                    // is no stock entry to replace. An *inflated* generic method is a different method
                    // info, created by the injector's own hook, and reaches the shim through the
                    // registration hook and the runtime guard.
                    noInvoker++;
                    continue;
                }

                var name = Marshal.PtrToStringUTF8(info.Name);
                if (string.Equals(name, "Finalize", StringComparison.Ordinal) && info.InvokerMethod != shim)
                {
                    // The engine runs finalizers on the native side and there is no managed method to invoke
                    // (a C# finalizer is a destructor), so adopting it only produced one
                    // "Handle is not initialized" per collection. It keeps the injector's invoker.
                    kept++;
                    
                    continue;
                }

                var mono = ResolveManagedMethod(type, name, info);
                if (mono == null)
                {
                    kept++;
                    
                    continue;
                }

                if (TryAdopt((Il2CppMethodInfo*)method, mono, "sweep:" + reason, null, out _)) rewritten++;
                else kept++;
            }
            catch (Exception exception)
            {
                kept++;
                
            }
        }

        ScanVTable(type, pointer, ref enumerated, ref rewritten, ref kept);
    }

    /// <summary>
    /// Reports what the class's vtable holds. A slot holds a *method info* plus the entry a direct call
    /// uses, so replacing the invoker of that method info is what repairs the slot; a slot that holds
    /// the invoker itself - the reflection ABI, five arguments - is repointed at the method's own
    /// trampoline, which is the pointer the injector writes there during registration.
    /// </summary>
    private static void ScanVTable(Type type, nint pointer, ref int enumerated, ref int rewritten, ref int kept)
    {
        try
        {
            var klass = UnityVersionHandler.Wrap((Il2CppClass*)pointer);
            if (klass == null) return;
            var slots = (VirtualInvokeData*)klass.VTable;
            if (slots == null) return;
            var count = (int)klass.VtableCount;
            var stock = 0;
            var repointed = 0;
            for (var i = 0; i < count; i++)
            {
                var method = (nint)slots[i].method;
                if (method == 0) continue;
                var info = UnityVersionHandler.Wrap((Il2CppMethodInfo*)method);
                if (info == null) continue;
                var invoker = info.InvokerMethod;
                if (invoker == 0 || Plans.ContainsKey(method)) continue;
                if (!IsInjectedMethodInfo(info)) continue;
                stock++;
                var name = Marshal.PtrToStringUTF8(info.Name);
                var pointedAtInvoker = slots[i].methodPtr != 0 && slots[i].methodPtr == invoker;
                if (pointedAtInvoker && info.MethodPointer != 0)
                {
                    slots[i].methodPtr = info.MethodPointer;
                    repointed++;
                }

                var adopted = EnsureAdopted((Il2CppMethodInfo*)method, "vtable", "slot " + i + " of " + type.FullName);
                if (adopted) rewritten++;
                else kept++;
                
            }

            Interlocked.Add(ref _vtableSlots, count);
            Interlocked.Add(ref _vtableStockInvokers, stock);
            Interlocked.Add(ref _vtableRepointed, repointed);
            if (stock != 0)
            {
                
            }
        }
        catch (Exception exception)
        {
            
        }
    }

    // ------------------------------------------------------------------ resolution

    private static nint ClassPointerOf(Type type)
    {
        try
        {
            var pointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
            if (pointer == 0) return 0;
            return RuntimeSpecificsStore.IsInjected(pointer) ? pointer : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool IsInjectedMethodInfo(INativeMethodInfoStruct info)
    {
        try
        {
            return info.Class != null && RuntimeSpecificsStore.IsInjected((nint)info.Class);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Type TypeOfClass(INativeMethodInfoStruct info)
    {
        try
        {
            var pointer = (nint)info.Class;
            if (pointer == 0) return null;
            if (TypesByClass.TryGetValue(pointer, out var known) && known != null) return known;
            var klass = UnityVersionHandler.Wrap(info.Class);
            if (klass == null) return null;
            var fullName = NativeFullName(klass);
            var type = ResolveTypeByName(fullName);
            if (type == null)
            {
                // The injector writes only the simple name and the namespace of an injected type
                // (ClassInjector.decompiled.cs:439-440), so a nested type reads as if it were top
                // level here. Match that shape against the types this class does know.
                type = ResolveTypeByFlattenedName(fullName);
            }

            if (type != null)
            {
                TypesByClass[pointer] = type;
                RememberType(type);
            }

            return type;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string NativeFullName(INativeClassStruct klass)
    {
        try
        {
            if (klass == null) return string.Empty;
            var name = Marshal.PtrToStringUTF8(klass.Name) ?? string.Empty;
            var declaring = klass.DeclaringType;
            if (declaring != null)
            {
                var outer = NativeFullName(UnityVersionHandler.Wrap(declaring));
                return string.IsNullOrEmpty(outer) ? name : outer + "+" + name;
            }

            var space = Marshal.PtrToStringUTF8(klass.Namespace);
            return string.IsNullOrEmpty(space) ? name : space + "." + name;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static Type ResolveTypeByName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        if (TypesByName.TryGetValue(fullName, out var cached)) return cached;
        Type found = null;
        try
        {
            try
            {
                found = Type.GetType(fullName, throwOnError: false);
            }
            catch (Exception)
            {
                found = null;
            }

            if (found == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        found = assembly.GetType(fullName, throwOnError: false);
                    }
                    catch (Exception)
                    {
                        found = null;
                    }

                    if (found != null) break;
                }
            }
        }
        catch (Exception)
        {
        }

        if (found != null) TypesByName[fullName] = found;
        return found;
    }

    private static Type ResolveTypeByFlattenedName(string flattened)
    {
        if (string.IsNullOrEmpty(flattened)) return null;
        try
        {
            foreach (var type in KnownTypes.Keys)
            {
                if (type.FullName != null && string.Equals(type.FullName.Replace('+', '.'), flattened, StringComparison.Ordinal))
                {
                    TypesByName[flattened] = type;
                    return type;
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static MethodInfo ResolveManagedMethod(Type type, string name, INativeMethodInfoStruct info)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;
        var isStatic = (info.Flags & Il2CppMethodFlags.METHOD_ATTRIBUTE_STATIC) != 0;
        var count = (int)info.ParametersCount;
        var candidates = new List<MethodInfo>();
        for (var scope = type; scope != null && scope != typeof(object); scope = scope.BaseType)
        {
            try
            {
                foreach (var method in scope.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != name) continue;
                    if (method.IsStatic != isStatic) continue;
                    if (method.GetParameters().Length != count) continue;
                    candidates.Add(method);
                }
            }
            catch (Exception)
            {
            }

            if (candidates.Count != 0) break;
        }

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        foreach (var candidate in candidates)
        {
            if (ParametersMatchNative(candidate, info)) return candidate;
        }

        return candidates[0];
    }

    /// <summary>
    /// Compares one candidate's parameter list with the native signature of the method info, the same
    /// way <c>ClassInjector.FindOverrideByName</c> disambiguates an override.
    /// </summary>
    private static bool ParametersMatchNative(MethodInfo method, INativeMethodInfoStruct info)
    {
        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            nint native;
            try
            {
                native = IL2CPP.il2cpp_class_from_il2cpp_type((nint)UnityVersionHandler.Wrap(info.Parameters, i).ParameterType);
            }
            catch (Exception)
            {
                return false;
            }

            var managed = parameters[i].ParameterType;
            if (managed.IsByRef) managed = managed.GetElementType();
            nint expected;
            try
            {
                expected = Il2CppClassPointerStore.GetNativeClassPointer(managed);
            }
            catch (Exception)
            {
                continue;
            }

            if (expected == 0 || native == 0) continue;
            if (expected != native) return false;
        }

        return true;
    }
}
