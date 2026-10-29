using System;
using System.Reflection;
using EFT.UI;
using SPTushonka.Reflection.Patching;
using UnityEngine.UI;

namespace Fika.Core.Main.FreeCamera.Patches;

[IgnoreAutoPatch]
internal class FadeBlackScreen_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(PreloaderUI)
            .GetMethod(nameof(PreloaderUI.FadeBlackScreen));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }
}

[IgnoreAutoPatch]
internal class StartBlackScreenShow_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(PreloaderUI).GetMethod(nameof(PreloaderUI.StartBlackScreenShow));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }

    [PatchPostfix]
    // PreloaderUI.StartBlackScreenShow(Single, Single, Action, Boolean) declares the il2cpp
    // delegate, so the postfix parameter must too. This patch is [IgnoreAutoPatch] and is not
    // enabled today, but the CLR type would be the same "reads it as the wrong type" landmine
    // as soon as it was turned on.
    public static void Postfix(Il2CppSystem.Action callback)
    {
        callback?.Invoke();
    }
}

[IgnoreAutoPatch]
internal class SetBlackImageAlpha_Patch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(PreloaderUI).GetMethod(nameof(PreloaderUI.SetBlackImageAlpha));
    }

    [PatchPrefix]
    public static bool Prefix()
    {
        return false;
    }

    [PatchPostfix]
    public static void Postfix(float alpha, EFT.UI.PreloaderUI __instance)
    {
        __instance._overlapBlackImage.gameObject.SetActive(value: true);
        __instance._overlapBlackImage.color = new Color(0f, 0f, 0f, 0f);
    }
}
