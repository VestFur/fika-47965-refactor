using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Fika.Core.Main.Utils;
using Fika.Core.Networking.Serialization;

namespace Fika.Headless.Classes;

/// <summary>
/// Reports the assemblies that a plugin copy can shadow in this install, so a
/// whole class of "the host hangs instead of creating the raid" failures is
/// visible from the start path instead of from whichever line dies first.
/// <para>
/// BepInEx resolves a plugin's references by simple assembly name. When the
/// same name also exists under BepInEx/plugins, the plugin copy can win over
/// the BepInEx/interop proxy the port is compiled against; every call site that
/// needs one of the proxy's IL2CPP overloads then fails with
/// <see cref="MissingMethodException"/>, raised while the *calling* method is
/// compiled. That happened to the headless start path through
/// <c>Newtonsoft.Json</c>.
/// </para>
/// <para>
/// Only assemblies that Fika.Headless and Fika.Core actually reference are
/// checked, so an unrelated plugin shipping its own JSON library is not
/// reported. The scan reads assembly metadata - nothing is loaded.
/// </para>
/// </summary>
internal static class HeadlessAssemblies
{
    /// <summary>
    /// Folders that provide the assemblies Fika is compiled against. A name that
    /// also exists under <c>plugins</c> is the shadowing case.
    /// </summary>
    private static readonly string[] ProxyFolders = ["core", "interop"];

    /// <summary>
    /// Logs the full inventory once per armed host. Never throws: the caller
    /// wraps it, but a diagnostic must not be able to stop the payload.
    /// </summary>
    internal static void Report(ManualLogSource log)
    {
        var root = BepInExRoot();
        if (string.IsNullOrEmpty(root))
        {
            log.LogWarning("[Headless] Assemblies: the BepInEx root could not be derived from the plugin location, " +
                "only the Newtonsoft.Json resolution is reported");
            ReportNewtonsoft(log);
            return;
        }

        var plugins = ScanManagedAssemblies(Path.Combine(root, "plugins"));
        var referenced = ReferencedAssemblyNames();

        log.LogInfo($"[Headless] Assemblies: {plugins.Count} managed assemblies under BepInEx/plugins, " +
            $"{referenced.Count} referenced by Fika.Headless and Fika.Core");

        var shadowed = 0;
        foreach (var name in referenced.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!plugins.TryGetValue(name, out var pluginFile))
            {
                continue;
            }

            foreach (var folder in ProxyFolders)
            {
                if (!File.Exists(Path.Combine(root, folder, name + ".dll")))
                {
                    continue;
                }

                shadowed++;
                log.LogError($"[Headless] Assemblies: '{name}' exists as both {folder}/{name}.dll and plugins/{pluginFile}; " +
                    "the plugins copy can shadow the IL2CPP proxy and make IL2CPP overloads throw MissingMethodException. " +
                    "Keep exactly one copy - remove the plugins copy.");
                break;
            }
        }

        if (shadowed == 0)
        {
            log.LogInfo($"[Headless] Assemblies: none of the {referenced.Count} referenced assemblies is shadowed by a " +
                "BepInEx/plugins copy");
        }

        ReportNewtonsoft(log);
    }

    /// <summary>
    /// The warning-only form, for a start path that just failed on a shadowed
    /// lookup. Reports once per process.
    /// </summary>
    internal static void ReportIfShadowed(ManualLogSource log)
    {
        if (ShadowedAssemblyReporter.NewtonsoftInteropAvailable)
        {
            return;
        }

        ShadowedAssemblyReporter.ReportIfShadowed("[Headless]");
        log.LogError($"[Headless] Assemblies: the IL2CPP overloads of Newtonsoft.Json are NOT available " +
            $"({ShadowedAssemblyReporter.NewtonsoftDescription}). Fika itself routes native values through the game's own " +
            "JsonConvert via IL2CPP and keeps working, but any other plugin that calls the IL2CPP overloads directly " +
            "throws MissingMethodException. Remove plugins/Newtonsoft.Json.dll to restore them.");
    }

    private static void ReportNewtonsoft(ManualLogSource log)
    {
        log.LogInfo($"[Headless] Assemblies: {ShadowedAssemblyReporter.NewtonsoftDescription}");
        ReportIfShadowed(log);
    }

    /// <summary>
    /// The BepInEx root this plugin was loaded from, derived from the plugin's own
    /// location (…/BepInEx/plugins/fika/Fika.Headless.dll). Derived rather than
    /// read from a BepInEx API so no install path is stored and no API version
    /// matters.
    /// </summary>
    private static string BepInExRoot()
    {
        try
        {
            var folder = Path.GetDirectoryName(typeof(FikaHeadlessPlugin).Assembly.Location);
            while (!string.IsNullOrEmpty(folder))
            {
                if (string.Equals(Path.GetFileName(folder), "plugins", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(folder);
                }

                folder = Path.GetDirectoryName(folder);
            }
        }
        catch (Exception)
        {
            // Fall through to null: the caller degrades to the Newtonsoft report.
        }

        return null;
    }

    /// <summary>Assembly names this process resolves at runtime.</summary>
    private static List<string> ReferencedAssemblyNames()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        Assembly[] assemblies = [typeof(FikaHeadlessPlugin).Assembly, typeof(FikaBackendUtils).Assembly];

        foreach (var assembly in assemblies)
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!string.IsNullOrEmpty(reference.Name))
                {
                    names.Add(reference.Name);
                }
            }
        }

        return [.. names];
    }

    /// <summary>
    /// Maps assembly simple name to its path relative to <paramref name="folder"/>.
    /// Files that are not managed assemblies are skipped.
    /// </summary>
    private static Dictionary<string, string> ScanManagedAssemblies(string folder)
    {
        Dictionary<string, string> found = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder))
        {
            return found;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            string name;
            try
            {
                name = AssemblyName.GetAssemblyName(file).Name;
            }
            catch (Exception)
            {
                // Native DLL or unreadable file: not an assembly that can shadow.
                continue;
            }

            if (string.IsNullOrEmpty(name) || found.ContainsKey(name))
            {
                continue;
            }

            found[name] = Path.GetRelativePath(folder, file).Replace('\\', '/');
        }

        return found;
    }
}
