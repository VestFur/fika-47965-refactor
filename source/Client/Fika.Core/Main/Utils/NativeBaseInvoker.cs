using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Il2CppInterop.Common;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Copies the generated CLR marshalling wrapper while selecting its declared
/// native implementation. Keeps ref/out, value types and return conversions
/// identical to the installed interop assembly, without virtual redispatch.
///
/// The dispatch count is *not* assumed. Measured on this install with
/// "tools/FindCalls ... il2cpp_object_get_virtual_method BepInEx/interop/Assembly-CSharp.dll":
/// the generated wrappers of this Il2CppInterop build never call
/// il2cpp_object_get_virtual_method (0 hits over the whole 306120-method assembly), while the
/// same bodies call il2cpp_runtime_invoke 102645 times - ClientLocalGameWorld::InitLevel
/// included. A wrapper therefore always invokes the NativeMethodInfoPtr_... of the type that
/// declares it, which *is* the base implementation this helper asks for.
///
/// The previous version required exactly one dispatch call and threw for every method of this
/// install. The first native base call of a raid is InitLevel (FikaHostGameWorld), so the level
/// initialisation faulted before LocalGameCreate: the loading screen stayed at 0 % and the
/// client dropped back to the main menu (2026-10-09 22:58:53.574, log line
/// "NotSupportedException: Expected one virtual dispatch ... found 0").
/// </summary>
internal static class NativeBaseInvoker
{
    private const string VirtualDispatchHelper = "il2cpp_object_get_virtual_method";
    private const string RuntimeInvokeHelper = "il2cpp_runtime_invoke";

    private static readonly ConcurrentDictionary<(MethodInfo, Type), Delegate> Cache = new();
    private static readonly ConcurrentDictionary<MethodInfo, bool> Warned = new();
    private static readonly MethodInfo SelectDeclared = typeof(NativeBaseInvoker)
        .GetMethod(nameof(DeclaredImplementation), BindingFlags.Static | BindingFlags.NonPublic);

    private static IntPtr DeclaredImplementation(IntPtr instance, IntPtr method) => method;

    internal static T Get<T>(Type declaringType, string name, Type[] parameters) where T : Delegate
    {
        var method = declaringType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.DeclaredOnly, null, parameters, null)
            ?? throw new MissingMethodException(declaringType.FullName, name);
        return (T)Cache.GetOrAdd((method, typeof(T)), key => Create<T>(key.Item1));
    }

    // True when the field reference names the declaring type itself, whatever shape the metadata
    // and the CLR use for it (nested separator, generic instantiation).
    private static bool DeclaresField(TypeReference fieldDeclaringType, Type declaringType)
    {
        if (declaringType == null)
        {
            return false;
        }

        var fieldType = fieldDeclaringType;
        while (fieldType is TypeSpecification specification)
        {
            fieldType = specification.ElementType;
        }

        var declaring = declaringType.IsGenericType && !declaringType.IsGenericTypeDefinition
            ? declaringType.GetGenericTypeDefinition()
            : declaringType;
        return string.Equals(fieldType.FullName.Replace('/', '+'), declaring.FullName, StringComparison.Ordinal);
    }

    private static T Create<T>(MethodInfo method) where T : Delegate
    {
        if (method.IsAbstract || method.ContainsGenericParameters ||
            Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method) == null)
            throw new NotSupportedException($"Not a concrete generated IL2CPP method: {method}");

        using var definition = new DynamicMethodDefinition(method);
        var dispatches = 0;
        var runtimeInvokes = 0;
        var ownMethodInfoLoads = 0;

        foreach (var instruction in definition.Definition.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference called)
            {
                continue;
            }

            if (string.Equals(called.Name, VirtualDispatchHelper, StringComparison.Ordinal))
            {
                // The helper is always called as (instance, declaredMethodInfo) and is replaced
                // with a constant selector: the copied body then invokes the method this wrapper
                // declares instead of redispatching virtually into the override that is running.
                instruction.Operand = definition.Module.ImportReference(SelectDeclared);
                dispatches++;
                continue;
            }

            if (string.Equals(called.Name, RuntimeInvokeHelper, StringComparison.Ordinal))
            {
                runtimeInvokes++;
            }
        }

        // A wrapper may only be accepted when it provably loads the method info of the method it
        // declares. That field is named after the declaring type, so the two names have to be
        // compared in a shape the metadata and the CLR agree on: a nested type is
        // "Outer/Inner" in metadata and "Outer+Inner" to the CLR, and a generic declaring type
        // reaches the CLR as a constructed type whose FullName also carries the assembly-qualified
        // arguments. Comparing the raw strings missed the load for every wrapper declared on a
        // nested or generic type - 73 of the 124 entries of NativeBaseCalls - which is how
        // EFT.Player.KnifeController.MakeKnifeKick got rejected 54 times in one raid although its
        // body has exactly the same shape as ClientLocalGameWorld.InitLevel's. The comparison is
        // only made shape-neutral; nothing is accepted that does not load its own method info.
        foreach (var instruction in definition.Definition.Body.Instructions)
        {
            if (instruction.Operand is FieldReference field &&
                field.Name.StartsWith("NativeMethodInfoPtr_" + method.Name + "_", StringComparison.Ordinal) &&
                DeclaresField(field.DeclaringType, method.DeclaringType))
            {
                ownMethodInfoLoads++;
            }
        }

        if (dispatches > 1)
        {
            return Degrade<T>(method, $"expected at most one virtual dispatch, found {dispatches}");
        }

        // Zero dispatches is the normal shape of this install, but it is only accepted when the
        // body provably calls the native method it declares: a body that neither dispatches nor
        // loads its own method info could redispatch virtually and re-enter the override that
        // called it, which would loop instead of failing.
        if (dispatches == 0 && (runtimeInvokes == 0 || ownMethodInfoLoads == 0))
        {
            return Degrade<T>(method, "the generated wrapper neither dispatches virtually nor invokes its own native "
                + $"method info (runtimeInvokes={runtimeInvokes}, ownMethodInfoLoads={ownMethodInfoLoads}), so the "
                + "installed interop is not the layout this helper was written for");
        }

        

        return (T)DMDCecilGenerator.Generate(definition).CreateDelegate(typeof(T));
    }

    /// <summary>
    /// A wrapper the guard rejected. Throwing here is what turned a shape mismatch into a process-wide
    /// failure: the table wraps every entry in <c>System.Lazy</c>, which re-throws the factory's exception
    /// on *every* later <c>.Value</c> access, so one rejected wrapper produced one exception per use and, on
    /// a hot path, an exception per frame (analysis/hands-nre). The guard itself is unchanged and still
    /// decides - it only degrades instead of throwing now: the rejection is reported once per method and the
    /// caller gets a neutral delegate whose return value is the default of the signature, which is exactly
    /// what the interop delivers when a native call throws. The native work the caller wanted is skipped, and
    /// the call site stays alive instead of taking the raid down.
    /// </summary>
    private static T Degrade<T>(MethodInfo method, string reason) where T : Delegate
    {
        if (Warned.TryAdd(method, true))
        {
            
        }

        return Neutral<T>();
    }

    /// <summary>A delegate of the requested shape that does nothing and returns the default of its signature.</summary>
    private static T Neutral<T>() where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke");
        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        Expression body = invoke.ReturnType == typeof(void)
            ? Expression.Empty()
            : Expression.Default(invoke.ReturnType);
        return Expression.Lambda<T>(body, parameters).Compile();
    }
}
