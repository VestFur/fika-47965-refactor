#if DEBUG
using Fika.Core.Main.Utils;
#endif
using System;
using System.Runtime.CompilerServices;

namespace Fika.Core.Networking.Pooling;

/// <summary>
/// Manages a pool of packet objects of type <typeparamref name="TType"/> categorized by keys of type <typeparamref name="TEnum"/>.
/// Provides functionality to create, clear, retrieve, and return packets from the pool.
/// </summary>
/// <typeparam name="TEnum">The enum type used as keys to identify packet types.</typeparam>
/// <typeparam name="TType">The packet type that implements <see cref="IDisposable"/>.</typeparam>
public abstract class BasePacketPoolManager<TEnum, TType>
    where TEnum : Enum
    where TType : class, IDisposable
{
    /// <summary>
    /// Array containing the constructing method for every packet type
    /// </summary>
    protected Func<TType>[] _subPacketFactories;

    private bool _poolExists;

    private PacketPool<TType>[] _pool;

    /// <summary>
    /// Creates a pool of packets for each packet type based on the registered factory functions.
    /// Initializes pools with an initial capacity of 1.
    /// </summary>
    public void CreatePool()
    {
        if (_pool == null)
        {
            _pool = new PacketPool<TType>[_subPacketFactories.Length];
            for (var i = 0; i < _subPacketFactories.Length; i++)
            {
                _pool[i] = new(1, _subPacketFactories[i]);
            }
        }

        _poolExists = true;
    }

    /// <summary>
    /// Clears and disposes all packet pools, releasing any resources held.
    /// </summary>
    public void ClearPool()
    {
        if (_pool != null)
        {
            for (var i = 0; i < _pool.Length; i++)
            {
                _pool[i].Dispose();
            }
        }

        _pool = null;
        _poolExists = false;
    }

    /// <summary>
    /// Retrieves a packet of the specified type from the pool.
    /// If the pool is not created yet, it will be initialized.
    /// </summary>
    /// <typeparam name="T">The concrete packet type to retrieve, which must derive from <typeparamref name="TType"/>.</typeparam>
    /// <param name="type">The key identifying the packet type in the pool.</param>
    /// <returns>A packet instance from the pool.</returns>
    /// <exception cref="ArgumentException">Thrown if the specified type does not exist in the pool.</exception>
    public T GetPacket<T>(TEnum type) where T : TType
    {
        if (!_poolExists)
        {
#if DEBUG
            FikaGlobals.LogError($"Pool did not exist when retrieving [{type.GetType().Name}]");
#endif
            CreatePool();
        }

        var pool = WithdrawPacket(type);
        if (pool == null)
        {
            // Unknown type for this build (see WithdrawPacket): the caller skips the packet.
            return default;
        }

        return (T)pool.Get();
    }

    /// <summary>
    /// Returns a packet to the pool and disposes it.
    /// </summary>
    /// <param name="type">The key identifying the packet type in the pool.</param>
    /// <param name="packet">The packet instance to return to the pool.</param>
    /// <remarks>Does nothing if the pool has not been created</remarks>
    public void ReturnPacket(TEnum type, TType packet)
    {
        if (!_poolExists)
        {
#if DEBUG
            FikaGlobals.LogError($"Pool did not exist when returning [{type.GetType().Name}] - [{packet.GetType().Name}]");
#endif
            return;
        }

        packet.Dispose();

        // (2026-10-26 W23) WithdrawPacket now returns default for a type this build does not know (W22); the old
        // one-liner would then have dereferenced null here. The packet is already disposed, so dropping it is the
        // whole work of "returning" an unknown one.
        var pool = WithdrawPacket(type);
        if (pool == null)
        {
            return;
        }

        pool.Return(packet);
    }

    /// <summary>
    /// Retrieves the <see cref="PacketPool{TType}"/> associated with the specified type.
    /// </summary>
    /// <param name="type">The key identifying the packet type.</param>
    /// <returns>The corresponding <see cref="PacketPool{TType}"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown if the specified type does not exist in the pool.</exception>
    private PacketPool<TType> WithdrawPacket(TEnum type)
    {
        // (2026-10-26 W22) A peer built from a different package can send a sub-packet type this build does not
        // know; the array index then threw IndexOutOfRange inside the network poll loop. Report it instead and
        // let the caller skip the packet - one unknown type must not take the whole net loop down.
        var index = ToInt(type);
        if ((System.UInt32)index >= (System.UInt32)_pool.Length)
        {
            global::Fika.Core.Main.Utils.FikaGlobals.LogError($"packet pool has no entry for {typeof(TEnum).Name}={index} " +
                $"(this build has {_pool.Length}); the peer runs a different build");
            return null;
        }
#if DEBUG
        return _pool[ToInt(type)]
            ?? throw new ArgumentException("Could not find given type in the packet pool manager!", nameof(type));
#else
        return _pool[ToInt(type)];
#endif

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ToInt(TEnum value)
    {
#if DEBUG
        if (Unsafe.SizeOf<TEnum>() != 1)
        {
            throw new InvalidOperationException($"{value.GetType().Name} must be backed by byte for this method.");
        }
#endif
        return Unsafe.As<TEnum, byte>(ref value);
    }
}