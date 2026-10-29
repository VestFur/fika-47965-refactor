using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Vehicle;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.World;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.BTR;

internal class BTRVehicle_TryPassengerGoOut_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BTRVehicle).GetMethod(nameof(BTRVehicle.TryPassengerGoOut));
    }

    [PatchPostfix]
    public static void Postfix(BTRPassenger passenger, EBtrInteractionStatus __result)
    {
        if (FikaBackendUtils.IsServer)
        {
            if (__result is EBtrInteractionStatus.Confirmed or EBtrInteractionStatus.EmptySlot)
            {
                if (passenger.Player.TryCast<ObservedPlayer>() is { } observedPlayer)
                {
                    BTRInteractionPacket packet = new(observedPlayer.NetId)
                    {
                        IsResponse = true,
                        Status = __result,
                        Data = new()
                        {
                            HasInteraction = true,
                            InteractionType = EInteractionType.GoOut,
                            SideId = passenger.SideId,
                            SlotId = passenger.SlotId,
                            Fast = false
                        }
                    };

                    FikaSingleton<IFikaNetworkManager>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                }
            }
        }
    }
}
