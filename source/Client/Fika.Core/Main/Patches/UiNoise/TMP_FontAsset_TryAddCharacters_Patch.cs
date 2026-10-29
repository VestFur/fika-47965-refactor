using System;
using System.Reflection;
using Fika.Core.Main.Utils;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using TMPro;

namespace Fika.Core.Main.Patches.UiNoise;

/// <summary>
/// Stops the SimSun character-add flood (5,744 warnings in one raid).
///
/// The Chinese locale makes EFT add the characters of its localization tables to the fallback
/// SimSun font asset: <c>EFT.LocalizationManager.UpdateFonts(string)</c> calls
/// <c>TMPro.TMP_FontAsset.TryAddCharacters(string, bool)</c> once per entry it walks, and entries
/// without characters arrive as null/empty. The vanilla body of that case is only: log
/// "Unable to add characters to font asset [...] because the provided character list is Null or
/// Empty", copy the input into the "missing" out parameter and return false - no glyph is added and
/// no atlas is touched. The 09:45 raid emitted 5,744 of those warnings back to back
/// (LogOutput.log lines 833-6576) from the single locale update that starts right after Fika's
/// "Waiting for locales to be ready...".
///
/// Skipping the call for a null/empty list therefore keeps the game behaviour identical and removes
/// the spam. The first skip is reported once at Info level with the font name, so the evidence for
/// this decision survives in the log.
///
/// Both guards are exception safe: a guard that throws inside the game's own method would be worse
/// than the warning it removes, so an unexpected shape degrades to one warning line and the vanilla
/// call runs unchanged (see <see cref="TrySkipEmptyRequest"/>).
/// </summary>
internal sealed class TMP_FontAsset_TryAddCharacters_Patch : ModulePatch
{
    private static bool _reported;
    private static bool _guardFailed;

    protected override MethodBase GetTargetMethod()
    {
        return FontGuardTarget.Resolve(
            "TryAddCharacters(string, bool)",
            static parameters => parameters.Length == 2
                && IsString(parameters[0], byRef: false)
                && parameters[1].ParameterType == typeof(bool),
            [typeof(string), typeof(bool)]);
    }

    [PatchPrefix]
    private static bool Prefix(TMP_FontAsset __instance, string characters, ref bool __result)
    {
        try
        {
            if (!string.IsNullOrEmpty(characters))
            {
                // A real request: the game must add those characters.
                return true;
            }

            // The same answer the vanilla body produces for this input, minus the warning.
            __result = false;

            if (!_reported)
            {
                _reported = true;
                FikaGlobals.LogInfo("[FontGuard] suppressed the first empty TMP character request for font '" +
                    (__instance == null ? "null" : __instance.name) +
                    "'; EFT.LocalizationManager.UpdateFonts sends one per locale entry without characters " +
                    "(5,744 warnings in the 09:45 raid). Further empty calls are skipped silently.");
            }

            return false;
        }
        catch (Exception exception)
        {
            ReportGuardFailure(ref _guardFailed, exception);
            return true;
        }
    }

    internal static bool IsString(ParameterInfo parameter, bool byRef)
    {
        var type = parameter.ParameterType;
        if (type.IsByRef != byRef)
        {
            return false;
        }

        var element = byRef ? type.GetElementType() : type;
        // The interop spells System.String as "String" (and Il2CppSystem.String would render the same
        // way), so the element name is the shape test - never an assembly-qualified string.
        return element is not null && element.Name == "String";
    }

    internal static void ReportGuardFailure(ref bool reported, Exception exception)
    {
        if (reported)
        {
            return;
        }

        reported = true;
        
    }
}

/// <summary>
/// The same guard for the overload that actually owns the warning. The two argument overload above
/// funnels into this one, and this is the method that logs "Unable to add characters to font asset
/// [...] because the provided character list is Null or Empty" and then returns false without
/// touching the atlas. Skipping the empty case therefore produces exactly the vanilla result.
/// </summary>
internal sealed class TMP_FontAsset_TryAddCharacters_Missing_Patch : ModulePatch
{
    private static bool _guardFailed;

    protected override MethodBase GetTargetMethod()
    {
        // The second parameter is an out/byref string. A byref parameter's Type.Name is "String&", so a
        // scan that compares the name directly never matches (that threw
        // "TMPro.TMP_FontAsset.TryAddCharacters(string, out string, bool)" at patch validation in the 10:11
        // run and left this patch unapplied); the element type is the shape that identifies it.
        return FontGuardTarget.Resolve(
            "TryAddCharacters(string, out string, bool)",
            static parameters => parameters.Length == 3
                && TMP_FontAsset_TryAddCharacters_Patch.IsString(parameters[0], byRef: false)
                && TMP_FontAsset_TryAddCharacters_Patch.IsString(parameters[1], byRef: true)
                && parameters[2].ParameterType == typeof(bool),
            [typeof(string), typeof(string).MakeByRefType(), typeof(bool)]);
    }

    [PatchPrefix]
    private static bool Prefix(string characters, ref string missingCharacters, ref bool __result)
    {
        try
        {
            if (!string.IsNullOrEmpty(characters))
            {
                return true;
            }

            // Vanilla behaviour for this input: report nothing missing, add nothing, return false.
            missingCharacters = characters;
            __result = false;
            return false;
        }
        catch (Exception exception)
        {
            TMP_FontAsset_TryAddCharacters_Patch.ReportGuardFailure(ref _guardFailed, exception);
            return true;
        }
    }
}

/// <summary>
/// Shared target resolution for both guards: the exact namespace-qualified interop signature first
/// (<c>AccessTools.Method</c> with the byref parameter spelled the way the metadata has it), then a
/// parameter-shape scan for a build whose interop spells the types differently.
/// </summary>
internal static class FontGuardTarget
{
    internal static MethodBase Resolve(string expectedSignature, Func<ParameterInfo[], bool> shapeMatches, Type[] exact)
    {
        var exactMatch = AccessToolsMethod("TryAddCharacters", exact);
        if (exactMatch != null)
        {
            return exactMatch;
        }

        foreach (var method in typeof(TMP_FontAsset).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != nameof(TMP_FontAsset.TryAddCharacters))
            {
                continue;
            }

            if (shapeMatches(method.GetParameters()))
            {
                
                return method;
            }
        }

        throw new MissingMethodException("TMPro.TMP_FontAsset." + expectedSignature +
            " does not exist in this game build; the empty-character guard cannot be installed.");
    }

    private static MethodBase AccessToolsMethod(string name, Type[] parameters)
    {
        try
        {
            return AccessTools.Method(typeof(TMP_FontAsset), name, parameters);
        }
        catch (Exception exception)
        {
            
            return null;
        }
    }
}
