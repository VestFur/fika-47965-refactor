namespace Fika.Core.Networking.Packets.World;

/// <summary>Forwards the native periodic timeline correction, including late joins.</summary>
public struct CutsceneTimePacket : INetSerializable
{
    public int ProcessId;
    public string CutsceneId;
    public float ElapsedTime;

    public void Deserialize(NetDataReader reader)
    {
        ProcessId = reader.GetInt();
        CutsceneId = reader.GetString();
        ElapsedTime = reader.GetFloat();
    }

    public readonly void Serialize(NetDataWriter writer)
    {
        writer.Put(ProcessId);
        writer.Put(CutsceneId);
        writer.Put(ElapsedTime);
    }
}
