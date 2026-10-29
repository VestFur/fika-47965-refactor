using Fika.Core.Main.Players;

namespace Fika.Core.Networking.LiteNetLib;

public partial class NetPeer
{
    /// <summary>The player associated with this peer on the host.</summary>
    public FikaPlayer Player { get; internal set; }
}
