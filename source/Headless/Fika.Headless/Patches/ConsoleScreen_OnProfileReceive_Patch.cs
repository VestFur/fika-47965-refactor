using EFT;
using EFT.UI;
using SPTushonka.Custom.Utils;
using SPTushonka.Reflection.Patching;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Fika.Headless.Patches;

public class ConsoleScreen_OnProfileReceive_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ConsoleScreen)
            .GetMethod(nameof(ConsoleScreen.OnProfileReceive));
    }

    /// <summary>
    /// Observer, not a gate: it only reports which profile the client logged in
    /// with. A normal playing profile leaves the game running untouched - no
    /// critical error screen, no <c>Application.Quit</c>, no graphics work.
    /// </summary>
    [PatchPostfix]
    public static void Prefix(Profile profile)
    {
        var nickname = profile?.Nickname;
        var isHeadlessProfile = nickname?.Contains("headless_", StringComparison.OrdinalIgnoreCase) == true;

        FikaHeadlessPlugin.FikaHeadlessLogger.LogInfo($"[Headless] Profile: received from the login flow " +
            $"(nickname={nickname ?? "<null>"}, isHeadlessProfile={isHeadlessProfile})");

        if (!isHeadlessProfile)
        {
            FikaHeadlessPlugin.Instance?.NoteNormalPlay("ConsoleScreen.OnProfileReceive", nickname);
            return;
        }

        FikaHeadlessPlugin.Instance.MarkProfileVerified("ConsoleScreen.OnProfileReceive", nickname);
    }
}

