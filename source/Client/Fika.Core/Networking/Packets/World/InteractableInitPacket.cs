using System.Collections.Generic;
using Fika.Core.Networking.Serialization;
using ComponentAce.Compression.Libs.zlib;

namespace Fika.Core.Networking.Packets.World;

public struct InteractableInitPacket(bool isRequest) : INetSerializable
{
    public bool IsRequest = isRequest;
    public byte[] RawData;
    public Dictionary<string, int> Interactables;

    public static InteractableInitPacket FromInteractables(
        Il2CppSystem.Collections.Generic.IReadOnlyDictionary<string, int> interactables)
    {
        System.ArgumentNullException.ThrowIfNull(interactables);
        // World exposes a native interface proxy, not a CLR Dictionary. Snapshot
        // its full ID mapping before serializing it with the managed JSON codec.
        var snapshot = new Dictionary<string, int>(interactables.Count);
        foreach (var entry in interactables)
            snapshot.Add(entry.Key, entry.Value);
        return new(false) { Interactables = snapshot };
    }

    public void Deserialize(NetDataReader reader)
    {
        IsRequest = reader.GetBool();
        if (!IsRequest)
        {
            RawData = reader.GetByteArray();
            Interactables = FikaJson.Deserialize<Dictionary<string, int>>(SimpleZlib.Decompress(RawData, null));
        }
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(IsRequest);
        if (!IsRequest)
        {
            var data = SimpleZlib.CompressToBytes(FikaJson.Serialize(Interactables), 6);
            writer.PutByteArray(data);
        }
    }
}
