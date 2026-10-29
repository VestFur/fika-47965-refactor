using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.Utils;

public static class FikaNativeTypes
{
    internal static Type[] RegistrationOrder() => RegistrationOrder(typeof(FikaPlugin).Assembly);

    private static Type[] RegistrationOrder(Assembly assembly) => assembly.GetTypes()
        .Where(t => t.IsClass && !t.ContainsGenericParameters && typeof(Il2CppObjectBase).IsAssignableFrom(t))
        .OrderBy(Depth).ThenBy(t => t.FullName, StringComparer.Ordinal).ToArray();

    private static int Depth(Type type)
    {
        var depth = 0;
        for (; type.BaseType != null; type = type.BaseType) depth++;
        return depth;
    }

    internal static void RegisterAll(ManualLogSource logger)
    {
        RegisterAll(typeof(FikaPlugin).Assembly, logger);
    }

    public static void RegisterAll(Assembly assembly, ManualLogSource logger)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(logger);
        using var signatures = new NativeInjectionScope(assembly);
        var types = RegistrationOrder(assembly);
        var defaultImplemented = new List<string>();
        var inheritedDescriptors = new List<string>();
        foreach (var type in types)
        {
            ValidateInterfaces(type, defaultImplemented);
            NativeInjectionScope.ValidateDeclaredMethods(type);
        }
        if (defaultImplemented.Count > 0)
            logger.LogWarning($"Interface validation skipped {defaultImplemented.Count} default-implemented member(s) with no native vtable slot: "
                + $"{string.Join(", ", defaultImplemented.Distinct())}. The interop contract supplies their body (the *_Managed helpers and the "
                + "bridges to BCL interface members), so the injector never binds them; every real native slot is still enforced.");
        foreach (var type in types)
        {
            if (type.GetConstructor(new[] { typeof(IntPtr) }) == null)
                throw new MissingMethodException(type.FullName, ".ctor(IntPtr)");
            logger.LogInfo($"Registering Fika native type: {type.FullName}");
            try
            {
                InitializeNativeHierarchy(type.BaseType, assembly);
                foreach (var contract in type.GetInterfaces().Where(i =>
                             i != typeof(IIl2CppObjectBase) && typeof(IIl2CppObjectBase).IsAssignableFrom(i)))
                    InitializeNativeClass(contract);
                logger.LogDebug($"Native hierarchy ready: {type.FullName}");
                // Keep the runtime's declared-interface resolver: passing an
                // empty list would discard game contracts such as IBotGame.
                ClassInjector.RegisterTypeInIl2Cpp(type);
                if (Il2CppClassPointerStore.GetNativeClassPointer(type) == IntPtr.Zero)
                    throw new InvalidOperationException("Registration returned a null native class");
                if (NativeInjectedGc.PreserveReferences(type))
                    inheritedDescriptors.Add(type.FullName);
                NativeInjectedFinalizer.Register(type);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Fika cannot register {type.FullName} (base {type.BaseType}).", exception);
            }
        }
        if (inheritedDescriptors.Count > 0)
            logger.LogWarning($"Il2CppInterop copied a base class GC descriptor into {inheritedDescriptors.Count} Fika native type(s). "
                + "That descriptor only describes the base class layout, while the injector appends this class's own fields after it, "
                + "so the descriptor was dropped and conservative reference tracing was enabled instead: "
                + string.Join(", ", inheritedDescriptors));
        logger.LogInfo($"Registered {types.Length} Fika native types from {assembly.GetName().Name}.");
        // The invoker of every injected method has to be the managed shim, not only the methods the
        // registration time postfix happened to see: sweep the types once the registration is complete
        // (analysis/managed-invoker/REPORT-sweep.md). Runs before the entry points are validated, so a
        // method the sweep rewrote is validated in its final shape.
        NativeManagedInvoker.Sweep(types, "Fika registration complete");
        // JIT validate the generated native entry points while a rejection is still catchable
        // (analysis/adjust-shot-vectors/REPORT.md).
        NativeEntryWarmup.WarmAll(types, logger);
    }

    private static void InitializeNativeHierarchy(Type type, Assembly assembly)
    {
        if (type == null || !typeof(Il2CppObjectBase).IsAssignableFrom(type)) return;
        InitializeNativeHierarchy(type.BaseType, assembly);
        if (type.Assembly == assembly || type == typeof(Il2CppObjectBase)) return;
        InitializeNativeClass(type);
    }

    private static void InitializeNativeClass(Type type)
    {
        var pointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException($"Native base is unavailable: {type}");
        // IsAssignableFrom on distinct classes calls vm::Class::Init, which
        // materializes vtables/interface offsets without running game ctors.
        // runtime_class_init only runs the managed static constructor.
        var objectClass = Il2CppClassPointerStore<Il2CppSystem.Object>.NativeClassPtr;
        if (pointer != objectClass)
            _ = IL2CPP.il2cpp_class_is_assignable_from(objectClass, pointer);
        // Native class_init does not necessarily materialize each ancestor's
        // lazy method table; the injector walks these tables directly.
        var iterator = IntPtr.Zero;
        while (IL2CPP.il2cpp_class_get_methods(pointer, ref iterator) != IntPtr.Zero) { }
        iterator = IntPtr.Zero;
        IntPtr contract;
        while ((contract = IL2CPP.il2cpp_class_get_interfaces(pointer, ref iterator)) != IntPtr.Zero)
        {
            _ = IL2CPP.il2cpp_class_is_assignable_from(objectClass, contract);
            var methods = IntPtr.Zero;
            while (IL2CPP.il2cpp_class_get_methods(contract, ref methods) != IntPtr.Zero) { }
            _ = IL2CPP.il2cpp_class_is_assignable_from(contract, pointer);
        }
    }

    private static void ValidateInterfaces(Type type, List<string> defaultImplemented)
    {
        if (type.IsAbstract) return;
        foreach (var contract in type.GetInterfaces().Where(i => i.Assembly != type.Assembly &&
                     i != typeof(IIl2CppObjectBase) && typeof(IIl2CppObjectBase).IsAssignableFrom(i)))
        {
            var map = type.GetInterfaceMap(contract);
            for (var index = 0; index < map.TargetMethods.Length; index++)
            {
                if (map.TargetMethods[index].DeclaringType?.IsInterface != true) continue;
                var member = map.InterfaceMethods[index];
                // Only the interop's native slots exist in the game metadata and
                // need a managed implementation for the injector to bind. The
                // *_Managed conveniences and the private, final bridges to BCL
                // interface members are default-implemented by the contract
                // itself, so they are recorded, not fatal.
                if (IsNativeSlot(contract, member))
                    throw new MissingMethodException($"{type.FullName} does not implement {contract.FullName}.{member}");
                defaultImplemented.Add($"{contract.Name}.{member.Name}");
            }
        }
    }

    /// <summary>
    /// A native slot is a public virtual newslot the generator backed with a
    /// NativeMethodInfoPtr field. Managed convenience accessors and the
    /// explicit bridges to the BCL interfaces have no such pointer and no
    /// native counterpart in the game.
    /// </summary>
    private static bool IsNativeSlot(Type contract, MethodInfo member)
    {
        if (member.IsStatic || !member.IsPublic || !member.IsVirtual || member.IsFinal ||
            (member.Attributes & MethodAttributes.NewSlot) == 0) return false;
        var prefix = "NativeMethodInfoPtr_" + member.Name + "_";
        return contract.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => field.Name.StartsWith(prefix, StringComparison.Ordinal));
    }
}
