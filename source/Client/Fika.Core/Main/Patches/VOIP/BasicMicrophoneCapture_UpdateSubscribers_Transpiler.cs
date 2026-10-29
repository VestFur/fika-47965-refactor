using System;
using System.Reflection;
using Dissonance.Audio.Capture;
using HarmonyLib;
using SPTushonka.Reflection.Patching;

namespace Fika.Core.Main.Patches.VOIP;

/// <summary>Suppress repeated empty-clip warnings while retaining native capture/restart behavior.</summary>
public class BasicMicrophoneCapture_UpdateSubscribers_Transpiler : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Dissonance.Log),
        nameof(Dissonance.Log.WriteLog), new[] { typeof(Dissonance.LogLevel), typeof(string) });

    [PatchPrefix]
    public static bool Prefix(Dissonance.Log __instance, string message)
    {
        // Keep all other recording errors and the native sample processing path.
        return __instance.Pointer != BasicMicrophoneCapture.Log.Pointer ||
            !string.Equals(message, "Unknown microphone capture error (zero length clip) - restarting mic", StringComparison.Ordinal);
    }
}
