using System.Reflection;
using Comfort.Common;
using EFT.BufferZone;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.Lighthouse;

public class BufferInnerZone_ChangePlayerAccessStatus_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BufferInnerZone)
            .GetMethod(nameof(BufferInnerZone.ChangePlayerAccessStatus));
    }

    [PatchPostfix]
    public static void Postfix(int raidID, bool status)
    {
        if (FikaBackendUtils.ClientType == EClientType.None || FikaBackendUtils.IsClient)
        {
            return;
        }

        var player = Singleton<EFT.GameWorld>.Instance.GetAlivePlayerByRaidID(raidID);
        if (player == null) return;
        BufferZonePacket packet = new(EBufferZoneData.PlayerAccessStatus)
        {
            ProfileId = player.ProfileId,
            Available = status
        };

        FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }
}
