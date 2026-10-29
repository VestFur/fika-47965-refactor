using Il2CppSystem.Collections.Generic;
using EFT.GlobalEvents;
using Fika.Core.Main.Utils;
using EFT;
using JsonType;
using System;

namespace Fika.Core.Networking.Packets.Communication;

public class TransitEventPacket : INetSerializable
{
    /// <summary>
    /// Event type byte no receiver maps to a payload. <see cref="Serialize"/> writes it when
    /// the payload of the declared type could not be built: a body that is only partially
    /// written makes NetPacketProcessor.ReadAllPackets parse the leftover bytes as another
    /// packet, and that misparse reports a random packet type.
    /// </summary>
    private const byte NoPayloadEventType = byte.MaxValue;

    /// <summary>
    /// Raid id the native transit initialization treats as "any player"
    /// (EFT TransitInteractionControllerAbstractClass.IsTargetPlayer). An initialization
    /// without a usable payload is sent with this id, so no peer can apply it to its own
    /// transit state while the packet stream stays aligned.
    /// </summary>
    internal const int AnyPlayerRaidId = -1;

    private const int MaxTransitPointCount = 1024;
    private const int MaxTransitProfileCount = 256;
    private const int MaxTransitEntryCount = 1024;

    public ETransitEventType EventType;
    public SyncEventFromServer TransitEvent;
    public int TransitId;
    public int PlayerId;
    public int[] EnabledPointIds = [];
    public int RegisteredPointId = -1;
    public string KeyId;
    public EDateTime SelectedTime;
    public LocationTransit Destination;

    public void Deserialize(NetDataReader reader)
    {
        EventType = (ETransitEventType)reader.GetByte();
        try
        {
            DeserializeBody(reader);
        }
        catch (Exception ex)
        {
            // Deserialize runs inside FikaClient.Update (NetManager.PollEvents). A packet we
            // could not finish reading would leave NetPacketProcessor.ReadAllPackets parsing
            // its leftover bytes as further packets, so the rest of this packet is dropped.
            FikaGlobals.LogError($"[Transit] Dropping a malformed {EventType} transit packet: {ex}");
            TransitEvent = null;
            DrainRemaining(reader);
        }
    }

    private void DeserializeBody(NetDataReader reader)
    {
        switch (EventType)
        {
            case ETransitEventType.Init:
                {
                    ReadInitializationPayload(reader.GetByteArray());
                    var count = reader.GetInt();
                    if (count < 0 || count > MaxTransitPointCount)
                    {
                        FikaGlobals.LogError($"[Transit] Init packet reported {count} transit points; the rest of the packet is dropped");
                        EnabledPointIds = [];
                        RegisteredPointId = -1;
                        KeyId = string.Empty;
                        SelectedTime = default;
                        DrainRemaining(reader);
                        break;
                    }
                    var ids = new int[count];
                    for (var i = 0; i < count; i++)
                    {
                        ids[i] = reader.GetInt();
                    }
                    EnabledPointIds = ids;
                    RegisteredPointId = reader.GetInt();
                    KeyId = reader.GetString();
                    SelectedTime = (EDateTime)reader.GetInt();
                }
                break;
            case ETransitEventType.GroupTimer:
                {
                    var pointId = reader.GetInt();
                    var timerAmount = reader.GetInt();
                    if (timerAmount < 0 || timerAmount > MaxTransitEntryCount)
                    {
                        FikaGlobals.LogError($"[Transit] GroupTimer packet reported {timerAmount} timers; the rest of the packet is dropped");
                        DrainRemaining(reader);
                        break;
                    }
                    TransitGroupTimerEvent timerEvent = new()
                    {
                        PointId = pointId
                    };
                    Dictionary<int, ushort> timers = new();
                    for (var i = 0; i < timerAmount; i++)
                    {
                        var key = reader.GetInt();
                        var value = reader.GetUShort();
#if DEBUG
                        FikaGlobals.LogWarning($"GroupTimer: int: {key}, ushort: {value}");
#endif
                        if (!timers.ContainsKey(key)) timers.Add(key, value);
                    }
                    timerEvent.Timers = timers;
                    TransitEvent = timerEvent;
                }
                break;
            case ETransitEventType.GroupSize:
                {
                    var pointId = reader.GetInt();
                    var sizeAmount = reader.GetInt();
                    if (sizeAmount < 0 || sizeAmount > MaxTransitEntryCount)
                    {
                        FikaGlobals.LogError($"[Transit] GroupSize packet reported {sizeAmount} sizes; the rest of the packet is dropped");
                        DrainRemaining(reader);
                        break;
                    }
                    TransitGroupSizeEvent sizeEvent = new() { PointId = pointId };
                    Dictionary<int, byte> sizes = new();
                    for (var i = 0; i < sizeAmount; i++)
                    {
                        var key = reader.GetInt();
                        var value = reader.GetByte();
#if DEBUG
                        FikaGlobals.LogWarning($"GroupSize: int: {key}, byte: {value}");
#endif
                        if (!sizes.ContainsKey(key)) sizes.Add(key, value);
                    }
                    sizeEvent.Sizes = sizes;
                    TransitEvent = sizeEvent;
                }
                break;
            case ETransitEventType.Interaction:
                {
                    TransitEvent = (TransitInteractionEvent)new()
                    {
                        PlayerRaidId = reader.GetInt(),
                        PointId = reader.GetInt(),
                        Type = (TransitInteractionEvent.EType)reader.GetByte()
                    };
                }
                break;
            case ETransitEventType.Messages:
                {
                    var messagesAmount = reader.GetInt();
                    if (messagesAmount < 0 || messagesAmount > MaxTransitEntryCount)
                    {
                        FikaGlobals.LogError($"[Transit] Messages packet reported {messagesAmount} messages; the rest of the packet is dropped");
                        DrainRemaining(reader);
                        break;
                    }
                    TransitMessagesEvent messagesEvent = new();
                    Dictionary<int, TransitMessagesEvent.EType> messages = new();
                    for (var i = 0; i < messagesAmount; i++)
                    {
                        var key = reader.GetInt();
                        var value = (TransitMessagesEvent.EType)reader.GetByte();
                        if (!messages.ContainsKey(key)) messages.Add(key, value);
                    }
                    messagesEvent.Messages = messages;
                    TransitEvent = messagesEvent;
                }
                break;
            case ETransitEventType.Extract:
                TransitId = reader.GetInt();
                PlayerId = reader.GetInt();
                Destination = ReadDestination(reader);
                break;
            default:
                // NoPayloadEventType and every type this build does not know: the sender
                // declared a payload it could not build, so there is nothing to read.
                break;
        }
    }

    /// <summary>
    /// Rebuilds the native <see cref="TransitInitEvent"/> of an Init packet. A payload that is
    /// missing or too large for the shared native buffer leaves <see cref="TransitEvent"/> null:
    /// receivers check for a usable payload (ClientTransitController.ReceiveSnapshot) and ignore
    /// the snapshot instead of applying an uninitialized one.
    /// </summary>
    private void ReadInitializationPayload(byte[] data)
    {
        var nativeReader = NetworkUtils.EventDataReader;
        var bufferLength = nativeReader?.Buffer?.Length ?? 0;
        if (data.Length == 0 || bufferLength == 0 || data.Length > bufferLength)
        {
            FikaGlobals.LogError($"[Transit] Init packet carried {data.Length} native bytes for a {bufferLength} byte buffer; the native initialization is ignored");
            TransitEvent = null;
            return;
        }

        nativeReader.Reset();
        Array.Clear(nativeReader.Buffer);
        Array.Copy(data, nativeReader.Buffer, data.Length);
        var initEvent = new TransitInitEvent();
        try
        {
            initEvent.Deserialize(ref nativeReader);
            TransitEvent = initEvent;
        }
        catch (Exception ex)
        {
            FikaGlobals.LogError($"[Transit] The native transit initialization could not be deserialized; it is ignored: {ex}");
            TransitEvent = null;
        }
    }

    public void Serialize(NetDataWriter writer)
    {
        switch (EventType)
        {
            case ETransitEventType.Init:
                {
                    var initEvent = TransitEvent?.TryCast<TransitInitEvent>();
                    if (initEvent == null)
                    {
                        FikaGlobals.LogError($"[Transit] Init packet had no usable TransitInitEvent payload ({(TransitEvent == null ? "null" : TransitEvent.GetType().Name)}); an empty initialization is sent instead");
                        initEvent = CreateEmptyInitialization();
                    }
                    writer.Put((byte)ETransitEventType.Init);
                    writer.PutByteArray(SerializeInitialization(initEvent));
                    var enabledPointIds = EnabledPointIds;
                    writer.Put(enabledPointIds?.Length ?? 0);
                    if (enabledPointIds != null)
                    {
                        foreach (var id in enabledPointIds)
                        {
                            writer.Put(id);
                        }
                    }
                    writer.Put(RegisteredPointId);
                    writer.Put(KeyId ?? string.Empty);
                    writer.Put((int)SelectedTime);
                }
                break;
            case ETransitEventType.GroupTimer:
                {
                    if (TransitEvent?.TryCast<TransitGroupTimerEvent>() is not { } timerEvent || timerEvent.Timers == null)
                    {
                        WriteNoPayload(writer, nameof(ETransitEventType.GroupTimer));
                        break;
                    }
                    writer.Put((byte)ETransitEventType.GroupTimer);
                    writer.Put(timerEvent.PointId);
                    writer.Put(timerEvent.Timers.Count);
                    foreach (var timer in timerEvent.Timers)
                    {
                        writer.Put(timer.Key);
                        writer.Put(timer.Value);
                    }
                }
                break;
            case ETransitEventType.GroupSize:
                {
                    if (TransitEvent?.TryCast<TransitGroupSizeEvent>() is not { } sizeEvent || sizeEvent.Sizes == null)
                    {
                        WriteNoPayload(writer, nameof(ETransitEventType.GroupSize));
                        break;
                    }
                    writer.Put((byte)ETransitEventType.GroupSize);
                    writer.Put(sizeEvent.PointId);
                    writer.Put(sizeEvent.Sizes.Count);
                    foreach (var size in sizeEvent.Sizes)
                    {
                        writer.Put(size.Key);
                        writer.Put(size.Value);
                    }
                }
                break;
            case ETransitEventType.Interaction:
                {
                    if (TransitEvent?.TryCast<TransitInteractionEvent>() is not { } interactionEvent)
                    {
                        WriteNoPayload(writer, nameof(ETransitEventType.Interaction));
                        break;
                    }
                    writer.Put((byte)ETransitEventType.Interaction);
                    writer.Put(interactionEvent.PlayerRaidId);
                    writer.Put(interactionEvent.PointId);
                    writer.Put((byte)interactionEvent.Type);
                }
                break;
            case ETransitEventType.Messages:
                {
                    if (TransitEvent?.TryCast<TransitMessagesEvent>() is not { } messagesEvent || messagesEvent.Messages == null)
                    {
                        WriteNoPayload(writer, nameof(ETransitEventType.Messages));
                        break;
                    }
                    writer.Put((byte)ETransitEventType.Messages);
                    writer.Put(messagesEvent.Messages.Count);
                    foreach (var message in messagesEvent.Messages)
                    {
                        writer.Put(message.Key);
                        writer.Put((byte)message.Value);
                    }
                }
                break;
            case ETransitEventType.Extract:
                writer.Put((byte)ETransitEventType.Extract);
                writer.Put(TransitId);
                writer.Put(PlayerId);
                WriteDestination(writer, Destination);
                break;
            default:
                writer.Put((byte)EventType);
                break;
        }
    }

    /// <summary>
    /// Serializes the native payload of an initialization. The shared native writer and a
    /// native exception both stay contained here: HostGameController.SetupEventsAndExfils
    /// continues with the exfiltration setup, the game status and the bot activation after
    /// this send, so an escaping exception would abort the whole raid startup.
    /// </summary>
    private static byte[] SerializeInitialization(TransitInitEvent initEvent)
    {
        var nativeWriter = NetworkUtils.EventDataWriter;
        var bufferLength = nativeWriter?.Buffer?.Length ?? 0;
        if (bufferLength == 0)
        {
            FikaGlobals.LogError("[Transit] The native event writer is unavailable; the initialization is sent without a native payload");
            return [];
        }

        try
        {
            if (initEvent.Points == null || initEvent.CompletedQuestRequirementMetByPointId == null)
            {
                // A null native field is what makes TransitInitEvent.Serialize throw a native
                // NullReferenceException mid write. Send a well formed empty initialization.
                FikaGlobals.LogError($"[Transit] The initialization of raid id {initEvent.PlayerRaidId} had no point or requirement dictionary; an empty initialization is sent instead");
                initEvent = CreateEmptyInitialization();
            }

            nativeWriter.Reset();
            initEvent.Serialize(ref nativeWriter);
            nativeWriter.Flush();
            var written = nativeWriter.BytesWritten;
            if (written <= 0 || written > bufferLength)
            {
                FikaGlobals.LogError($"[Transit] The native initialization writer reported {written} bytes for a {bufferLength} byte buffer; the native payload is dropped");
                return [];
            }

            var data = new byte[written];
            Array.Copy(nativeWriter.Buffer, data, written);
            return data;
        }
        catch (Exception ex)
        {
            FikaGlobals.LogError($"[Transit] The native transit initialization could not be serialized; the native payload is dropped: {ex}");
            return [];
        }
    }

    /// <summary>
    /// A well formed initialization without content. The raid id is the native "any player"
    /// sentinel, so the native transit controller never applies it to the local player.
    /// </summary>
    private static TransitInitEvent CreateEmptyInitialization()
    {
        return new TransitInitEvent
        {
            PlayerRaidId = AnyPlayerRaidId, Points = new Dictionary<int, LocationSettings.Location.TransitParameters>(),
            CompletedQuestRequirementMetByPointId = new Dictionary<int, bool>(),
            TransitionCount = 0, EventPlayer = false
        };
    }

    /// <summary>
    /// The declared event type has no payload. Writing a short body would make the receiver
    /// parse the remaining bytes of that body as another packet, so the packet is written as
    /// an event type every receiver ignores.
    /// </summary>
    private static void WriteNoPayload(NetDataWriter writer, string eventName)
    {
        FikaGlobals.LogError($"[Transit] {eventName} packet had no usable payload; it is sent as an event the receiver ignores");
        writer.Put(NoPayloadEventType);
    }

    private static void DrainRemaining(NetDataReader reader)
    {
        var available = reader.AvailableBytes;
        if (available > 0)
        {
            reader.SkipBytes(available);
        }
    }

    private static void WriteDestination(NetDataWriter writer, LocationTransit data)
    {
        if (data == null)
        {
            // The peer validates the destination against its own transit points
            // (ClientTransitController.HandleClientExtract) and drops an empty one.
            FikaGlobals.LogError("[Transit] Extract packet had no destination; an empty destination is sent instead");
            writer.Put(string.Empty);
            writer.Put(0);
            writer.Put(string.Empty);
            writer.Put(string.Empty);
            writer.Put(string.Empty);
            writer.Put(0);
            writer.Put(0);
            writer.Put(0);
            writer.Put(0);
            return;
        }

        writer.Put(data.hash ?? string.Empty);
        writer.Put(data.playersCount);
        writer.Put(data.ip ?? string.Empty);
        writer.Put(data.location ?? string.Empty);
        writer.Put(data.transitionRaidId ?? string.Empty);
        writer.Put((int)data.raidMode);
        writer.Put((int)data.side);
        writer.Put((int)data.dayTime);
        var profiles = data.profiles;
        writer.Put(profiles?.Count ?? 0);
        if (profiles == null)
        {
            return;
        }
        foreach (var entry in profiles)
        {
            var profileKey = entry.Value;
            writer.Put(entry.Key ?? string.Empty);
            writer.Put(profileKey?._id ?? string.Empty);
            writer.Put(profileKey?.keyId ?? string.Empty);
            writer.Put(profileKey?.isSolo ?? false);
        }
    }

    private static LocationTransit ReadDestination(NetDataReader reader)
    {
        var data = new LocationTransit
        {
            hash = reader.GetString(), playersCount = reader.GetInt(), ip = reader.GetString(),
            location = reader.GetString(), transitionRaidId = reader.GetString(),
            raidMode = (ERaidMode)reader.GetInt(), side = (ESideType)reader.GetInt(),
            dayTime = (EDateTime)reader.GetInt(), profiles = new()
        };
        var count = reader.GetInt();
        if (count < 0 || count > MaxTransitProfileCount)
        {
            // The destination is the last field of this packet, so the unusable tail is
            // dropped here instead of being parsed as another packet.
            FikaGlobals.LogError($"[Transit] Extract packet reported {count} transit profiles; the destination is treated as empty");
            DrainRemaining(reader);
            return data;
        }
        for (var i = 0; i < count; i++)
        {
            var profileId = reader.GetString();
            var id = reader.GetString();
            var keyId = reader.GetString();
            var isSolo = reader.GetBool();
            if (!data.profiles.ContainsKey(profileId))
            {
                data.profiles.Add(profileId, new ProfileKey
                {
                    _id = id, keyId = keyId, isSolo = isSolo
                });
            }
        }
        return data;
    }

    public enum ETransitEventType
    {
        Init,
        GroupTimer,
        GroupSize,
        Interaction,
        Messages,
        Extract,

        /// <summary>
        /// "The host has started loading the next map of a transit reload" (analysis/transit-reload-sync).
        /// The packet carries no payload on purpose: the type byte alone is the signal. A receiver whose
        /// build does not know this value reads that byte, finds an unknown type and ignores the packet -
        /// exactly what the serializer's default branch writes for it. Both halves of that contract must
        /// stay in place: the default case of <see cref="Serialize"/> keeps writing the bare type byte and
        /// the default case of <see cref="DeserializeBody"/> keeps reading nothing.
        /// </summary>
        ReloadGo
    }
}
