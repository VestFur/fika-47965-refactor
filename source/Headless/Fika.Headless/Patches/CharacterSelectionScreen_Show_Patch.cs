using EFT;
using EFT.UI;
using SPTushonka.Reflection.Patching;
using System;
using System.Reflection;

namespace Fika.Headless.Patches;

public class CharacterSelectionScreen_Show_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(CharacterSelectionScreen).GetMethod(nameof(CharacterSelectionScreen.Show),
            [typeof(CharacterSelectionScreen.CharacterSelectionScreenController)]);
    }

    [PatchPostfix]
    public static void Postfix(CharacterSelectionScreen.CharacterSelectionScreenController controller)
    {
        if (controller == null)
        {
            Logger.LogError("[Headless] Profile: character selection controller was null");
            return;
        }

        if (!controller.Data.TryGetValue(EGameMode.Pve, out var profileData))
        {
            Logger.LogError("[Headless] Profile: no PVE profile to select");
            return;
        }

        var nickname = profileData?.Nickname;
        var isHeadlessProfile = nickname?.Contains("headless_", StringComparison.OrdinalIgnoreCase) == true;

        Logger.LogInfo($"[Headless] Profile: character selection (id={profileData?.ProfileId}, nickname={nickname ?? "<null>"}, " +
            $"hasProfile={profileData?.HasProfile}, selectable={profileData?.IsSelectable}, locked={profileData?.IsLocked}, isHeadlessProfile={isHeadlessProfile})");

        // Normal play: never submit, never select, never touch the screen. The
        // player picks the profile exactly as if this plugin were not loaded.
        if (!isHeadlessProfile)
        {
            FikaHeadlessPlugin.Instance?.NoteNormalPlay("CharacterSelectionScreen.Show", nickname);
            return;
        }

        // On some SPT 5 IL2CPP startup paths ConsoleScreen.OnProfileReceive is
        // never dispatched, although the character-selection data is already
        // populated.  The generated headless profile is identifiable here,
        // so complete the readiness handshake from this authoritative data.
        FikaHeadlessPlugin.Instance.MarkProfileVerified("CharacterSelectionScreen.Show", nickname);

        // Harmony invokes a postfix after CharacterSelectionScreen.Show has
        // returned. Calling Submit here is therefore already outside Show's
        // native call frame. Queuing this through Fika's managed dispatcher
        // can stall in batchmode when the menu lifecycle stops ticking before
        // the first frame, leaving headless clients permanently at profile
        // selection. Submit directly and report any native exception.
        try
        {
            controller.Submit(EGameMode.Pve, profileData);
            Logger.LogInfo("[Headless] Profile: PVE profile submit invoked");
        }
        catch (Exception exception)
        {
            Logger.LogError($"[Headless] Profile: PVE profile submit failed: {exception}");
        }
    }
}

