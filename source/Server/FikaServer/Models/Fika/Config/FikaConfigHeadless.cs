using System.Text.Json.Serialization;

namespace FikaServer.Models.Fika.Config;

public record FikaConfigHeadless
{
    [JsonPropertyName("profiles")]
    public FikaConfigHeadlessProfiles Profiles { get; set; } = new();

    [JsonPropertyName("scripts")]
    public FikaConfigHeadlessScripts Scripts { get; set; } = new();

    [JsonPropertyName("display")]
    public FikaConfigHeadlessDisplay Display { get; set; } = new();

    [JsonPropertyName("setLevelToAverageOfLobby")]
    public bool SetLevelToAverageOfLobby { get; set; } = true;

    [JsonPropertyName("restartAfterAmountOfRaids")]
    public int RestartAfterAmountOfRaids { get; set; } = 1;
}

public record FikaConfigHeadlessProfiles
{
    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("aliases")]
    public Dictionary<string, string> Aliases { get; set; } = [];
}

public record FikaConfigHeadlessScripts
{
    [JsonPropertyName("generate")]
    public bool Generate { get; set; } = true;

    [JsonPropertyName("forceIp")]
    public string ForceIp { get; set; } = "https://127.0.0.1:6969";
}

/// <summary>
/// How a dedicated host presents itself on the machine it runs on. The value is rendered into
/// <c>StartHeadless.cmd</c> and written into <c>HeadlessConfig.json</c>, so the launcher and the
/// Fika.Headless plugin always agree.
/// </summary>
public record FikaConfigHeadlessDisplay
{
    /// <summary>
    /// <c>Console</c>: start the host with <c>-batchmode -nographics</c> - no game window, no
    /// graphics device, so no shader is compiled and no draw call is submitted; only the console
    /// and the log remain. <c>Hidden</c>: the historical windowed host, started minimised/hidden
    /// with the graphics patches still tearing down every renderer.
    /// <para>
    /// Absent (<c>null</c>) means "not configured here": a profile whose
    /// <c>HeadlessConfig.json</c> already names a mode keeps it, every other profile uses
    /// <c>Console</c>. When this key is present it wins for all profiles, so the launch helpers
    /// the server rewrites on startup always match this file.
    /// </para>
    /// </summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }
}

/// <summary>The accepted <see cref="FikaConfigHeadlessDisplay.Mode"/> values.</summary>
public static class HeadlessDisplayModes
{
    /// <summary>No client picture, no shaders, no GPU: <c>-batchmode -nographics</c>.</summary>
    public const string Console = "Console";

    /// <summary>The historical host: a real device with a hidden/minimised window.</summary>
    public const string Hidden = "Hidden";

    /// <summary>Null when <paramref name="value"/> does not name a mode.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (string.Equals(value, Console, StringComparison.OrdinalIgnoreCase))
        {
            return Console;
        }

        if (string.Equals(value, Hidden, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Windowed", StringComparison.OrdinalIgnoreCase))
        {
            return Hidden;
        }

        return null;
    }
}
