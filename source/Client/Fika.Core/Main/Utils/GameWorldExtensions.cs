using EFT;

namespace Fika.Core.Main.Utils;

public static class GameWorldExtensions
{
    public static IObserverToPlayerBridge GetAlivePlayerBridgeByProfileID(this GameWorld gameWorld, string profileId)
    {
        if (gameWorld == null || string.IsNullOrEmpty(profileId))
        {
            return null;
        }

        foreach (var player in gameWorld.AllAlivePlayersList)
        {
            if (player != null && player.ProfileId == profileId)
            {
                return gameWorld.GetAlivePlayerBridgeByRaidID(player.RaidId);
            }
        }

        return null;
    }
}
