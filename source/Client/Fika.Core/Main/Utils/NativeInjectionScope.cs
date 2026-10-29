using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The injector resolves System.Nullable&lt;T&gt; to a CLR struct, whereas generated
/// game wrappers expose Il2CppSystem.Nullable&lt;T&gt;. Correct that signature lookup
/// only while registering Fika types; keep the generated wrapper's native ABI.
/// </summary>
internal sealed class NativeInjectionScope : IDisposable
{
    private readonly Harmony _harmony = new("com.fika.core.native-signatures");
    private readonly Assembly _assembly;
    private static Assembly _activeAssembly;
    private readonly MethodInfo _resolver;
    private readonly MethodInfo _postfix;
    private readonly MethodInfo _prefix;
    private readonly MethodInfo _rewriter;
    private readonly MethodInfo _rewritePrefix;
    private readonly MethodInfo _eligibility;
    private readonly MethodInfo _eligibilityPrefix;
    private readonly MethodInfo _converter;
    private readonly MethodInfo _converterPostfix;
    private readonly MethodInfo _interfaceLookup;
    private readonly MethodInfo _interfacePrefix;
    private readonly MethodInfo _trampoline;
    private readonly MethodInfo _invoker;
    private readonly MethodInfo _invokerCapture;
    // A real CLR generic parameter, used only as a lookup marker for inherited
    // open generic methods. It is never written into a native signature.
    private static readonly Type InheritedParameter = typeof(InheritedGeneric<>).GetGenericArguments()[0];
    private sealed class InheritedGeneric<T> { }

    // ------------------------------------------------------------------
    // Injected-method invoker: replace the emitted calli with a managed call.
    //
    // Il2CppInterop fills an injected method's MethodInfo with two pointers, and BOTH
    // are managed reverse P/Invoke stubs (Marshal.GetFunctionPointerForDelegate over a
    // managed delegate): InvokerMethod (the invoker) and MethodPointer (the per-method
    // trampoline). CreateInvoker's generated body then does
    //     ldarg.0; <push native args>; EmitCalli(Calli, Cdecl, trampolineReturn, trampolineParams)
    // i.e. managed code performs an indirect call straight into a managed reverse
    // P/Invoke stub. CoreCLR rejects exactly that combination with an uncatchable
    // fail-fast ("attempted to call a UnmanagedCallersOnly method from managed code",
    // HRESULT 0x80131506), which is what killed the 2026-10-10 14:22 host raid.
    //
    // The injector already avoids that combination for constructors: their InvokerMethod
    // is the managed StaticVoidIntPtrInvoker_MetadataV29, which turns the method pointer
    // back into the SAME cached delegate with Marshal.GetDelegateForFunctionPointer<T>
    // and invokes it like any other managed delegate (ClassInjector.decompiled.cs:1541-1544).
    // The code below applies the same trick to every injected method. The rewrite happens while
    // ONE body is being built, and any mismatch keeps the injector's original indirect call
    // (ResolveCurrentTrampolineInvoke returns null and the helper re-emits the calli verbatim).
    // ------------------------------------------------------------------

    /// <summary>Method currently being turned into an invoker, set by <see cref="CaptureInvokerMethod"/>.</summary>
    [ThreadStatic]
    private static MethodInfo _invokerMethod;

    /// <summary>Delegate type for a method pointer, resolved once (or its failure reported once).</summary>
    private static readonly Dictionary<nint, DelegateInvocation> ManagedTrampolineInvocations = new();

    /// <summary>Method pointers whose invoker body already reported its call site, so the log stays quiet.</summary>
    private static readonly HashSet<nint> ReportedInvokerCallSites = new();

    /// <summary>Once-per-process state of the managed invoker path.</summary>
    private static bool _managedInvokerDegraded;

    // Il2CppInterop's delegate factory, its nested signature key, Marshal's generic
    // pointer->delegate entry point, and one compiled converter per delegate type.
    private static MethodInfo _getOrCreateDelegateType;
    private static ConstructorInfo _methodSignatureConstructor;
    private static MethodInfo _getDelegateForFunctionPointer;
    private static readonly Dictionary<Type, MethodInfo> DelegateConverters = new();
    private static readonly MethodInfo _reportInvokerCallSite =
        typeof(NativeInjectionScope).GetMethod(nameof(ReportInvokerCallSite), BindingFlags.Static | BindingFlags.NonPublic);

    private sealed class DelegateInvocation
    {
        internal Type DelegateType;
        internal MethodInfo Invoke;
        internal int ParameterCount;
    }

    internal NativeInjectionScope(Assembly assembly)
    {
        _assembly = assembly;
        if (System.Threading.Interlocked.CompareExchange(ref _activeAssembly, assembly, null) != null)
            throw new InvalidOperationException("Native injection scopes cannot overlap.");
        try
        {
            _resolver = AccessTools.DeclaredMethod(typeof(ClassInjector), "SystemTypeFromIl2CppType")
                ?? throw new MissingMethodException(typeof(ClassInjector).FullName, "SystemTypeFromIl2CppType");
            _postfix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(UseGeneratedNullable));
            _prefix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(KeepInheritedGeneric));
            _harmony.Patch(_resolver, prefix: new HarmonyMethod(_prefix), postfix: new HarmonyMethod(_postfix));
            _rewriter = AccessTools.DeclaredMethod(typeof(ClassInjector), "RewriteType");
            _rewritePrefix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(KeepOpenSignature));
            _harmony.Patch(_rewriter, prefix: new HarmonyMethod(_rewritePrefix));
            _eligibility = AccessTools.DeclaredMethod(typeof(ClassInjector), "IsMethodEligible");
            _eligibilityPrefix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(KeepNativeSignatures));
            _harmony.Patch(_eligibility, prefix: new HarmonyMethod(_eligibilityPrefix));
            _converter = AccessTools.DeclaredMethod(typeof(ClassInjector), "ConvertMethodInfo");
            _converterPostfix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(BindUsableItemEntry));
            _harmony.Patch(_converter, postfix: new HarmonyMethod(_converterPostfix));
            // The invoker of every injected method is what the engine reaches reflectively; the
            // generated body ends in a calli into a reverse P/Invoke thunk, which is the fatal shape
            // (analysis/managed-invoker). Replace the entry where the shape is covered, keep the
            // injector's own invoker everywhere else. Installed here, before the first type registers.
            //
            // These hooks deliberately live on their own Harmony instance (see
            // NativeManagedInvoker.OwnHarmonyId): Dispose() below unpatches every target this scope
            // patched, and a rewrite hook that disappears with the scope is exactly what let the methods
            // registered afterwards - BepInEx's coroutine wrapper, a generic instantiation the injector
            // converts at runtime, another plugin's types - keep the stock, calli shaped invoker
            // (analysis/managed-invoker/REPORT-sweep.md).
            NativeManagedInvoker.Install();
            _interfaceLookup = AccessTools.DeclaredMethod(typeof(ClassInjector), "FindBaseInterfaceImplementation");
            _interfacePrefix = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(MatchInterfaceSignature));
            _harmony.Patch(_interfaceLookup, prefix: new HarmonyMethod(_interfacePrefix));
            _trampoline = AccessTools.DeclaredMethod(typeof(ClassInjector), "CreateTrampoline");
            _harmony.Patch(_trampoline, transpiler: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(PreserveNullableBoxing))));
            _harmony.Patch(_trampoline, transpiler: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(PreserveByRefValues))));
            _invoker = AccessTools.DeclaredMethod(typeof(ClassInjector), "CreateInvoker");
            _harmony.Patch(_invoker, transpiler: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(PreserveInvokerReferences))));
            // The invoker body must also not perform an indirect call into the managed
            // trampoline stub; the metadata plane names the same method, so capture it with a
            // prefix on the same body and resolve the delegate type it would have to reproduce.
            // A missing interop API must not abort the load: the helper then keeps the
            // injector's own indirect call and reports it once (see ResolveCurrentTrampolineInvoke).
            ResolveDelegateSupport();
            _invokerCapture = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(CaptureInvokerMethod));
            _harmony.Patch(_invoker, prefix: new HarmonyMethod(_invokerCapture));
            _harmony.Patch(_invoker, transpiler: new HarmonyMethod(
                AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(PreserveInvokerCalli))));
        }
        catch
        {
            // A throwing constructor is never disposed by a using statement.
            // Remove any hooks already installed before allowing load to fail.
            Dispose();
            throw;
        }
    }

    private static unsafe bool MatchInterfaceSignature(Type type, INativeClassStruct baseClass, int slot, ref MethodInfo __result)
    {
        if (type.Assembly != _activeAssembly) return true;
        __result = null;
        // The installed resolver matches only name/arity. ApplyItem's two
        // overloads have the same arity but incompatible value/reference ABIs.
        // Use the actual interface slot's full parameter signature instead.
        var resolver = AccessTools.DeclaredMethod(typeof(ClassInjector), "SystemTypeFromIl2CppType");
        Type Resolve(Il2CppTypeStruct* pointer) => (Type)resolver.Invoke(null,
            new[] { Pointer.Box(pointer, resolver.GetParameters()[0].ParameterType) });
        for (var i = 0; i < baseClass.InterfaceOffsetsCount; i++)
        {
            var offset = baseClass.InterfaceOffsets[i];
            var contract = UnityVersionHandler.Wrap(offset.interfaceType);
            if (slot < offset.offset || slot >= offset.offset + contract.MethodCount) continue;
            var managedContract = Resolve((Il2CppTypeStruct*)IL2CPP.il2cpp_class_get_type((IntPtr)offset.interfaceType));
            if (!managedContract.IsInterface || !managedContract.IsAssignableFrom(type)) continue;
            var nativeMethod = UnityVersionHandler.Wrap(contract.Methods[slot - offset.offset]);
            var name = Marshal.PtrToStringUTF8(nativeMethod.Name);
            var parameters = new Type[nativeMethod.ParametersCount];
            for (var p = 0; p < parameters.Length; p++)
                parameters[p] = Resolve(UnityVersionHandler.Wrap(nativeMethod.Parameters, p).ParameterType);
            var map = type.GetInterfaceMap(managedContract);
            for (var m = 0; m < map.InterfaceMethods.Length; m++)
            {
                var method = map.InterfaceMethods[m];
                if (method.Name != name || method.IsGenericMethod != nativeMethod.IsGeneric ||
                    !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters)) continue;
                var implementation = map.TargetMethods[m];
                if (implementation.DeclaringType == type) __result = implementation;
                return false;
            }
        }
        return false;
    }

    private static unsafe void BindUsableItemEntry(MethodInfo monoMethod, Il2CppMethodInfo* __result)
    {
        if (!IsUsableItemEntry(monoMethod)) return;
        // Proceed<T> has a closed ABI and its body does not depend on T.
        // Il2CppInterop leaves generic definitions' pointers empty, then rejects
        // the vtable slot. Supply a valid fallback while retaining IsGeneric and
        // the injector's normal per-instantiation inflation dictionary.
        var closed = monoMethod.MakeGenericMethod(typeof(EFT.Player.UsableItemController));
        var trampoline = (Delegate)AccessTools.DeclaredMethod(typeof(ClassInjector), "GetOrCreateTrampoline").Invoke(null, new object[] { closed });
        var invoker = (Delegate)AccessTools.DeclaredMethod(typeof(ClassInjector), "GetOrCreateInvoker").Invoke(null, new object[] { closed });
        var info = UnityVersionHandler.Wrap(__result);
        info.MethodPointer = Marshal.GetFunctionPointerForDelegate(trampoline);
        info.VirtualMethodPointer = info.MethodPointer;
        info.InvokerMethod = Marshal.GetFunctionPointerForDelegate(invoker);
    }

    private static bool IsUsableItemEntry(MethodInfo method) =>
        method.DeclaringType == typeof(Players.FikaPlayer) && method.Name == "Proceed" && method.IsGenericMethodDefinition;

    private static bool KeepNativeSignatures(MethodInfo method, ref bool __result)
    {
        if (method.DeclaringType?.Assembly != _activeAssembly) return true;
        var nativeOverride = method.GetBaseDefinition().DeclaringType?.Assembly != _activeAssembly;
        var managedGeneric = method.IsGenericMethodDefinition && !nativeOverride;
        if (method.IsGenericMethodDefinition && nativeOverride && !IsUsableItemEntry(method))
            throw new NotSupportedException($"Generic native override requires an explicit ABI adapter: {method}");
        if (!managedGeneric && IsNativeSignatureType(method.ReturnType) &&
            method.GetParameters().All(p => IsNativeSignatureType(p.ParameterType))) return true;
        if (nativeOverride)
            throw new NotSupportedException($"Native override has a managed-only signature: {method.DeclaringType}.{method}");
        // ValueTask and Fika packet structs pass the stock IsValueType check,
        // but have no native class/ABI. Their calls remain entirely managed.
        __result = false;
        return false;
    }

    internal static bool IsNativeSignatureType(Type type)
    {
        if (type.IsByRef) return IsNativeSignatureType(type.GetElementType());
        if (type.ContainsGenericParameters) return false;
        if (type == typeof(void) || type == typeof(string) || type.IsPrimitive ||
            type == typeof(IntPtr) || type == typeof(UIntPtr)) return true;
        if (typeof(IIl2CppObjectBase).IsAssignableFrom(type)) return true;
        return type.IsValueType && type.Assembly != _activeAssembly &&
            type.Assembly != typeof(FikaPlugin).Assembly &&
            type.Assembly != typeof(object).Assembly;
    }

    private static bool KeepOpenSignature(Type type, ref Type __result)
    {
        if (!type.ContainsGenericParameters) return true;
        // E.g. IEnumerable<TInventoryOperation>. FullName is null for an open
        // constructed type. Preserve it for lookup; retain the native method.
        __result = type;
        return false;
    }

    private static unsafe bool KeepInheritedGeneric(Il2CppTypeStruct* typePointer, ref Type __result)
    {
        var type = UnityVersionHandler.Wrap(typePointer);
        if (type.Type != Il2CppTypeEnum.IL2CPP_TYPE_MVAR) return true;
        // Execute<TInventoryOperation> is inherited intact. The stock resolver
        // treats TInventoryOperation as an assembly type and throws before it
        // can retain that native vtable slot. No Fika method may match this
        // marker; ValidateDeclaredMethods enforces that boundary.
        __result = type.ByRef ? InheritedParameter.MakeByRefType() : InheritedParameter;
        return false;
    }

    internal static void ValidateDeclaredMethods(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.IsDefined(typeof(HideFromIl2CppAttribute), false)) continue;
            if (method.IsSpecialName && type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Any(p => (p.GetMethod == method || p.SetMethod == method) && p.IsDefined(typeof(HideFromIl2CppAttribute), false))) continue;
            var definition = method.GetBaseDefinition();
            if (method.IsAbstract && definition.DeclaringType?.Assembly == type.Assembly)
                throw new NotSupportedException($"Managed abstract members must be hidden from the native injector: {type}.{method}");
            if (definition.DeclaringType?.Assembly == type.Assembly) continue;
            foreach (var parameter in method.GetParameters())
                if (parameter.ParameterType.ContainsGenericParameters)
                    throw new NotSupportedException($"Native injection of open generic parameters requires an explicit adapter: {type}.{method}");
        }
    }

    private static void UseGeneratedNullable(ref Type __result)
    {
        var byRef = __result.IsByRef;
        var type = byRef ? __result.GetElementType() : __result;
        var element = Nullable.GetUnderlyingType(type);
        if (element == null) return;
        var wrapper = typeof(Il2CppSystem.Nullable<>).MakeGenericType(element);
        __result = byRef ? wrapper.MakeByRefType() : wrapper;
    }

    // This rewrites the CLR injector's IL generator only during Fika registration.
    // Native Nullable boxing erases the wrapper (null or boxed T); generated
    // interop parameters require an actual Nullable<T> object with both fields.
    private static System.Collections.Generic.IEnumerable<CodeInstruction> PreserveNullableBoxing(
        System.Collections.Generic.IEnumerable<CodeInstruction> source)
    {
        var pendingBoxLookup = false;
        var replacements = 0;
        foreach (var instruction in source)
        {
            yield return instruction;
            if (instruction.opcode == System.Reflection.Emit.OpCodes.Ldstr &&
                Equals(instruction.operand, nameof(IL2CPP.il2cpp_value_box))) pendingBoxLookup = true;
            else if (pendingBoxLookup && instruction.operand is MethodInfo method &&
                method.DeclaringType == typeof(Type) && method.Name == nameof(Type.GetMethod))
            {
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call,
                    AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(SelectValueBox)));
                pendingBoxLookup = false;
                replacements++;
            }
        }
        if (replacements != 1) throw new NotSupportedException($"Expected one trampoline boxing lookup, found {replacements}.");
    }

    // The installed injector emits ldind.i/stind.i for ref structs. That reads
    // and writes only a pointer-sized portion of Vector3 (and misreads float
    // references). Emit typed copies for CLR value types; retain the original
    // reference conversion for native objects, strings and arrays.
    private static System.Collections.Generic.IEnumerable<CodeInstruction> PreserveByRefValues(
        System.Collections.Generic.IEnumerable<CodeInstruction> source)
    {
        var instructions = source.ToList();
        CodeInstruction elementLocal = null;
        var readPending = false;
        var writePending = false;
        var reads = 0;
        var writes = 0;
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.operand is MethodInfo element && element.DeclaringType == typeof(Type) &&
                element.Name == nameof(Type.GetElementType))
            {
                var store = instructions[i + 1];
                var index = store.opcode == System.Reflection.Emit.OpCodes.Stloc_0 ? 0 :
                    store.opcode == System.Reflection.Emit.OpCodes.Stloc_1 ? 1 :
                    store.opcode == System.Reflection.Emit.OpCodes.Stloc_2 ? 2 :
                    store.opcode == System.Reflection.Emit.OpCodes.Stloc_3 ? 3 : -1;
                if (index >= 0) elementLocal = new CodeInstruction(System.Reflection.Emit.OpCodes.Ldloc, index);
                else if (store.opcode == System.Reflection.Emit.OpCodes.Stloc || store.opcode == System.Reflection.Emit.OpCodes.Stloc_S)
                    elementLocal = new CodeInstruction(System.Reflection.Emit.OpCodes.Ldloc, store.operand);
                else throw new NotSupportedException("Trampoline element type is no longer stored in a local.");
            }
            if (instruction.operand is FieldInfo field)
            {
                if (field.DeclaringType == typeof(System.Reflection.Emit.OpCodes) && field.Name == "Ldind_I") readPending = true;
                if (field.Name == "StIndOpcodes") writePending = true;
            }
            if ((readPending || writePending) && instruction.operand is MethodInfo emit &&
                emit.DeclaringType == typeof(System.Reflection.Emit.ILGenerator) && emit.Name == "Emit" &&
                emit.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(System.Reflection.Emit.OpCode) }))
            {
                if (elementLocal == null) throw new NotSupportedException("Missing byref element local.");
                var load = new CodeInstruction(elementLocal);
                load.labels.AddRange(instruction.labels);
                instruction.labels.Clear();
                load.blocks.AddRange(instruction.blocks);
                instruction.blocks.Clear();
                yield return load;
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(NativeInjectionScope),
                    readPending ? nameof(EmitByRefRead) : nameof(EmitByRefWrite));
                if (readPending) reads++; else writes++;
                readPending = writePending = false;
            }
            yield return instruction;
        }
        if (reads != 1 || writes != 1)
            throw new NotSupportedException($"Expected one byref read/write emitter; found {reads}/{writes}.");
    }

    private static void EmitByRefRead(System.Reflection.Emit.ILGenerator body, System.Reflection.Emit.OpCode original, Type element)
    {
        if (element.IsValueType) body.Emit(System.Reflection.Emit.OpCodes.Ldobj, element);
        else body.Emit(original);
    }

    private static void EmitByRefWrite(System.Reflection.Emit.ILGenerator body, System.Reflection.Emit.OpCode original, Type element)
    {
        if (element.IsValueType) body.Emit(System.Reflection.Emit.OpCodes.Stobj, element);
        else body.Emit(original);
    }

    private static System.Collections.Generic.IEnumerable<CodeInstruction> PreserveInvokerReferences(
        System.Collections.Generic.IEnumerable<CodeInstruction> source)
    {
        foreach (var instruction in source)
        {
            if (instruction.operand is MethodInfo emit && emit.DeclaringType == typeof(System.Reflection.Emit.ILGenerator) &&
                emit.Name == "Emit" && emit.GetParameters().Select(p => p.ParameterType)
                    .SequenceEqual(new[] { typeof(System.Reflection.Emit.OpCode), typeof(Type) }))
            {
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(EmitInvokerValue));
            }
            yield return instruction;
        }
    }

    private static void EmitInvokerValue(System.Reflection.Emit.ILGenerator body, System.Reflection.Emit.OpCode opcode, Type type)
    {
        // args[i] already contains the address of a ref/out argument. Native
        // object references are rewritten to IntPtr*, CLR structs to T&.
        // Neither address should be dereferenced a second time.
        if (opcode == System.Reflection.Emit.OpCodes.Ldobj && (type.IsByRef || type.IsPointer)) return;
        body.Emit(opcode, type);
    }

    // ------------------------------------------------------------------
    // The invoker body: calli -> managed delegate call
    // ------------------------------------------------------------------

    /// <summary>
    /// Harmony prefix on <c>ClassInjector.CreateInvoker</c>: remembers which method's body is
    /// being built, so the transpiler below can resolve the exact same trampoline delegate type
    /// that <c>CreateTrampoline</c> created for it. Registration is a single, synchronous pass
    /// per thread, and the transpiler runs inside that call, before the value is cleared.
    /// </summary>
    private static void CaptureInvokerMethod(object[] __args)
    {
        try
        {
            _invokerMethod = __args.Length > 0 ? __args[0] as MethodInfo : null;
        }
        catch (Exception exception)
        {
            _invokerMethod = null;
            
        }
    }

    /// <summary>
    /// Rewrites <c>ILGenerator.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, returnType,
    /// parameterTypes)</c> - the one indirect call in <c>CreateInvoker</c> - into a call that
    /// emits the managed-delegate sequence instead (see <see cref="EmitManagedTrampolineInvoke"/>).
    /// If the delegate shape cannot be reproduced, the original calli is left untouched and the
    /// downgrade is reported once: a fix that cannot be proven must not make the callout worse.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<CodeInstruction> PreserveInvokerCalli(
        System.Collections.Generic.IEnumerable<CodeInstruction> source)
    {
        // This runs ONCE, when the injector's body is first patched - there is no method being
        // injected yet. It may therefore only redirect the call: the decision to keep the
        // original indirect call belongs to the helper below, which runs while the injector
        // builds one concrete body (Harmony transpilers are not re-run per invocation).
        var callis = 0;
        foreach (var instruction in source)
        {
            if (instruction.operand is MethodInfo emit && IsCalliEmitter(emit))
            {
                callis++;
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(NativeInjectionScope),
                    nameof(EmitManagedTrampolineInvoke));
            }
            yield return instruction;
        }
        if (callis != 1)
            ReportDowngrade("the injector no longer has exactly one indirect call in CreateInvoker;", null);
    }

    private static bool IsCalliEmitter(MethodInfo method)
    {
        if (method.DeclaringType != typeof(System.Reflection.Emit.ILGenerator)) return false;
        if (method.Name != nameof(System.Reflection.Emit.ILGenerator.EmitCalli)) return false;
        var parameters = method.GetParameters();
        if (parameters.Length != 4) return false;
        return parameters[0].ParameterType == typeof(System.Reflection.Emit.OpCode)
            && parameters[1].ParameterType == typeof(CallingConvention)
            && parameters[2].ParameterType == typeof(Type)
            && parameters[3].ParameterType == typeof(Type[]);
    }

    /// <summary>
    /// Emits the managed equivalent of the calli that <c>CreateInvoker</c> used to emit.
    ///
    /// Stack contract (identical to the calli's, which is what makes this a drop-in rewrite):
    ///   in:  [native parameters..., methodPointer]   (the method pointer is pushed last)
    ///   out: the trampoline's native return value (or nothing for a void trampoline)
    ///
    /// The trampoline delegate is <c>(nint obj, [nint returnBuffer,] nativeParameters...,
    /// Il2CppMethodInfo* methodInfo)</c> - exactly the calli's parameter list plus the trailing
    /// <c>methodInfo</c>, which the invoker holds in argument 1 (<c>ldarg.1</c>).
    /// </summary>
    internal static void EmitManagedTrampolineInvoke(System.Reflection.Emit.ILGenerator body,
        System.Reflection.Emit.OpCode opcode, CallingConvention callingConvention, Type returnType,
        Type[] parameterTypes)
    {
        // Everything is decided here, while one concrete body is being built. Any mismatch
        // keeps the injector's own indirect call: a fix that cannot be proven must not turn a
        // working (if unsafe) call into a broken one.
        var invocation = ResolveCurrentTrampolineInvoke(opcode, callingConvention, returnType, parameterTypes);
        if (invocation == null)
        {
            body.EmitCalli(opcode, callingConvention, returnType, parameterTypes);
            return;
        }

        // 1) Save what the calli would have consumed. The stack is [parameters..., pointer]
        //    (pointer last), so the stores run in reverse: the pointer is stashed first.
        var locals = new System.Reflection.Emit.LocalBuilder[parameterTypes.Length + 1];
        for (var i = parameterTypes.Length; i >= 0; i--)
        {
            var type = i == parameterTypes.Length ? typeof(nint) : parameterTypes[i];
            locals[i] = body.DeclareLocal(type);
            body.Emit(System.Reflection.Emit.OpCodes.Stloc, locals[i]);
        }

        // 2) methodPointer -> the exact delegate instance CreateTrampoline installed.
        body.Emit(System.Reflection.Emit.OpCodes.Ldloc, locals[parameterTypes.Length]);
        body.Emit(System.Reflection.Emit.OpCodes.Call, DelegateConverter(invocation.DelegateType));

        // 3) The trampoline's own arguments: the calli parameters in their original order...
        for (var i = 0; i < parameterTypes.Length; i++)
            body.Emit(System.Reflection.Emit.OpCodes.Ldloc, locals[i]);

        // ...plus the Il2CppMethodInfo* the invoker received as its second argument.
        var methodInfo = body.DeclareLocal(typeof(Il2CppMethodInfo*));
        body.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        body.Emit(System.Reflection.Emit.OpCodes.Stloc, methodInfo);
        body.Emit(System.Reflection.Emit.OpCodes.Ldloc, methodInfo);

        // 3b) Record which method the engine reached, once per method pointer. This is the
        //     only direct evidence of who calls an injected method reflectively.
        body.Emit(System.Reflection.Emit.OpCodes.Ldloc, methodInfo);
        body.Emit(System.Reflection.Emit.OpCodes.Conv_I);
        body.Emit(System.Reflection.Emit.OpCodes.Call, _reportInvokerCallSite);

        // 4) A normal managed delegate invocation; the trampoline runs, no reverse P/Invoke
        //    transition and no calli into a managed stub is involved.
        body.Emit(System.Reflection.Emit.OpCodes.Ldloc, methodInfo);
        body.Emit(System.Reflection.Emit.OpCodes.Callvirt, invocation.Invoke);
    }

    /// <summary>
    /// Resolves the delegate type <c>CreateTrampoline</c> is using for the method currently being
    /// transpiled by calling the injector's own factory with the same argument it uses
    /// (<c>new DelegateSupport.MethodSignature(monoMethod, hasThis: true)</c>).
    /// <c>DelegateSupport.GetOrCreateDelegateType</c> caches its result by that signature, so this
    /// returns the very same <see cref="Type"/> object - the type identity the round trip through
    /// <c>Marshal</c> depends on (see analysis/injected-invoker-fix/REPORT.md).
    /// </summary>
    /// <summary>
    /// Resolves the trampoline delegate for the method whose body is being built right now.
    /// Returns null - after reporting once - whenever anything about the shape does not match,
    /// so the caller emits the injector's original indirect call instead.
    /// </summary>
    private static DelegateInvocation ResolveCurrentTrampolineInvoke(System.Reflection.Emit.OpCode opcode,
        CallingConvention callingConvention, Type returnType, Type[] parameterTypes)
    {
        if (opcode != System.Reflection.Emit.OpCodes.Calli)
        {
            ReportDowngrade("the invoker's indirect call is no longer a Calli", null);
            return null;
        }
        if (callingConvention != CallingConvention.Cdecl)
        {
            ReportDowngrade("the invoker's indirect call is no longer Cdecl", null);
            return null;
        }
        if (_invokerMethod == null)
        {
            ReportDowngrade("an invoker body was built without CaptureInvokerMethod running first", null);
            return null;
        }
        try
        {
            if (!ResolveDelegateSupport())
            {
                ReportDowngrade("Il2CppInterop's DelegateSupport API is unavailable in this install", null);
                return null;
            }
            var delegateType = ResolveTrampolineDelegateType();
            var invocation = ResolveManagedTrampolineInvoke(delegateType);
            if (invocation?.Invoke == null)
            {
                ReportDowngrade("the trampoline delegate of " + DescribeMethod(_invokerMethod)
                    + " has no usable Invoke method", null);
                return null;
            }
            if (invocation.ParameterCount != parameterTypes.Length || invocation.Invoke.ReturnType != returnType)
            {
                ReportDowngrade("the trampoline delegate of " + DescribeMethod(_invokerMethod)
                    + " does not match the invoker's indirect call (, " + parameterTypes.Length + " vs "
                    + invocation.ParameterCount + " parameter(s), " + returnType?.Name + " vs "
                    + invocation.Invoke.ReturnType.Name + ")", null);
                return null;
            }
            return invocation;
        }
        catch (Exception exception)
        {
            ReportDowngrade("resolving the trampoline delegate of " + DescribeMethod(_invokerMethod) + " failed", exception);
            return null;
        }
    }

    private static bool ResolveDelegateSupport()
    {
        if (_getOrCreateDelegateType != null) return true;
        _getOrCreateDelegateType = AccessTools.Method(typeof(DelegateSupport), "GetOrCreateDelegateType");
        _getDelegateForFunctionPointer = AccessTools.Method(typeof(Marshal), "GetDelegateForFunctionPointer",
            new[] { typeof(nint) });
        if (_getOrCreateDelegateType == null || _getDelegateForFunctionPointer == null
            || !_getDelegateForFunctionPointer.IsGenericMethodDefinition
            || _getDelegateForFunctionPointer.GetParameters().Length != 1)
        {
            _getOrCreateDelegateType = null;
            _getDelegateForFunctionPointer = null;
            return false;
        }
        return true;
    }

    private static Type ResolveTrampolineDelegateType()
    {
        var method = _invokerMethod;
        if (method == null) throw new InvalidOperationException("no method being injected on this thread");
        var signature = CreateMethodSignature(method);
        return (Type)_getOrCreateDelegateType.Invoke(null, new[] { signature, method });
    }

    private static object CreateMethodSignature(MethodInfo method)
    {
        if (_methodSignatureConstructor == null)
        {
            var type = typeof(DelegateSupport).GetNestedType("MethodSignature", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(typeof(DelegateSupport).FullName, "MethodSignature");
            _methodSignatureConstructor = type.GetConstructor(new[] { typeof(MethodInfo), typeof(bool) })
                ?? throw new MissingMethodException(type.FullName, ".ctor(MethodInfo, bool)");
        }
        return _methodSignatureConstructor.Invoke(new object[] { method, true });
    }

    /// <summary>
    /// Resolves (and caches) the data <see cref="EmitManagedTrampolineInvoke"/> needs for a
    /// delegate type. Returns null - never throws - when the delegate has no usable Invoke, so
    /// the caller falls back to the original calli.
    /// </summary>
    private static DelegateInvocation ResolveManagedTrampolineInvoke(Type delegateType)
    {
        if (ManagedTrampolineInvocations.TryGetValue((nint)delegateType.TypeHandle.Value, out var known))
            return known;
        try
        {
            if (!typeof(Delegate).IsAssignableFrom(delegateType)) return null;
            var invoke = delegateType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
            if (invoke == null || invoke.GetParameters().Length == 0) return null;
            var resolved = new DelegateInvocation
            {
                DelegateType = delegateType,
                Invoke = invoke,
                ParameterCount = invoke.GetParameters().Length - 1,
            };
            ManagedTrampolineInvocations[(nint)delegateType.TypeHandle.Value] = resolved;
            return resolved;
        }
        catch (Exception exception)
        {
            ReportDowngrade("inspecting the trampoline delegate of " + DescribeMethod(_invokerMethod) + " failed", exception);
            return null;
        }
    }

    // Marshal.GetDelegateForFunctionPointer<T>(nint), closed over the exact trampoline delegate
    // type and cached: the invoker body calls a plain static method, not reflection.
    private static MethodInfo DelegateConverter(Type delegateType)
    {
        lock (DelegateConverters)
        {
            if (DelegateConverters.TryGetValue(delegateType, out var cached)) return cached;
            var method = BuildDelegateConverter(delegateType);
            DelegateConverters[delegateType] = method;
            return method;
        }
    }

    private static MethodInfo BuildDelegateConverter(Type delegateType)
    {
        var parameters = new[] { typeof(Delegate), typeof(nint) };
        var method = new System.Reflection.Emit.DynamicMethod(
            "LoadTrampoline_" + delegateType.Name, typeof(Delegate), parameters, typeof(NativeInjectionScope), true);
        var body = method.GetILGenerator();
        body.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        body.Emit(System.Reflection.Emit.OpCodes.Castclass, delegateType);
        body.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        body.Emit(System.Reflection.Emit.OpCodes.Call,
            _getDelegateForFunctionPointer.MakeGenericMethod(delegateType));
        body.Emit(System.Reflection.Emit.OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// One diagnostic per distinct method pointer: this is the entry point the engine actually
    /// reached, which is exactly what identifies whoever called an injected method reflectively.
    /// Runs inside an il2cpp callout, so it only reads the native method info struct - never an
    /// il2cpp object's ToString - and never lets a failure disturb the invocation.
    /// </summary>
    private static unsafe void ReportInvokerCallSite(nint methodInfoPointer)
    {
        try
        {
            var key = methodInfoPointer;
            lock (ReportedInvokerCallSites)
            {
                if (!ReportedInvokerCallSites.Add(key)) return;
            }
            var description = DescribeNativeMethod((Il2CppMethodInfo*)methodInfoPointer);
            
        }
        catch (Exception)
        {
        }
    }

    private static unsafe string DescribeNativeMethod(Il2CppMethodInfo* methodInfo)
    {
        if (methodInfo == null) return "<unknown>";
        var wrapper = UnityVersionHandler.Wrap(methodInfo);
        if (wrapper == null) return "<unknown>";
        var name = Marshal.PtrToStringUTF8(wrapper.Name);
        var declaring = DescribeNativeClass(wrapper.Class);
        if (string.IsNullOrEmpty(name)) name = "<unnamed>";
        return declaring + "." + name;
    }

    private static unsafe string DescribeNativeClass(Il2CppClass* klass)
    {
        if (klass == null) return "<unknown>";
        var wrapper = UnityVersionHandler.Wrap(klass);
        var name = Marshal.PtrToStringUTF8(wrapper.Name);
        var space = Marshal.PtrToStringUTF8(wrapper.Namespace);
        if (string.IsNullOrEmpty(space)) return string.IsNullOrEmpty(name) ? "<unknown>" : name;
        return space + "." + name;
    }

    private static string DescribeMethod(MethodInfo method) =>
        method == null ? "<unknown>" : method.DeclaringType?.FullName + "." + method.Name;

    private static void ReportDowngrade(string what, Exception exception)
    {
        try
        {
            if (_managedInvokerDegraded) return;
            _managedInvokerDegraded = true;
            var message = "the injected invoker keeps its original indirect call: " + what
                + " (" + (_invokerMethod == null ? "no method" : DescribeMethod(_invokerMethod)) + ")";
            
            
        }
        catch (Exception)
        {
        }
    }

    private static MethodInfo SelectValueBox(MethodInfo original) =>
        AccessTools.DeclaredMethod(typeof(NativeInjectionScope), nameof(BoxNativeValue));

    internal static unsafe IntPtr BoxNativeValue(IntPtr klass, IntPtr data)
    {
        if (IL2CPP.il2cpp_class_get_name_(klass) != "Nullable`1" || IL2CPP.il2cpp_class_get_namespace_(klass) != "System")
            return IL2CPP.il2cpp_value_box(klass, data);

        var boxed = IL2CPP.il2cpp_object_new(klass);
        var headerSize = IL2CPP.il2cpp_object_unbox(boxed).ToInt64() - boxed.ToInt64();
        var iterator = IntPtr.Zero;
        IntPtr field;
        while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iterator)) != IntPtr.Zero)
        {
            if ((IL2CPP.il2cpp_field_get_flags(field) & (int)FieldAttributes.Static) != 0) continue;
            var offset = (long)IL2CPP.il2cpp_field_get_offset(field) - headerSize;
            // The field API handles native references/write barriers as well
            // as primitive values; do not assume Nullable<T> is blittable.
            IL2CPP.il2cpp_field_set_value(boxed, field, (byte*)data + offset);
        }
        return boxed;
    }

    public void Dispose()
    {
        foreach (var target in new[] { _resolver, _rewriter, _eligibility, _converter, _interfaceLookup, _trampoline, _invoker })
            if (target != null) _harmony.Unpatch(target, HarmonyPatchType.All, _harmony.Id);
        System.Threading.Interlocked.CompareExchange(ref _activeAssembly, null, _assembly);
    }
}
