using System;
using System.Collections.Generic;
using Fika.Core.Modding.Events;

namespace Fika.Core.Modding;

/// <summary>
/// Provides a static event dispatcher for Fika events, allowing subscription and dispatching of events.
/// </summary>
public static class FikaEventDispatcher
{
    /// <summary>
    /// Represents a handler for Fika events.
    /// </summary>
    /// <param name="e">The event instance.</param>
    public delegate void FikaEventHandler(FikaEvent e);

    /// <summary>
    /// Occurs when any Fika event is dispatched.
    /// </summary>
    /// <remarks>Consumers are encouraged to subscribe to individual events instead.</remarks>
    public static event FikaEventHandler OnFikaEvent;

    /// <summary>
    /// Maps a composite key of (Callback, EventType) to the generated wrapper delegate for unsubscription matching.
    /// </summary>
    private static readonly Dictionary<(Delegate Callback, Type EventType), FikaEventHandler> _delegateMap = [];

    /// <summary>
    /// Dispatches a Fika event to all registered handlers.
    /// </summary>
    /// <typeparam name="TEvent">The type of the event, derived from <see cref="FikaEvent"/>.</typeparam>
    /// <param name="e">The event instance to dispatch.</param>
    public static void DispatchEvent<TEvent>(TEvent e) where TEvent : FikaEvent
    {
        OnFikaEvent?.Invoke(e);
    }

    /// <summary>
    /// Subscribes a callback to a specific type of Fika event.
    /// </summary>
    /// <typeparam name="TEvent">The type of the event to subscribe to.</typeparam>
    /// <param name="callback">The callback to invoke when the event is dispatched.</param>
    public static void SubscribeEvent<TEvent>(Action<TEvent> callback) where TEvent : FikaEvent
    {
        if (callback == null)
        {
            return;
        }

        void wrapper(FikaEvent e)
        {
            if (e is TEvent specificEvent)
            {
                callback(specificEvent);
            }
        }

        _delegateMap[(callback, typeof(TEvent))] = wrapper;
        OnFikaEvent += wrapper;
    }

    /// <summary>
    /// Unsubscribes a callback from a specific type of Fika event.
    /// </summary>
    /// <typeparam name="TEvent">The type of the event to unsubscribe from.</typeparam>
    /// <param name="callback">The callback to remove from the event subscription.</param>
    public static void UnsubscribeEvent<TEvent>(Action<TEvent> callback) where TEvent : FikaEvent
    {
        if (callback == null)
        {
            return;
        }

        var key = (callback, typeof(TEvent));
        if (_delegateMap.TryGetValue(key, out var wrapper))
        {
            OnFikaEvent -= wrapper;
            _delegateMap.Remove(key);
        }
    }
}