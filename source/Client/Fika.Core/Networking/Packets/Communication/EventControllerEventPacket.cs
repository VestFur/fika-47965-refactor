using EFT;
using EFT.GlobalEvents;

namespace Fika.Core.Networking.Packets.Communication;

public struct EventControllerEventPacket : INetSerializable
{
    public int NetId;
    public string ItemId;
    public EEventType Type;
    public SyncEventFromServer Event;
    // Runddans' native event only carries the state enum.  These two maps
    // preserve the native generator progress across a late join/reconnect so
    // a running or damaged generator does not reset its timer.
    public System.Collections.Generic.Dictionary<int, float> Durability;
    public System.Collections.Generic.Dictionary<int, long> DateTimeTicks;

    public void Deserialize(NetDataReader reader)
    {
        Type = (EEventType)reader.GetByte();
        if (Type == EEventType.StateEvent)
        {
            RunddansStateEvent stateEvent = new()
            {
                PlayerRaidId = reader.GetInt()
            };
            var amount = reader.GetInt();
            stateEvent.Objects = new(amount);
            for (var i = 0; i < amount; i++)
            {
                stateEvent.Objects.Add(reader.GetInt(),
                    (EventObject.EState)reader.GetByte());
            }
            var progressAmount = reader.GetInt();
            if (progressAmount < 0 || progressAmount > 4096) throw new System.InvalidOperationException("Invalid Runddans progress count");
            var durability = new System.Collections.Generic.Dictionary<int, float>(progressAmount);
            var dateTimes = new System.Collections.Generic.Dictionary<int, long>(progressAmount);
            for (var i = 0; i < progressAmount; i++)
            {
                var id = reader.GetInt();
                durability[id] = reader.GetFloat();
                dateTimes[id] = reader.GetLong();
            }
            Durability = durability;
            DateTimeTicks = dateTimes;
            Event = stateEvent;
        }
        else if (Type == EEventType.MessageEvent)
        {
            Event = new RunddansMessagesEvent()
            {
                PlayerRaidId = reader.GetInt(),
                Type = (RunddansMessagesEvent.EType)reader.GetByte()
            };
        }
        else if (Type == EEventType.RemoveItem)
        {
            NetId = reader.GetInt();
            ItemId = reader.GetString();
        }
    }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((byte)Type);
        if (Type == EEventType.StateEvent)
        {
            var stateEvent = Event.Cast<RunddansStateEvent>();
            writer.Put(stateEvent.PlayerRaidId);
            writer.Put(stateEvent.Objects.Count);
            foreach (var entry in stateEvent.Objects)
            {
                writer.Put(entry.Key);
                writer.Put((byte)entry.Value);
            }
            var durability = Durability ?? new();
            var dateTimes = DateTimeTicks ?? new();
            // Write the union of both maps. This keeps a valid timestamp even
            // when an object has zero durability and avoids silently dropping
            // its native damage time from the snapshot.
            var ids = new System.Collections.Generic.HashSet<int>(durability.Keys);
            ids.UnionWith(dateTimes.Keys);
            writer.Put(ids.Count);
            foreach (var id in ids)
            {
                writer.Put(id);
                durability.TryGetValue(id, out var value);
                writer.Put(value);
                dateTimes.TryGetValue(id, out var ticks);
                writer.Put(ticks);
            }
        }
        else if (Type == EEventType.MessageEvent)
        {
            var messagesEvent = Event.Cast<RunddansMessagesEvent>();
            writer.Put(messagesEvent.PlayerRaidId);
            writer.Put((byte)messagesEvent.Type);
        }
        else if (Type == EEventType.RemoveItem)
        {
            writer.Put(NetId);
            writer.Put(ItemId ?? "");
        }
    }

    public enum EEventType
    {
        StartedEvent,
        RemoveItem,
        StateEvent,
        MessageEvent
    }
}
