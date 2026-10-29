using EFT;
using Fika.Core.Main.Utils;

namespace Fika.Core.Networking.Packets.Communication;

/// <summary>
/// Authoritative inventory snapshot sent after the server rejects an inventory
/// operation. The callback id associates it with the local operation.
/// </summary>
public struct InventoryResyncPacket : INetSerializable
{
    public int NetId;
    public ushort CallbackId;
    public InventoryDescriptor Descriptor;

    public void Deserialize(NetDataReader reader)
    {
        NetId = reader.GetInt();
        CallbackId = reader.GetUShort();
        Descriptor = reader.GetEFTInventoryDescriptor();
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(NetId);
        writer.Put(CallbackId);
        writer.PutEFTInventoryDescriptor(Descriptor);
    }
}
