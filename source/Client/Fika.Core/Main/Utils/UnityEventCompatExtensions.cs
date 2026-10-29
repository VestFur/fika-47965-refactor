using System;
using UnityEngine.Events;

namespace Fika.Core;

/// <summary>
/// Il2CppInterop generates UnityAction as a managed proxy type, so C# method
/// groups and lambdas cannot bind to UnityEvent.AddListener directly.  These
/// overloads keep call sites idiomatic while performing the native delegate
/// conversion at one boundary.
internal static class UnityEventCompatExtensions
{
    public static void AddListener(this UnityEvent evt, Action listener)
    {
        UnityAction action = listener;
        evt.AddListener(action);
    }

    /// <summary>
    /// Counterpart of <see cref="AddListener(UnityEvent, Action)"/>.
    ///
    /// The conversion is cached per managed delegate (Il2CppInterop keeps a
    /// ConditionalWeakTable keyed by the CLR delegate), so converting the *same* delegate
    /// instance again yields the same native listener and the removal actually finds it.
    /// Passing a fresh method group would create a second, different listener and leave the
    /// first one registered - which is how a native event ends up holding a delegate whose
    /// target the managed side has already dropped.
    /// </summary>
    public static void RemoveListener(this UnityEvent evt, Action listener)
    {
        UnityAction action = listener;
        evt.RemoveListener(action);
    }

    public static void AddListener<T>(this UnityEvent<T> evt, Action<T> listener)
    {
        UnityAction<T> action = listener;
        evt.AddListener(action);
    }

    public static void AddListener<T0, T1>(this UnityEvent<T0, T1> evt, Action<T0, T1> listener)
    {
        UnityAction<T0, T1> action = listener;
        evt.AddListener(action);
    }

    public static void AddListener<T0, T1, T2>(this UnityEvent<T0, T1, T2> evt, Action<T0, T1, T2> listener)
    {
        UnityAction<T0, T1, T2> action = listener;
        evt.AddListener(action);
    }

    public static void AddListener<T0, T1, T2, T3>(this UnityEvent<T0, T1, T2, T3> evt, Action<T0, T1, T2, T3> listener)
    {
        UnityAction<T0, T1, T2, T3> action = listener;
        evt.AddListener(action);
    }
}
