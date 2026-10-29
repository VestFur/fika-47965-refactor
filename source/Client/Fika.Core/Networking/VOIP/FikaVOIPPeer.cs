using System;
using Comfort.Common;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Fika.Core.Networking.VOIP;

public readonly struct FikaVOIPPeer(IPeer connection) : IEquatable<FikaVOIPPeer>
{
    public readonly IPeer Peer = connection;

    public override int GetHashCode()
    {
        if (Peer is RemotePeer remote)
            return remote.Peer?.GetHashCode() ?? 0;
        return Peer?.GetHashCode() ?? 0;
    }

    public override string ToString()
    {
        return Peer?.ToString() ?? "<null>";
    }

    public override bool Equals(object obj)
    {
        return obj != null && obj is FikaVOIPPeer peer && Equals(peer);
    }

    public bool Equals(FikaVOIPPeer other)
    {
        if (Peer == null)
        {
            return other.Peer == null;
        }

        if (Peer is RemotePeer localRemote && other.Peer is RemotePeer otherRemote)
        {
            return Equals(localRemote.Peer, otherRemote.Peer);
        }

        return Peer.Equals(other.Peer);
    }
}

public interface IPeer
{
    public bool IsLocal { get; set; }
    void SendData(Il2CppSystem.ArraySegment<byte> data, DeliveryMethod deliveryMethod);
}

public class LocalPeer : IPeer
{
    public bool IsLocal { get; set; } = true;

    public void SendData(Il2CppSystem.ArraySegment<byte> data, DeliveryMethod deliveryMethod)
    {
        FikaSingleton<FikaServer>.Instance?.VOIPClient?.NetworkReceivedPacket(data);
    }
}

public struct RemotePeer(NetPeer peer) : IPeer
{
    public bool IsLocal { get; set; }

    public readonly NetPeer Peer { get; } = peer;

    public readonly void SendData(Il2CppSystem.ArraySegment<byte> data, DeliveryMethod deliveryMethod)
    {
        FikaSingleton<IFikaNetworkManager>.Instance.SendVOIPData(data.ToManaged(), deliveryMethod, Peer);
    }
}
