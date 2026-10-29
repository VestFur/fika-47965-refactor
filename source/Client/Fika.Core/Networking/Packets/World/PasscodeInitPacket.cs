using System.Collections.Generic;

namespace Fika.Core.Networking.Packets.World;

public struct PasscodeInitPacket(bool isRequest) : INetSerializable
{
    public bool IsRequest = isRequest;
    public Dictionary<string, string[]> Hints;

    /// <summary>
    /// BO (analysis/icebreaker-keypad-2): the passcode ids of the host's location (id -> list id), or null when
    /// the host's own controller was not up yet. Both ends of this port send and read the same shape; the field is
    /// optional on the wire so a peer of an older build still understands the packet (and vice versa).
    /// </summary>
    public Dictionary<string, string> Entries;

    /// <summary>
    /// BU (analysis/icebreaker-keypad-3): the code table of this raid (passcode id -> code), drawn on the host
    /// by the game's own generator. It is the one set both machines check against, so a client can run the
    /// game's own local validation instead of asking the host about every attempt. Null while the host has not
    /// drawn its codes yet; the field is an optional tail block, so a peer of an older build keeps understanding
    /// the packet (and vice versa).
    /// </summary>
    public Dictionary<string, string> Codes;

    public void Deserialize(NetDataReader reader)
    {
        IsRequest = reader.GetBool();
        if (IsRequest)
        {
            return;
        }

        var amount = reader.GetUShort();
        Hints = new(amount);
        for (var i = 0; i < amount; i++)
        {
            var passcodeId = reader.GetString();
            var hintAmount = reader.GetUShort();
            var hints = new string[hintAmount];
            for (var j = 0; j < hintAmount; j++)
            {
                hints[j] = reader.GetString();
            }

            Hints[passcodeId] = hints;
        }

        // The optional passcode-id block is read only when the sender wrote one. LiteNetLib appends nothing of
        // its own here, so the writer's own trailing flag is the test.
        if (reader.AvailableBytes <= 0)
        {
            return;
        }

        if (!reader.GetBool())
        {
            return;
        }

        var entryAmount = reader.GetUShort();
        Entries = new(entryAmount);
        for (var i = 0; i < entryAmount; i++)
        {
            var passcodeId = reader.GetString();
            Entries[passcodeId] = reader.GetString();
        }

        // BU (analysis/icebreaker-keypad-3): the optional code table of this snapshot, read under the same
        // rule as the block above - only when the sender really wrote one.
        if (reader.AvailableBytes <= 0)
        {
            return;
        }

        if (!reader.GetBool())
        {
            return;
        }

        var codeAmount = reader.GetUShort();
        Codes = new(codeAmount);
        for (var i = 0; i < codeAmount; i++)
        {
            var passcodeId = reader.GetString();
            Codes[passcodeId] = reader.GetString();
        }
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(IsRequest);
        if (IsRequest)
        {
            return;
        }

        writer.Put((ushort)Hints.Count);
        foreach ((var passcodeId, var hints) in Hints)
        {
            writer.Put(passcodeId);
            writer.Put((ushort)hints.Length);
            for (var i = 0; i < hints.Length; i++)
            {
                writer.Put(hints[i]);
            }
        }

        // BO (analysis/icebreaker-keypad-2): the optional passcode-id block of this snapshot.
        writer.Put(Entries != null);
        if (Entries != null)
        {
            writer.Put((ushort)Entries.Count);
            foreach ((var passcodeId, var listId) in Entries)
            {
                writer.Put(passcodeId);
                writer.Put(listId ?? string.Empty);
            }
        }

        // BU (analysis/icebreaker-keypad-3): the optional code table of this snapshot (the raid's codes).
        writer.Put(Codes != null);
        if (Codes == null)
        {
            return;
        }

        writer.Put((ushort)Codes.Count);
        foreach ((var passcodeId, var code) in Codes)
        {
            writer.Put(passcodeId);
            writer.Put(code ?? string.Empty);
        }
    }
}
