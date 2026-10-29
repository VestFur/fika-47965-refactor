using System;
using System.Linq;

namespace Fika.Headless.Classes;

/// <summary>
/// How this process is allowed to behave.
/// <see cref="HeadlessRunMode.Auto"/> is the default: the mode is decided from
/// the process itself (the selected profile, or an explicit command line
/// opt-in) and never from a guess made at plugin load.
/// </summary>
internal enum HeadlessRunMode
{
    /// <summary>Decide from the process: command line first, then the selected profile.</summary>
    Auto = 0,
    /// <summary>Force the dedicated host. The user guarantees this is not a playing install.</summary>
    Headless = 1,
    /// <summary>Never act: the plugin logs and leaves the game completely untouched.</summary>
    Disabled = 2
}

/// <summary>
/// Process level opt-ins. A normal playing launch cannot produce any of them:
/// the SPT launcher starts the game without graphics flags, and a playing
/// profile is never named <c>headless_</c>. They are therefore safe to treat
/// as an explicit request to run as a dedicated host.
/// </summary>
internal static class HeadlessProcessFlags
{
    private static readonly string[] _headlessOptIns = ["-headless", "--headless"];
    private static readonly string[] _unattendedFlags = ["-batchmode", "-nographics"];

    private static string[] Arguments => Environment.GetCommandLineArgs() ?? [];

    private static bool HasArgument(string[] arguments, string flag) =>
        arguments.Any(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>An explicit, deliberate host opt-in on the command line.</summary>
    internal static bool HasHeadlessOptIn(out string matched)
    {
        var arguments = Arguments;
        matched = _headlessOptIns.FirstOrDefault(flag => HasArgument(arguments, flag));
        return matched != null;
    }

    /// <summary>
    /// True only for an unattended, display-less process: Unity received both
    /// <c>-batchmode</c> and <c>-nographics</c>. A playing client is launched
    /// by the SPT launcher and can never match this.
    /// </summary>
    internal static bool IsUnattended()
    {
        var arguments = Arguments;
        return _unattendedFlags.All(flag => HasArgument(arguments, flag));
    }
}
