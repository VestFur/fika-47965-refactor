using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace Fika.Core.Main.Utils;

internal static class NativeInjectedGc
{
    /// <summary>
    /// Enables native reference tracing for a class the injector just created.
    /// Returns true when the class had inherited a GC descriptor from its base
    /// class and that descriptor had to be dropped.
    /// </summary>
    internal static unsafe bool PreserveReferences(Type type)
    {
        var pointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        var klass = UnityVersionHandler.Wrap((Il2CppClass*)pointer);
        if (!IL2CPP.il2cpp_class_has_references((IntPtr)klass.Parent) && klass.FieldCount == 0) return false;

        // The installed injector zero-initializes the class metadata it
        // allocates and then copies the base class GC state into it (gc_desc
        // and has_references). Native allocation would follow the base class
        // bitmap, which only describes the base layout: the injector appends
        // this class's own fields after the base instance size, so their
        // reference slots are invisible to a typed GC pass. Resolve the layout
        // from the runtime's selected handler rather than hard-coding a class
        // offset for one Unity version.
        var layout = klass.GetType().GetProperty("_", BindingFlags.NonPublic | BindingFlags.Instance)?.PropertyType.GetElementType()
            ?? throw new NotSupportedException("Cannot resolve injected class GC layout.");
        var flags = layout.GetField("_bitfield0", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(layout.FullName, "_bitfield0");
        var mask = Convert.ToByte(Enum.Parse(flags.FieldType, "has_references"));
        var offset = Marshal.OffsetOf(layout, flags.Name).ToInt32();
        var descriptor = Marshal.OffsetOf(layout, "gc_desc").ToInt32();
        var droppedInheritedDescriptor = false;
        if (Marshal.ReadIntPtr(pointer, descriptor) != IntPtr.Zero)
        {
            // A descriptor present here is never this class's own: the injector
            // copied it from the base class in the same registration call.
            // Drop it rather than keeping it or installing a replacement.
            // With a null descriptor, has_references selects conservative
            // tracing, which covers inherited and injected fields alike, while
            // a bitmap sized for the base layout can only miss slots.
            Marshal.WriteIntPtr(pointer, descriptor, IntPtr.Zero);
            droppedInheritedDescriptor = true;
        }
        // A null descriptor plus has_references requests conservative tracing,
        // covering inherited and injected fields, including future assignments.
        Marshal.WriteByte(pointer, offset, (byte)(Marshal.ReadByte(pointer, offset) | mask));
        if (!IL2CPP.il2cpp_class_has_references(pointer))
            throw new InvalidOperationException($"Native reference tracing was not enabled for {type}.");
        return droppedInheritedDescriptor;
    }
}
