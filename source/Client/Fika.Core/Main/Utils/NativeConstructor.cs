using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime;
using System;

namespace Fika.Core.Main.Utils;

internal static class NativeConstructor
{
    internal static unsafe void InvokeBase<T>(Il2CppObjectBase instance, params object[] arguments) where T : Il2CppObjectBase
    {
        // The installed injector pins CLR value arguments. CLR Boolean cannot
        // be pinned as blittable data; a native Boolean keeps its exact type and
        // one-byte ABI, and the injector can safely unbox its native storage.
        for (var i = 0; i < arguments.Length; i++)
            if (arguments[i] is bool value)
                arguments[i] = new Il2CppSystem.Object(IL2CPP.il2cpp_value_box(
                    Il2CppClassPointerStore<bool>.NativeClassPtr, (IntPtr)(&value)));
        ClassInjector.InvokeBaseConstructor<T>(instance, arguments);
        GC.KeepAlive(arguments);
    }
}
