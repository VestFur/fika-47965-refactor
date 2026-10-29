using Il2CppInterop.Runtime.InteropTypes;
using System;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Log formatting for values that may be IL2CPP references.
/// <para>
/// Calling <c>ToString()</c> on an IL2CPP wrapper runs in native code
/// (<c>IL2CPP.Il2CppStringToManaged(pointer)</c>); on a wrapper whose native object is
/// gone that is an access violation which no managed catch can stop - it killed the
/// 47888 headless host once, in a log line that interpolated a pet id
/// (Fika.Headless FikaHeadlessPlugin.BeginFikaStartRaid, 2026-10-10).
/// </para>
/// Every member listed here is read from the managed proxy only: the native pointer,
/// or a null check. Nothing here ever dereferences the native object.
/// </summary>
internal static class LogSafe
{
    /// <summary>
    /// Describes an IL2CPP reference for a log line: its native pointer, or "null".
    /// The value of the native object is deliberately not read.
    /// </summary>
    public static string Ref(object value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is Il2CppObjectBase proxy)
        {
            return proxy.Pointer == IntPtr.Zero ? "null" : "0x" + proxy.Pointer.ToInt64().ToString("X");
        }

        return value.ToString();
    }

    /// <summary>
    /// Null-ness of an IL2CPP reference, for cases where the pointer alone says little.
    /// </summary>
    public static string IsNull(object value)
    {
        if (value is null)
        {
            return "true";
        }

        if (value is Il2CppObjectBase proxy)
        {
            return (proxy.Pointer == IntPtr.Zero).ToString();
        }

        return "false";
    }
}
