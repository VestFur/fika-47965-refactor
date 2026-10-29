using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Validation for il2cpp/Unity references before they are handed to native code.
///
/// A null managed reference is cheap to test, but a *bogus* one is not: Il2CppInterop wraps
/// whatever a serialized prefab field, a bundle asset or a stale object slot happens to
/// contain, and the first native member access then faults inside GameAssembly
/// (0xC0000005 reading 0xA through "mov rax,[rcx]" in the 47888 crash dumps) instead of
/// throwing a managed exception.
///
/// The previous version of this class only checked that the value *looks* like a pointer
/// (small and misaligned values were rejected). The 22:15 run proved that is not enough:
/// the value that reaches the fault is 0xA, but a field can also hold a perfectly aligned
/// pointer to memory that is not an il2cpp object at all, and then the write corrupts
/// whatever lives there.
///
/// Every check here therefore asks the operating system first ("is this address committed
/// and readable?") and only then reads the object header, the class pointer and the class
/// name. Nothing is dereferenced before VirtualQuery answered for the page it lives in, so
/// a bogus reference is rejected without ever being dereferenced.
/// </summary>
internal static class NativeRefGuard
{
    // Real native objects live far above this and are always pointer aligned.
    private const long MinimumNativePointer = 0x10000;
    private const long AlignmentMask = 0x7;

    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const uint ReadableProtections = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    // Il2CppClass layout used by this runtime: image, gc_desc, name, namespaze, ... parent.
    private const int KlassNameOffset = 0x10;
    private const int KlassNamespaceOffset = 0x18;
    private const int KlassParentOffset = 0x48;
    private const int MaxTypeNameLength = 128;
    private const int MaxHierarchyDepth = 10;

    /// <summary>
    /// Set by <see cref="VerifyTypeProbe"/>. False means the class-name layout could not be
    /// confirmed on a reference whose type is known, so references are only required to be
    /// readable - a broken probe must not disable working UI.
    /// </summary>
    private static bool _typeProbeTrusted;

    /// <summary>Set by <see cref="VerifyTypeProbe"/>. False means the parent chain is unusable.</summary>
    private static bool _hierarchyTrusted;

    private static bool _fallbackReported;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, IntPtr length);

    /// <summary>True when the pointer can be a real native object (cheap plausibility test).</summary>
    internal static bool IsUsable(IntPtr pointer)
    {
        long value = pointer.ToInt64();
        return value >= MinimumNativePointer && (value & AlignmentMask) == 0;
    }

    /// <summary>True when the wrapper exists and its pointer is plausible.</summary>
    internal static bool IsUsable(Il2CppObjectBase instance)
    {
        if (instance is null)
        {
            return false;
        }

        try
        {
            return IsUsable(instance.Pointer);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the whole [pointer, pointer+length) range is committed and readable.
    /// This is answered by the memory manager and never dereferences the address itself.
    /// </summary>
    internal static bool IsProvablyReadable(IntPtr pointer, int length)
    {
        long start = pointer.ToInt64();
        if (start < MinimumNativePointer || length <= 0)
        {
            return false;
        }

        return IsReadableAddress(start) && IsReadableAddress(start + length - 1);
    }

    private static bool IsReadableAddress(long address)
    {
        try
        {
            if (VirtualQuery(new IntPtr(address), out var information,
                    new IntPtr(Marshal.SizeOf<MemoryBasicInformation>())) == IntPtr.Zero)
            {
                return false;
            }

            if (information.State != MemCommit)
            {
                return false;
            }

            if ((information.Protect & (PageNoAccess | PageGuard)) != 0)
            {
                return false;
            }

            return (information.Protect & ReadableProtections) != 0;
        }
        catch (Exception)
        {
            // No kernel32 (or a hostile loader): the caller keeps the plausibility result.
            return false;
        }
    }

    /// <summary>
    /// Reads the il2cpp class name of an object. Every hop is proven readable first, so this
    /// can be called on a reference whose validity is still unknown.
    /// </summary>
    internal static string TryReadTypeName(IntPtr instance)
    {
        IntPtr klass = TryReadPointer(instance);
        if (klass == IntPtr.Zero || !IsProvablyReadable(klass, KlassNamespaceOffset + 8))
        {
            return null;
        }

        string name = TryReadTypeNameOfKlass(klass);
        if (name is null)
        {
            return null;
        }

        IntPtr namespacePointer = TryReadPointer(klass + KlassNamespaceOffset);
        string namespaze = namespacePointer == IntPtr.Zero ? null : TryReadCString(namespacePointer);

        if (string.IsNullOrEmpty(namespaze) || !IsTypeNameLike(namespaze))
        {
            return name;
        }

        return namespaze + "." + name;
    }

    private static string TryReadTypeNameOfKlass(IntPtr klass)
    {
        IntPtr namePointer = TryReadPointer(klass + KlassNameOffset);
        if (namePointer == IntPtr.Zero)
        {
            return null;
        }

        string name = TryReadCString(namePointer);
        return name is not null && IsTypeNameLike(name) ? name : null;
    }

    /// <summary>
    /// Walks the class hierarchy of an object and looks for a class name. Returns true when
    /// the name matched, false when the hierarchy was readable but did not contain it, and
    /// null (in <paramref name="hierarchyReadable"/>) when it could not be walked safely -
    /// the caller decides what to do with that.
    /// </summary>
    internal static bool TypeNameIsOrDerivesFrom(IntPtr instance, string expectedTypeName,
        out string actualName, out bool hierarchyReadable)
    {
        actualName = TryReadTypeName(instance);
        hierarchyReadable = false;

        IntPtr klass = TryReadPointer(instance);
        if (klass == IntPtr.Zero || !IsProvablyReadable(klass, KlassParentOffset + 8))
        {
            return false;
        }

        hierarchyReadable = true;
        var chain = new StringBuilder();
        for (int depth = 0; depth < MaxHierarchyDepth && klass != IntPtr.Zero; depth++)
        {
            string name = TryReadTypeNameOfKlass(klass);
            if (name is null)
            {
                // The chain broke at this level; what was read so far is still evidence.
                hierarchyReadable = depth > 0;
                break;
            }

            if (chain.Length > 0)
            {
                chain.Append(" -> ");
            }

            chain.Append(name);
            if (string.Equals(name, expectedTypeName, StringComparison.Ordinal))
            {
                actualName = chain.ToString();
                return true;
            }

            IntPtr parent = TryReadPointer(klass + KlassParentOffset);
            if (parent == klass || (parent != IntPtr.Zero && !IsProvablyReadable(parent, KlassParentOffset + 8)))
            {
                break;
            }

            klass = parent;
        }

        if (actualName is null)
        {
            actualName = chain.ToString();
        }

        return false;
    }

    /// <summary>
    /// True when the reference is a real, readable il2cpp object. When
    /// <paramref name="expectedTypeName"/> is given and the class hierarchy can be read, the
    /// object also has to be of that type (or derive from it).
    /// </summary>
    internal static bool IsProvablyUsable(Il2CppObjectBase instance, string expectedTypeName, out string detail)
    {
        detail = "null";
        if (instance is null)
        {
            return false;
        }

        IntPtr pointer;
        try
        {
            pointer = instance.Pointer;
        }
        catch (Exception exception)
        {
            detail = "unreadable wrapper (" + exception.GetType().Name + ")";
            return false;
        }

        if (!IsUsable(pointer))
        {
            detail = Describe(pointer) + " is not a native pointer";
            return false;
        }

        if (!IsProvablyReadable(pointer, 8))
        {
            detail = Describe(pointer) + " is not in a committed readable page";
            return false;
        }

        string typeName = TryReadTypeName(pointer);
        if (typeName is null)
        {
            // The page is readable but there is no class name where an il2cpp object must
            // have one. Treat it as unusable unless the probe itself is unproven.
            if (!_typeProbeTrusted)
            {
                ReportFallbackOnce();
                detail = Describe(pointer) + " readable, class name unreadable (type probe unproven)";
                return true;
            }

            detail = Describe(pointer) + " readable but carries no il2cpp class name";
            return false;
        }

        detail = Describe(pointer) + " (" + typeName + ")";

        if (string.IsNullOrEmpty(expectedTypeName) || !_typeProbeTrusted)
        {
            return true;
        }

        if (TypeNameIsOrDerivesFrom(pointer, expectedTypeName, out string chain, out bool hierarchyReadable))
        {
            detail = Describe(pointer) + " (" + chain + ")";
            return true;
        }

        if (!hierarchyReadable)
        {
            // The class name was readable but the parent chain was not: the name alone is
            // the best evidence available, so the object is accepted.
            detail = Describe(pointer) + " (" + (chain ?? typeName) + ", hierarchy unreadable)";
            return true;
        }

        detail = Describe(pointer) + " is " + chain + ", expected " + expectedTypeName;
        return false;
    }

    /// <summary>
    /// Confirms that the Il2CppClass layout this class uses matches the running runtime by
    /// reading a reference whose type is already known. Must be called once, before the
    /// stricter checks are relied upon.
    /// </summary>
    internal static bool VerifyTypeProbe(Il2CppObjectBase knownReference, string knownTypeName)
    {
        try
        {
            string observed = knownReference is null ? null : TryReadTypeName(knownReference.Pointer);
            _typeProbeTrusted = observed is not null &&
                observed.IndexOf(knownTypeName, StringComparison.Ordinal) >= 0;

            if (_typeProbeTrusted)
            {
                _hierarchyTrusted = TypeNameIsOrDerivesFrom(knownReference.Pointer, knownTypeName,
                    out string chain, out bool hierarchyReadable) && hierarchyReadable;
                FikaGlobals.LogInfo(
                    $"[NativeRefGuard] type probe confirmed on {knownTypeName}: {observed}; hierarchy '{chain}' (readable={hierarchyReadable})");
            }
            else
            {
                _hierarchyTrusted = false;
                FikaGlobals.LogWarning(
                    $"[NativeRefGuard] type probe could not be confirmed (expected a name containing '{knownTypeName}', read '{observed ?? "null"}'); references are only checked for readability");
            }

            return _typeProbeTrusted;
        }
        catch (Exception exception)
        {
            _typeProbeTrusted = false;
            _hierarchyTrusted = false;
            FikaGlobals.LogWarning($"[NativeRefGuard] type probe failed: {exception.Message}");
            return false;
        }
    }

    internal static bool TypeProbeTrusted
    {
        get
        {
            return _typeProbeTrusted;
        }
    }

    internal static bool HierarchyTrusted
    {
        get
        {
            return _hierarchyTrusted;
        }
    }

    private static void ReportFallbackOnce()
    {
        if (_fallbackReported)
        {
            return;
        }

        _fallbackReported = true;
        FikaGlobals.LogWarning("[NativeRefGuard] the il2cpp class name could not be read; references are only required to be readable pages");
    }

    private static IntPtr TryReadPointer(IntPtr address)
    {
        if (!IsProvablyReadable(address, 8))
        {
            return IntPtr.Zero;
        }

        try
        {
            byte[] buffer = new byte[8];
            Marshal.Copy(address, buffer, 0, 8);
            return new IntPtr(BitConverter.ToInt64(buffer, 0));
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static string TryReadCString(IntPtr address)
    {
        if (!IsProvablyReadable(address, 1))
        {
            return null;
        }

        var builder = new StringBuilder(32);
        for (int offset = 0; offset < MaxTypeNameLength; offset += 8)
        {
            if (!IsProvablyReadable(address + offset, 8))
            {
                return null;
            }

            try
            {
                byte[] buffer = new byte[8];
                Marshal.Copy(address + offset, buffer, 0, 8);
                for (int index = 0; index < 8; index++)
                {
                    if (buffer[index] == 0)
                    {
                        return builder.Length == 0 ? null : builder.ToString();
                    }

                    builder.Append((char)buffer[index]);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        return builder.ToString();
    }

    private static bool IsTypeNameLike(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        char first = name[0];
        if (!char.IsLetter(first) && first != '_')
        {
            return false;
        }

        foreach (char character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                continue;
            }

            switch (character)
            {
                case '_':
                case '.':
                case '`':
                case '<':
                case '>':
                case '[':
                case ']':
                case '+':
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>Short, log friendly description of a reference, safe to call on any value.</summary>
    internal static string Describe(Il2CppObjectBase instance)
    {
        if (instance is null)
        {
            return "null";
        }

        try
        {
            return Describe(instance.Pointer);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>Short, log friendly description of a raw pointer value.</summary>
    internal static string Describe(IntPtr pointer)
    {
        long value = pointer.ToInt64();
        if (value == 0)
        {
            return "0x0";
        }

        if (!IsUsable(pointer))
        {
            return string.Format(CultureInfo.InvariantCulture, "0x{0:X} (not a pointer)", value);
        }

        string typeName = TryReadTypeName(pointer);
        return typeName is null
            ? string.Format(CultureInfo.InvariantCulture, "0x{0:X}", value)
            : string.Format(CultureInfo.InvariantCulture, "0x{0:X} ({1})", value, typeName);
    }
}
