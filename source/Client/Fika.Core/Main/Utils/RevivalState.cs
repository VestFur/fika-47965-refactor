using System;

namespace Fika.Core.Main.Utils;

/// <summary>Raid-local revival state. Durations, never process-local clock values, cross the wire.</summary>
public struct RevivalState : INetSerializable
{
    public bool IsAlive;
    public bool Downed;
    public bool BledOut;
    public int Revives;
    // Negative means timed bleedout is disabled; zero means it has expired.
    public float BleedoutRemaining;
    public float RevivePauseRemaining;
    public string ReviverNickname;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(IsAlive);
        writer.Put(Downed);
        writer.Put(BledOut);
        writer.Put(Revives);
        writer.Put(BleedoutRemaining);
        writer.Put(RevivePauseRemaining);
        writer.Put(ReviverNickname ?? string.Empty);
    }

    public void Deserialize(NetDataReader reader)
    {
        IsAlive = reader.GetBool();
        Downed = reader.GetBool();
        BledOut = reader.GetBool();
        Revives = reader.GetInt();
        BleedoutRemaining = reader.GetFloat();
        RevivePauseRemaining = reader.GetFloat();
        ReviverNickname = reader.GetString();
        if (Revives < 0 || !float.IsFinite(BleedoutRemaining) || !float.IsFinite(RevivePauseRemaining) ||
            RevivePauseRemaining < 0 || (Downed && (!IsAlive || BledOut)))
            throw new InvalidOperationException("Invalid revival snapshot");
    }
}

/// <summary>Also advances on the host while a player's process is disconnected or loading.</summary>
internal sealed class RevivalStateClock
{
    private RevivalState _state = new() { IsAlive = true, BleedoutRemaining = -1f };
    private double _receivedAt;

    internal RevivalState Capture()
    {
        var state = _state;
        var elapsed = (float)Math.Max(0, Time.unscaledTimeAsDouble - _receivedAt);
        if (state.Downed)
        {
            if (state.BleedoutRemaining >= 0)
                state.BleedoutRemaining = Math.Max(0, state.BleedoutRemaining - Math.Max(0, elapsed - state.RevivePauseRemaining));
            state.RevivePauseRemaining = Math.Max(0, state.RevivePauseRemaining - elapsed);
            if (state.RevivePauseRemaining == 0) state.ReviverNickname = string.Empty;
        }
        return state;
    }

    internal void Restore(RevivalState state)
    {
        _state = state;
        _receivedAt = Time.unscaledTimeAsDouble;
    }

    internal void SetDowned(bool downed, float duration)
    {
        var state = Capture();
        if (state.Downed == downed) return;
        state.IsAlive = true;
        state.Downed = downed;
        if (!downed) state.Revives++;
        state.BleedoutRemaining = downed && duration > 0 ? duration : -1f;
        state.RevivePauseRemaining = 0;
        state.ReviverNickname = string.Empty;
        Restore(state);
    }

    internal void SetReviving(bool reviving, string nickname, float duration)
    {
        var state = Capture();
        if (!state.Downed) return;
        // A disconnected rescuer cannot leave the timer paused indefinitely.
        state.RevivePauseRemaining = reviving ? Math.Max(0, duration) : 0;
        state.ReviverNickname = reviving ? nickname : string.Empty;
        Restore(state);
    }

    internal void MarkDead()
    {
        var state = Capture();
        state.BledOut |= state.Downed;
        state.IsAlive = false;
        state.Downed = false;
        state.RevivePauseRemaining = 0;
        state.ReviverNickname = string.Empty;
        Restore(state);
    }
}
