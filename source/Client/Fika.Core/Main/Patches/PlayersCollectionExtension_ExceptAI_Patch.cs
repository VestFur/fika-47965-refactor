using EFT.Game.Spawning;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using Fika.Core.Networking;
using Fika.Core.Main.Utils;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches;

public class PlayersCollectionExtension_ExceptAI_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(PlayersCollectionExtension)
            .GetMethod(nameof(PlayersCollectionExtension.ExceptAI));
    }

    [PatchPrefix]
    public static bool Prefix(Il2CppSystem.Collections.Generic.IEnumerable<IPlayer> persons, ref Il2CppSystem.Collections.Generic.IEnumerable<IPlayer> __result)
    {
        if (FikaBackendUtils.ClientType == EClientType.None) return true;
        if (persons != null)
        {
            __result = FikaSingleton<IFikaNetworkManager>.Instance.CoopHandler.HumanPlayers
                .Select(player => player.Cast<IPlayer>()).ToNativeList();
            return false;
        }

        return true;
    }
}
