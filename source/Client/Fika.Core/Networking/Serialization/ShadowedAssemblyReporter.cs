using System;
using System.IO;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes;
using Fika.Core.Main.Utils;

namespace Fika.Core.Networking.Serialization;

/// <summary>
/// Recognises and names the managed plugin assemblies that shadow an IL2CPP
/// interop proxy by assembly name.
/// <para>
/// BepInEx resolves a plugin's references by simple assembly name, so a managed
/// <c>Newtonsoft.Json.dll</c> dropped into BepInEx/plugins wins over the
/// <c>BepInEx/interop</c> proxy Fika is compiled against. Every call site that
/// needs one of the proxy's IL2CPP overloads then dies with
/// <see cref="MissingMethodException"/> <b>while that call site's own method is
/// compiled</b>. The failure is therefore raised in the caller's frame, before
/// the first statement of the failing method runs, which is why a try/catch
/// inside that method can never observe it - the enclosing method has to be a
/// different frame.
/// </para>
/// <para>
/// This class is the single place that detects the condition, reports it with
/// the file that actually won name resolution, and is safe to call from a
/// preflight before anything depends on those overloads.
/// </para>
/// </summary>
public static class ShadowedAssemblyReporter
{
    /// <summary>Simple name of the assembly whose IL2CPP overloads Fika depends on.</summary>
    public const string NewtonsoftJsonAssembly = "Newtonsoft.Json";

    private static readonly object Gate = new();

    private static bool _probed;
    private static bool _newtonsoftInteropAvailable;
    private static string _newtonsoftDescription;
    private static int _reportedIfShadowed;

    /// <summary>
    /// True while <c>Newtonsoft.Json</c> still resolves to the IL2CPP interop
    /// proxy. The probe only asks which assembly won; it never calls into it.
    /// </summary>
    public static bool NewtonsoftInteropAvailable
    {
        get
        {
            Probe();
            return _newtonsoftInteropAvailable;
        }
    }

    /// <summary>
    /// Names the assembly file that won <c>Newtonsoft.Json</c> name resolution,
    /// as <c>folder/file (state)</c> so a log line never leaks an install path.
    /// </summary>
    public static string NewtonsoftDescription
    {
        get
        {
            Probe();
            return _newtonsoftDescription;
        }
    }

    /// <summary>
    /// The standard, existing shadow report. Kept under this method name so
    /// <see cref="FikaGlobals"/> still tags the line <c>[ReportShadowed]</c>.
    /// </summary>
    public static void ReportShadowed(Exception exception)
    {
        FikaGlobals.LogWarning($"Newtonsoft.Json resolved to an assembly without the IL2CPP overloads " +
            $"({exception.GetType().Name}: {exception.Message}); using the game's JsonConvert through IL2CPP instead. " +
            $"{NewtonsoftDescription}");
    }

    /// <summary>
    /// Logs once per process when the IL2CPP overloads are unavailable, so the
    /// condition is visible from the start path instead of only from whichever
    /// call happens to fail first. Pass the caller's log prefix
    /// (for example <c>"[Headless]"</c>) so the line can be found by grep.
    /// </summary>
    public static void ReportIfShadowed(string source)
    {
        Probe();
        if (_newtonsoftInteropAvailable)
        {
            return;
        }

        if (Interlocked.Exchange(ref _reportedIfShadowed, 1) != 0)
        {
            return;
        }

        FikaGlobals.LogWarning($"{source} assemblies: Newtonsoft.Json resolves to an assembly without the IL2CPP overloads " +
            $"({_newtonsoftDescription}). Fika reaches the game's own JsonConvert through IL2CPP instead, but any other " +
            "plugin that calls the IL2CPP overloads directly will fail with MissingMethodException; keep exactly one copy " +
            "of Newtonsoft.Json.dll in the install.");
    }

    /// <summary>
    /// True for the exceptions a shadowed interop proxy produces. Callers use it
    /// as a <c>when</c> filter around a call into the proxy.
    /// </summary>
    public static bool IsShadowedInterop(Exception exception) =>
        exception is MissingMethodException or MissingFieldException or TypeLoadException
            or FileLoadException or FileNotFoundException or BadImageFormatException
        || exception.InnerException is MissingMethodException or TypeLoadException;

    private static void Probe()
    {
        lock (Gate)
        {
            if (_probed)
            {
                return;
            }

            _probed = true;

            try
            {
                // typeof() reports which assembly won name resolution and never
                // executes code in it. The shadowing managed assembly is not an
                // Il2CppObjectBase, which is what makes the check possible.
                _newtonsoftInteropAvailable =
                    typeof(Il2CppObjectBase).IsAssignableFrom(typeof(Newtonsoft.Json.JsonConverter));
            }
            catch (Exception exception)
            {
                _newtonsoftInteropAvailable = false;
                _newtonsoftDescription =
                    $"Newtonsoft.Json -> could not be probed ({exception.GetType().Name}: {exception.Message})";
                return;
            }

            _newtonsoftDescription = DescribeNewtonsoft(_newtonsoftInteropAvailable);
        }
    }

    private static string DescribeNewtonsoft(bool interopAvailable)
    {
        try
        {
            var assembly = typeof(Newtonsoft.Json.JsonConverter).Assembly;
            var location = assembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                return $"Newtonsoft.Json -> {assembly.GetName().Name}.dll " +
                    $"({(interopAvailable ? "IL2CPP interop proxy" : "managed assembly without the IL2CPP overloads")})";
            }

            var folder = Path.GetFileName(Path.GetDirectoryName(location));
            return $"Newtonsoft.Json -> {folder}/{Path.GetFileName(location)} " +
                $"({(interopAvailable ? "IL2CPP interop proxy" : "managed assembly without the IL2CPP overloads")})";
        }
        catch (Exception exception)
        {
            return $"Newtonsoft.Json -> resolution unknown ({exception.GetType().Name}: {exception.Message})";
        }
    }
}
