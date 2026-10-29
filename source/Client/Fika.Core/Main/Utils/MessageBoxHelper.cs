using System;
using EFT;
using EFT.UI;
using HarmonyLib;
using Fika.Core.UI;

namespace Fika.Core.Main.Utils;

internal static class MessageBoxHelper
{
    internal enum MessageBoxType { OK }

    internal static void Show(string message, string title, MessageBoxType type)
    {
        try
        {
            var preloader = MonoBehaviourSingleton<PreloaderUI>.Instance;
            preloader.ShowCriticalErrorScreen(title, message, ErrorScreen.EButtonType.OkButton, 0f);
        }
        catch (Exception ex)
        {
            FikaGlobals.LogError($"Unable to show message box: {ex}");
        }
    }
}
