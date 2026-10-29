using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Minimal IL2CPP reflection for calls whose signatures mention a type that can
/// be shadowed by a managed assembly.
/// <para>
/// A managed <c>Newtonsoft.Json.dll</c> in BepInEx/plugins wins name resolution
/// over the IL2CPP interop proxy, so managed metadata can no longer express
/// signatures such as <c>ToUnparsedData(Il2CppReferenceArray&lt;JsonConverter&gt;)</c>
/// or a <c>JToken</c> return value. Resolving the classes through IL2CPP by name
/// and invoking them with raw pointers sidesteps managed type resolution
/// entirely.
/// </para>
/// </summary>
internal static unsafe class NativeIl2Cpp
{
    internal static IntPtr OpenAssembly(string name)
    {
        var native = Marshal.StringToHGlobalAnsi(name);
        try
        {
            return IL2CPP.il2cpp_domain_assembly_open(IL2CPP.il2cpp_domain_get(), native);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    internal static IntPtr FindClass(string assemblyName, string @namespace, string name)
    {
        var assembly = OpenAssembly(assemblyName);
        if (assembly == IntPtr.Zero) return IntPtr.Zero;
        var image = IL2CPP.il2cpp_assembly_get_image(assembly);
        return image == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_from_name(image, @namespace, name);
    }

    internal static IntPtr FindMethod(IntPtr type, string name, int parameterCount, string firstParameterType = null)
    {
        // Inherited methods are not part of the derived class's own method list,
        // so the whole hierarchy has to be walked. Without this a runtime type
        // that inherits the target (for example the backend session used from the
        // menu) yields "could not resolve ... through IL2CPP" and the caller's
        // managed fallback then fails against the shadowing Newtonsoft.Json.
        var owner = type;
        while (owner != IntPtr.Zero)
        {
            var iterator = IntPtr.Zero;
            while (true)
            {
                var method = IL2CPP.il2cpp_class_get_methods(owner, ref iterator);
                if (method == IntPtr.Zero) break;
                if (IL2CPP.il2cpp_method_get_name_(method) != name) continue;
                if (IL2CPP.il2cpp_method_get_param_count(method) != (uint)parameterCount) continue;
                if (firstParameterType != null && parameterCount > 0 &&
                    ParameterTypeName(IL2CPP.il2cpp_method_get_param(method, 0)) != firstParameterType)
                {
                    continue;
                }

                return method;
            }

            owner = IL2CPP.il2cpp_class_get_parent(owner);
        }

        return IntPtr.Zero;
    }

    internal static string ParameterTypeName(IntPtr parameter)
    {
        if (parameter == IntPtr.Zero) return null;
        var handle = IL2CPP.il2cpp_type_get_object(parameter);
        return handle == IntPtr.Zero ? null : new Il2CppSystem.Type(handle).FullName;
    }

    internal static IntPtr FindGetter(IntPtr type, string propertyName, IntPtr fallbackType = default)
    {
        var native = Marshal.StringToHGlobalAnsi(propertyName);
        try
        {
            var owner = type;
            while (owner != IntPtr.Zero)
            {
                var property = IL2CPP.il2cpp_class_get_property_from_name(owner, native);
                if (property != IntPtr.Zero)
                {
                    var getter = IL2CPP.il2cpp_property_get_get_method(property);
                    if (getter != IntPtr.Zero) return getter;
                }

                // The name-based lookup misses inherited properties on some
                // runtimes, so walk the property list as well.
                var iterator = IntPtr.Zero;
                while (true)
                {
                    var candidate = IL2CPP.il2cpp_class_get_properties(owner, ref iterator);
                    if (candidate == IntPtr.Zero) break;
                    if (IL2CPP.il2cpp_property_get_name_(candidate) != propertyName) continue;
                    var getter = IL2CPP.il2cpp_property_get_get_method(candidate);
                    if (getter != IntPtr.Zero) return getter;
                }

                owner = IL2CPP.il2cpp_class_get_parent(owner);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }

        return fallbackType == IntPtr.Zero ? IntPtr.Zero : FindGetter(fallbackType, propertyName);
    }

    /// <summary>
    /// Finds a method by name across every class in an assembly. Extension
    /// methods are compiled into static helper classes whose names are not part
    /// of the call site, so the declaring type cannot be named up front.
    /// </summary>
    internal static IntPtr FindMethodInAssembly(string assemblyName, string methodName, int parameterCount,
        out string declaringType, string firstParameterType = null)
    {
        declaringType = null;
        var assembly = OpenAssembly(assemblyName);
        if (assembly == IntPtr.Zero) return IntPtr.Zero;
        var image = IL2CPP.il2cpp_assembly_get_image(assembly);
        if (image == IntPtr.Zero) return IntPtr.Zero;

        // The reported type name of a parameter can be unavailable, so an exact
        // name match is a preference, not a requirement: a lone method with the
        // requested name and parameter count is unambiguous on its own.
        var fallback = IntPtr.Zero;
        var fallbackType = (string)null;
        var matches = 0;

        var classCount = IL2CPP.il2cpp_image_get_class_count(image);
        for (var index = 0u; index < classCount; index++)
        {
            var type = IL2CPP.il2cpp_image_get_class(image, index);
            if (type == IntPtr.Zero) continue;

            var iterator = IntPtr.Zero;
            while (true)
            {
                var method = IL2CPP.il2cpp_class_get_methods(type, ref iterator);
                if (method == IntPtr.Zero) break;
                if (IL2CPP.il2cpp_method_get_name_(method) != methodName) continue;
                if (IL2CPP.il2cpp_method_get_param_count(method) != (uint)parameterCount) continue;

                matches++;
                var @namespace = IL2CPP.il2cpp_class_get_namespace_(type);
                var name = IL2CPP.il2cpp_class_get_name_(type);
                var owner = string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";

                if (firstParameterType != null && parameterCount > 0)
                {
                    var actual = ParameterTypeName(IL2CPP.il2cpp_method_get_param(method, 0));
                    if (actual != null && actual.Contains(firstParameterType))
                    {
                        declaringType = owner;
                        return method;
                    }
                }

                if (fallback != IntPtr.Zero) continue;
                fallback = method;
                fallbackType = owner;
            }
        }

        if (matches != 1) return IntPtr.Zero;
        declaringType = fallbackType;
        return fallback;
    }

    /// <summary>Describes every method with this name, for diagnosing a failed lookup.</summary>
    internal static string Describe(string assemblyName, string methodName)
    {
        var assembly = OpenAssembly(assemblyName);
        if (assembly == IntPtr.Zero) return $"{assemblyName} was not found";
        var image = IL2CPP.il2cpp_assembly_get_image(assembly);
        if (image == IntPtr.Zero) return $"{assemblyName} has no image";

        var builder = new System.Text.StringBuilder();
        var classCount = IL2CPP.il2cpp_image_get_class_count(image);
        for (var index = 0u; index < classCount; index++)
        {
            var type = IL2CPP.il2cpp_image_get_class(image, index);
            if (type == IntPtr.Zero) continue;

            var iterator = IntPtr.Zero;
            while (true)
            {
                var method = IL2CPP.il2cpp_class_get_methods(type, ref iterator);
                if (method == IntPtr.Zero) break;
                if (IL2CPP.il2cpp_method_get_name_(method) != methodName) continue;

                var @namespace = IL2CPP.il2cpp_class_get_namespace_(type);
                var name = IL2CPP.il2cpp_class_get_name_(type);
                builder.Append(string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}").Append('(');
                var parameters = IL2CPP.il2cpp_method_get_param_count(method);
                for (var parameter = 0u; parameter < parameters; parameter++)
                {
                    if (parameter > 0) builder.Append(", ");
                    builder.Append(ParameterTypeName(IL2CPP.il2cpp_method_get_param(method, parameter)) ?? "?");
                }

                builder.Append(") ");
            }
        }

        return builder.Length == 0 ? "no method named " + methodName : builder.ToString();
    }

    /// <summary>Reads a member that may be either a property or a field.</summary>
    internal static IntPtr ReadMember(IntPtr instance, string memberName)
    {
        if (instance == IntPtr.Zero) return IntPtr.Zero;
        var type = IL2CPP.il2cpp_object_get_class(instance);

        var getter = FindGetter(type, memberName);
        if (getter != IntPtr.Zero)
        {
            var exception = IntPtr.Zero;
            return IL2CPP.il2cpp_runtime_invoke(getter, instance, null, ref exception);
        }

        var field = IL2CPP.il2cpp_class_get_field_from_name(type, memberName);
        return field == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_field_get_value_object(field, instance);
    }

    /// <summary>Lists the members of an object's class, for diagnosing a failed lookup.</summary>
    internal static string DescribeMembers(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return "no instance";
        var type = IL2CPP.il2cpp_object_get_class(instance);
        var builder = new System.Text.StringBuilder(IL2CPP.il2cpp_class_get_name_(type)).Append(':');

        var iterator = IntPtr.Zero;
        while (true)
        {
            var field = IL2CPP.il2cpp_class_get_fields(type, ref iterator);
            if (field == IntPtr.Zero) break;
            builder.Append(" f:").Append(IL2CPP.il2cpp_field_get_name_(field));
        }

        iterator = IntPtr.Zero;
        while (true)
        {
            var property = IL2CPP.il2cpp_class_get_properties(type, ref iterator);
            if (property == IntPtr.Zero) break;
            builder.Append(" p:").Append(IL2CPP.il2cpp_property_get_name_(property));
        }

        return builder.ToString();
    }

    internal static IntPtr NewArray(IntPtr elementType, ulong length) =>
        elementType == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_array_new(elementType, length);

    internal static IntPtr Invoke(IntPtr method, IntPtr instance, params IntPtr[] arguments)
    {
        if (method == IntPtr.Zero)
        {
            throw new InvalidOperationException("The IL2CPP method could not be resolved.");
        }

        var buffer = stackalloc void*[arguments.Length == 0 ? 1 : arguments.Length];
        for (var index = 0; index < arguments.Length; index++) buffer[index] = (void*)arguments[index];

        var exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(method, instance, buffer, ref exception);
        if (exception != IntPtr.Zero)
        {
            var exceptionType = IL2CPP.il2cpp_class_get_name_(IL2CPP.il2cpp_object_get_class(exception));
            var message = new Il2CppSystem.Exception(exception).Message;
            throw new InvalidOperationException($"IL2CPP invocation failed with {exceptionType}: {message}");
        }

        return result;
    }

    internal static string ToManagedString(IntPtr value) =>
        value == IntPtr.Zero ? null : IL2CPP.Il2CppStringToManaged(value);
}
