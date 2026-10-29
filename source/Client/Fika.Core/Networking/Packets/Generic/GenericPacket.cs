// © 2026 Lacyway All Rights Reserved

using Fika.Core.Networking.Pooling;

namespace Fika.Core.Networking.Packets.Generic;

/// <summary>
/// Packet used for many different things to reduce packet bloat
/// </summary>
public sealed class GenericPacket : INetReusable
{
    public int NetId;
    public EGenericSubPacketType Type;
    public IPoolSubPacket SubPacket;

    public void Execute()
    {
        SubPacket?.Execute();
    }

    public void Deserialize(NetDataReader reader)
    {
        NetId = reader.GetInt();
        Type = reader.GetEnum<EGenericSubPacketType>();
        SubPacket = GenericSubPacketPoolManager.Instance.GetPacket<IPoolSubPacket>(Type);
        // (2026-10-26 W22) Unknown sub-packet type (a peer on a different build): the pool reports it and returns
        // null. Skipping the body leaves this packet inert instead of throwing inside the network poll loop.
        if (SubPacket == null)
        {
            reader.SkipBytes(reader.AvailableBytes);
            Type = default;
            return;
        }

        SubPacket.Deserialize(reader);
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(NetId);
        writer.PutEnum(Type);
        SubPacket?.Serialize(writer);
    }

    public void Clear()
    {
        if (SubPacket != null)
        {
            GenericSubPacketPoolManager.Instance.ReturnPacket(Type, SubPacket);
            SubPacket = null;
            Type = default;
        }
    }

    public void Flush()
    {
        GenericSubPacketPoolManager.Instance.ReturnPacket(Type, SubPacket);
        SubPacket = null;
        Type = default;
    }
}
