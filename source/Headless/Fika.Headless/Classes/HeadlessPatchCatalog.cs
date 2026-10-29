using Fika.Headless.Patches;
using SPTushonka.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Fika.Headless.Classes;

/// <summary>
/// The single place that knows which patches may be installed before the mode
/// is known and which ones carry the dedicated-host payload.
/// </summary>
internal static class HeadlessPatchCatalog
{
    /// <summary>
    /// Installed at plugin load, in every mode except <c>Disabled</c>. They
    /// only inspect the selected profile and log; they never touch a renderer,
    /// a material, a camera or any other game object, and they return without
    /// doing anything when the profile is not a headless one.
    /// </summary>
    internal static readonly Type[] ObserverPatches =
    [
        typeof(ConsoleScreen_OnProfileReceive_Patch),
        typeof(CharacterSelectionScreen_Show_Patch)
    ];

    /// <summary>
    /// Namespaces whose patches remove renderers, materials, textures, lights,
    /// post effects or cameras, override graphics capabilities, or replace the
    /// local raid factory. Loading any of them into a playing client is what
    /// destroys its renderer, so they are only ever installed once the process
    /// is a confirmed dedicated host.
    /// </summary>
    internal static readonly string[] GraphicsNamespaces =
    [
        "Fika.Headless.Patches.DestroyGraphics",
        "Fika.Headless.Patches.DLSS",
        "Fika.Headless.Patches.VRAM",
        "Fika.Headless.Patches.ReflexPatches",
        "Fika.Headless.Patches.TextureValidateFormat",
        "Fika.Headless.Patches.TODPatches"
    ];

    internal static bool IsGraphicsPatch(Type type)
    {
        var space = type.Namespace;
        if (string.IsNullOrEmpty(space))
            return false;
        foreach (var candidate in GraphicsNamespaces)
        {
            if (space.Equals(candidate, StringComparison.Ordinal) ||
                space.StartsWith(candidate + ".", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Every patch that must not exist in a playing install. Mirrors the
    /// client plugin's auto-patch discovery, minus the observers (already
    /// installed) and minus the graphics namespaces when the user disabled
    /// renderer destruction in the config.
    /// </summary>
    internal static List<Type> GetDeferredPatches(Assembly assembly, bool includeGraphics) =>
        assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.ContainsGenericParameters && typeof(ModulePatch).IsAssignableFrom(type))
            .Where(type => !type.IsDefined(typeof(IgnoreAutoPatchAttribute), false))
#if !DEBUG
            .Where(type => !type.IsDefined(typeof(DebugPatchAttribute), false))
#endif
            .Where(type => !ObserverPatches.Contains(type))
            .Where(type => includeGraphics || !IsGraphicsPatch(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();
}
