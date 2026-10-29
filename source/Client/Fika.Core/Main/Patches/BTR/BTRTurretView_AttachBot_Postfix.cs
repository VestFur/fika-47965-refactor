using System.Reflection;
using EFT.Vehicle;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.BTR;

/// <summary>Move Fika's LocalPlayer-based remote shooter before native weapon alignment.</summary>
public class BTRTurretView_AttachBot_Postfix : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BTRTurretView), nameof(BTRTurretView.AttachBot));

    [PatchPrefix]
    public static void Prefix(BTRTurretView __instance, int btrBotId)
    {
        if (!FikaBackendUtils.IsClient) return;
        var bot = __instance._offlineBot;
        if (!bot.Item2)
        {
            if (!__instance.TryBotInitialization(btrBotId)) return;
            bot = __instance._offlineBot;
        }
        if (bot.Item1 == null) return;
        // Native AttachBot does this only for its online game mode. Fika's
        // observed player inherits LocalPlayer, so it uses the offline branch.
        bot.Item1.PlayerBones.BodyTransform.Original.position = __instance.BotRoot.position;
    }
}
