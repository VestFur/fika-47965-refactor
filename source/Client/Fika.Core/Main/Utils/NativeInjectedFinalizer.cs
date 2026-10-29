using System;
using System.Collections.Concurrent;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;

namespace Fika.Core.Main.Utils;

/// <summary>Native allocation without a constructor owns no CLR handle to free.</summary>
internal static class NativeInjectedFinalizer
{
    private static readonly ConcurrentDictionary<IntPtr, byte> FikaClasses = new();
    private static Harmony _harmony;
    internal static int UnboundFinalizations;

    internal static void Register(Type type)
    {
        // Limit this runtime compatibility fix to our own registered classes.
        FikaClasses.TryAdd(Il2CppClassPointerStore.GetNativeClassPointer(type), 0);
        if (_harmony != null) return;
        _harmony = new Harmony("com.fika.core.unbound-finalizer");
        _harmony.Patch(AccessTools.DeclaredMethod(typeof(ClassInjector), nameof(ClassInjector.Finalize), new[] { typeof(IntPtr) }),
            prefix: new HarmonyMethod(typeof(NativeInjectedFinalizer), nameof(HasHandleToRelease)));
    }

    private static bool HasHandleToRelease(IntPtr ptr)
    {
        if (!FikaClasses.ContainsKey(IL2CPP.il2cpp_object_get_class(ptr)) ||
            ClassInjectorBase.GetGcHandlePtrFromIl2CppObject(ptr) != IntPtr.Zero) return true;
        // Such an object never ran ProcessNewObject/DerivedConstructorBody.
        // There is no managed state to dispose, unlike a normally bound instance.
        Interlocked.Increment(ref UnboundFinalizations);
        return false;
    }
}
