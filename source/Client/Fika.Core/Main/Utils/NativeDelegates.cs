using Il2CppInterop.Runtime;

namespace Fika.Core;

internal static class NativeDelegates
{
    // Bind native methods directly so native code can later remove the same
    // target/method pair. A managed trampoline would have a different identity.
    public static T Bind<T>(Il2CppSystem.Object target, string method) where T : Il2CppSystem.Delegate =>
        Il2CppSystem.Delegate.CreateDelegate(Il2CppType.Of<T>(), target, method).Cast<T>();

    public static T Find<T>(Il2CppSystem.Delegate handlers, Il2CppSystem.Object target, string method)
        where T : Il2CppSystem.Delegate
    {
        if (handlers == null || target == null) return null;
        foreach (var handler in handlers.GetInvocationList())
        {
            if (handler.Target?.Pointer == target.Pointer && handler.Method?.Name == method)
                return handler.Cast<T>();
        }
        return null;
    }

    // 47888 exposes several events with MANAGED delegate types (System.Action<...>)
    // instead of Il2CppSystem delegates, so Il2CppSystem.Delegate.CreateDelegate cannot be used.
    // Managed delegates compare by target+method, so the same call site can also unsubscribe.
    public static T BindManaged<T>(Il2CppSystem.Object target, string method) where T : System.Delegate
    {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        var info = target.GetType().GetMethod(method, flags);
        if (info == null)
        {
            throw new System.MissingMethodException(target.GetType().FullName, method);
        }
        return (T)(object)System.Delegate.CreateDelegate(typeof(T), target, info);
    }

    /// <summary>
    /// <see cref="BindManaged{T}"/> that reports a failure instead of throwing.
    ///
    /// A CLR delegate whose parameter list carries a by-value struct (Nullable&lt;T&gt;, Vector3,
    /// DamageInfo, ValueStruct, a pair of small structs...) cannot always be marshalled across this
    /// boundary: the interop builds a per-signature conversion plan when the delegate is bound and
    /// aborts with an ArgumentException when an argument is register-sized. That shape already
    /// aborted a host raid through the matchmaker status delegate (Nullable&lt;float&gt;). An event
    /// subscription is never worth a raid, so callers disable that one feature and keep their
    /// pull-based path instead of propagating the exception.
    /// </summary>
    public static bool TryBindManaged<T>(Il2CppSystem.Object target, string method, string site, out T handler)
        where T : System.Delegate
    {
        try
        {
            handler = BindManaged<T>(target, method);
        }
        catch (System.Exception exception)
        {
            handler = null;
            Fika.Core.Main.Utils.FikaGlobals.LogError(site + ": could not bind " + method + " as " + typeof(T).Name
                + "; this subscription is disabled and the raid continues: " + exception);
            return false;
        }

        if (handler is null)
        {
            Fika.Core.Main.Utils.FikaGlobals.LogWarning(site + ": " + method + " bound to a null " + typeof(T).Name);
            return false;
        }

        return true;
    }
}
