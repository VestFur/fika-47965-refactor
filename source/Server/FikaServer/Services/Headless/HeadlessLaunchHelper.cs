using System.Text;

namespace FikaServer.Services.Headless;

/// <summary>
/// Builds the double-clickable <c>StartHeadless.cmd</c> that the server writes next to
/// <c>HeadlessConfig.json</c> for every headless profile, so a dedicated host can be started
/// without the SPT launcher - which never lists headless profiles.
/// </summary>
/// <remarks>
/// The generated file stores no absolute path: it finds the game folder by walking up from its
/// own location. Only the values that belong to the profile are embedded: the profile id
/// (the launcher's <c>-token</c>), the backend url (the launcher's <c>BackendUrl</c>), the
/// window state from <c>HeadlessConfig.StartMinimized</c> and the display mode from
/// <c>HeadlessConfig.Display</c>.
/// <para>
/// Two command lines exist, both of which keep the SPT launcher's arguments:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>Console</c>: <c>EscapeFromTarkov.exe -force-gfx-jobs native -batchmode -nographics
/// -token=... -config=...</c> inside its own console window. Unity creates a Null graphics
/// device, so no shader is compiled, no swapchain exists and no draw call is submitted; the
/// game's own stdout and the log are the only output.
/// </description></item>
/// <item><description>
/// <c>Hidden</c>: <c>EscapeFromTarkov.exe -force-gfx-jobs native -token=... -config=...</c>
/// started with <c>start /min</c>; the Fika.Headless graphics patches tear the scene down.
/// </description></item>
/// </list>
/// </remarks>
internal static class HeadlessLaunchHelper
{
    /// <summary>Name of the generated helper, written next to HeadlessConfig.json.</summary>
    public const string FileName = "StartHeadless.cmd";

    /// <summary>How many parent folders the helper checks for EscapeFromTarkov.exe.</summary>
    public const int SearchDepth = 8;

    /// <summary>
    /// Builds the JSON value the launcher hands to the game through <c>-config=</c>. These are
    /// exactly the launcher's three fields: <c>BackendUrl</c> is the server url this mod already
    /// publishes as HeadlessConfig.BackendUrl, while <c>Version</c> and <c>MatchingVersion</c>
    /// are the launcher's fixed <c>live</c> markers.
    /// </summary>
    public static string BuildLaunchConfig(string backendUrl)
    {
        return $"{{'BackendUrl':'{backendUrl}','Version':'live','MatchingVersion':'live'}}";
    }

    /// <summary>
    /// The graphics flags for a display mode. <c>Console</c> is the only mode that removes the
    /// graphics device; everything else keeps the historical windowed host.
    /// </summary>
    public static string BuildDisplayArguments(string displayMode)
    {
        return string.Equals(displayMode, "Console", StringComparison.OrdinalIgnoreCase)
            ? " -batchmode -nographics"
            : string.Empty;
    }

    /// <summary>
    /// Escapes a value so cmd.exe passes it to the game unchanged while it is written unquoted,
    /// which is the form the SPT launcher uses for <c>-config=</c>. Characters cmd.exe gives a
    /// meaning to are caret escaped, percent is doubled, and double quotes - which cannot survive
    /// an unquoted cmd argument - are dropped.
    /// </summary>
    public static string EscapeForCmd(string value)
    {
        StringBuilder builder = new(value.Length + 8);

        foreach (var character in value)
        {
            switch (character)
            {
                case '%':
                    builder.Append("%%");
                    break;
                case '"':
                    break;
                case '&':
                case '|':
                case '<':
                case '>':
                case '^':
                    builder.Append('^').Append(character);
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the complete contents of the launch helper. The file is pure ASCII with CRLF line
    /// endings, so cmd.exe parses it identically on every machine.
    /// </summary>
    /// <param name="startMinimized">
    /// The value of <c>HeadlessConfig.StartMinimized</c> for this profile. When it is true the
    /// game is started with <c>start /min</c>, so the host never steals the desktop; the Fika
    /// client plugin reads the same file and minimises its own window as well, which covers hosts
    /// that were not started through this helper. Unused in <c>Console</c> mode: that host has no
    /// window at all.
    /// </param>
    /// <param name="displayMode">
    /// <c>Console</c> (no client picture, no shaders, no GPU) or <c>Hidden</c> (windowed host).
    /// </param>
    public static string Build(string profileId, string backendUrl, bool startMinimized, string displayMode = "Hidden")
    {
        var console = string.Equals(displayMode, "Console", StringComparison.OrdinalIgnoreCase);
        var escapedProfileId = EscapeForCmd(profileId);
        var escapedBackendUrl = EscapeForCmd(backendUrl);
        var launchConfig = EscapeForCmd(BuildLaunchConfig(backendUrl));
        var gameArguments = $"-force-gfx-jobs native{BuildDisplayArguments(displayMode)} -token={escapedProfileId} -config={launchConfig}";
        var searchRange = string.Join(" ", Enumerable.Range(1, SearchDepth));
        var startFlags = startMinimized && !console ? "/min" : string.Empty;
        var windowDocumentation = console
            ? "rem  This profile runs Console display mode: no game window, no graphics device."
            : startMinimized
                ? "rem  This profile starts its host minimised (StartMinimized=true)."
                : "rem  This profile starts its host with a normal window (StartMinimized=false).";

        List<string> lines =
        [
            "@echo off",
            "setlocal EnableExtensions",
            "rem ============================================================================",
            "rem  Fika dedicated host launcher - generated by fika-server",
            $"rem  Profile: {escapedProfileId}",
            $"rem  Display: {displayMode} (headless.display.mode, or HeadlessConfig.json)",
            "rem",
            "rem  Double-click this file to start the dedicated host for this profile. The SPT",
            "rem  launcher never lists headless profiles, so this helper runs the same command",
            "rem  line the launcher uses for a normal profile:",
            $"rem      EscapeFromTarkov.exe {gameArguments}",
            "rem",
            "rem  No absolute path is stored in this file. The game folder is resolved in order:",
            "rem      1. the FIKA_GAME_DIR environment variable",
            "rem      2. the first argument, when that folder contains EscapeFromTarkov.exe",
            "rem      3. by walking up from this file's own folder",
            "rem",
            "rem  Usage:",
            "rem      StartHeadless.cmd",
            "rem          start the host",
            "rem      StartHeadless.cmd \"game folder\"",
            "rem          start the host from an explicit game folder",
            "rem      StartHeadless.cmd -logFile host.log -screen-fullscreen 0",
            "rem          start the host and forward the extra arguments to the game",
            "rem      set FIKA_NO_MINIMIZE=1",
            "rem          hidden host only: start this run with a normal, restored window",
            "rem      set FIKA_WAIT=1",
            "rem          run the game inside this console instead of a new process, so its",
            "rem          output stays visible - useful when the host fails on startup",
            "rem",
            "rem  Console mode keeps the game in its own console window and gives it no graphics",
            "rem  device: no shader is compiled, no draw call is submitted, no window is created.",
            "rem  The window state is the StartMinimized value of HeadlessConfig.json, which the",
            "rem  Fika.Headless client plugin reads as well. This file is regenerated by",
            "rem  fika-server; change the server, not this file.",
            "rem ============================================================================",
            "",
            windowDocumentation,
            "set \"SCRIPT_DIR=%~dp0\"",
            $"set \"PROFILE_ID={escapedProfileId}\"",
            $"set \"BACKEND_URL={escapedBackendUrl}\"",
            $"set \"LAUNCH_CONFIG={launchConfig}\"",
            $"set \"DISPLAY_MODE={displayMode}\"",
            $"set \"GAME_ARGS=-force-gfx-jobs native{BuildDisplayArguments(displayMode)} -token=%PROFILE_ID% -config=%LAUNCH_CONFIG%\"",
            $"set \"START_MINIMIZED={(startMinimized ? "1" : "0")}\"",
            $"set \"START_FLAGS={startFlags}\"",
            "set \"GAME_DIR=\"",
            "set \"DIR_ARG=\"",
            "",
            "rem --- 1. explicit override, 2. first argument, 3. walk up from this file -------",
            "if defined FIKA_GAME_DIR if exist \"%FIKA_GAME_DIR%\\EscapeFromTarkov.exe\" set \"GAME_DIR=%FIKA_GAME_DIR%\"",
            "if defined GAME_DIR goto :fika_args",
            "if not \"%~1\"==\"\" if exist \"%~1\\EscapeFromTarkov.exe\" set \"GAME_DIR=%~1\"",
            "if defined GAME_DIR set \"DIR_ARG=1\"",
            "if defined GAME_DIR goto :fika_args",
            "set \"PROBE=%SCRIPT_DIR%\"",
            $"for %%I in ({searchRange}) do call :fika_probe_parent",
            "if not defined GAME_DIR goto :fika_nogame",
            "",
            ":fika_args",
            "set \"EXTRA_ARGS=\"",
            "if defined DIR_ARG shift",
            "",
            ":fika_collect",
            "if \"%~1\"==\"\" goto :fika_run",
            "set \"EXTRA_ARGS=%EXTRA_ARGS% %1\"",
            "shift",
            "goto :fika_collect",
            "",
            ":fika_run",
            "rem One-off override for a host someone wants to watch; the setting in",
            "rem HeadlessConfig.json and the Fika.Headless plugin are not affected by it.",
            "if defined FIKA_NO_MINIMIZE set \"START_FLAGS=\"",
            "set \"GAME_EXE=%GAME_DIR%\\EscapeFromTarkov.exe\"",
            "echo [Headless] profile : %PROFILE_ID%",
            "echo [Headless] backend : %BACKEND_URL%",
            "echo [Headless] display : %DISPLAY_MODE%",
            "echo [Headless] game    : %GAME_EXE%",
            "echo [Headless] window  : %START_FLAGS%",
            "echo [Headless] command : \"%GAME_EXE%\" %GAME_ARGS%%EXTRA_ARGS%",
            "echo [Headless] starting the dedicated host...",
            "echo.",
            "if defined FIKA_WAIT goto :fika_wait",
            "if /I \"%DISPLAY_MODE%\"==\"Console\" goto :fika_console",
            "start \"\" %START_FLAGS% /D \"%GAME_DIR%\" \"%GAME_EXE%\" %GAME_ARGS%%EXTRA_ARGS%",
            "if not errorlevel 1 goto :fika_started",
            "echo [Headless] ERROR: the game process could not be started, errorlevel %ERRORLEVEL%",
            "pause",
            "exit /b 1",
            "",
            ":fika_console",
            "rem Console mode: no game window and no graphics device. The command window is not",
            "rem this process's console (the game is a GUI executable), so the Fika.Headless",
            "rem plugin allocates the single console window the host keeps and mirrors its log",
            "rem into it.",
            "start \"Fika Headless %PROFILE_ID%\" /D \"%GAME_DIR%\" \"%GAME_EXE%\" %GAME_ARGS%%EXTRA_ARGS%",
            "if not errorlevel 1 goto :fika_started",
            "echo [Headless] ERROR: the console host could not be started, errorlevel %ERRORLEVEL%",
            "pause",
            "exit /b 1",
            "",
            ":fika_wait",
            "pushd \"%GAME_DIR%\"",
            "\"%GAME_EXE%\" %GAME_ARGS%%EXTRA_ARGS%",
            "set \"FIKA_EXIT=%ERRORLEVEL%\"",
            "popd",
            "echo [Headless] the host exited with code %FIKA_EXIT%",
            "pause",
            "exit /b %FIKA_EXIT%",
            "",
            ":fika_started",
            "echo [Headless] dedicated host process started.",
            "exit /b 0",
            "",
            ":fika_nogame",
            "echo [Headless] ERROR: EscapeFromTarkov.exe was not found.",
            "echo [Headless]   script folder: %SCRIPT_DIR%",
            $"echo [Headless]   checked FIKA_GAME_DIR, the first argument and {SearchDepth} folders up.",
            "echo [Headless] Pass the folder that contains EscapeFromTarkov.exe, for example:",
            "echo [Headless]   \"%~nx0\" \"path to the game folder\"",
            "echo.",
            "pause",
            "exit /b 1",
            "",
            ":fika_probe_parent",
            "if defined GAME_DIR goto :eof",
            "for %%P in (\"%PROBE%..\") do set \"PARENT=%%~fP\"",
            "if exist \"%PARENT%\\EscapeFromTarkov.exe\" set \"GAME_DIR=%PARENT%\"",
            "set \"PROBE=%PARENT%\\\"",
            "goto :eof",
            ""
        ];

        return string.Join("\r\n", lines);
    }
}
