using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.UI.Matchmaker;

namespace Fika.Core.Main.Utils;

/// <summary>
/// The clock time the game itself pins on some maps ("this map always runs at HH:mm").
///
/// Where the vanilla value lives: the matchmaker's own conditions panel,
/// <see cref="EFT.UI.Matchmaker.LocationConditionsPanel"/>, carries one static DateTime property per
/// such map - the installed 5.0 build has FactoryDayTime, FactoryNightTime and IcebreakerFixedTime
/// (interop Assembly-CSharp.dll, type EFT.UI.Matchmaker.LocationConditionsPanel). The panel feeds
/// that value into the raid time selector, which is why the live game gives a player no usable time
/// choice on those maps. The raid clock itself is only ever told an integer hour
/// (EFT.TimeAndWeatherSettings.HourOfDay), so the hour is the whole of what the game consumes.
///
/// Why this class exists: a Fika raid never runs that panel, so nothing brought the pinned time into
/// the raid and the raid started on whatever the raid settings held - which on this port is usually
/// the -1 ("follow the world clock") the Fika raid settings screen leaves behind. This reads the same
/// native value the panel would have used, by name, so a map the game pins later works without a code
/// change here. See analysis/map-fixed-time/REPORT.md for the evidence and the judging lines.
/// </summary>
internal static class MapFixedTime
{
    /// <summary>
    /// The name the game gives a pinned map time: the location id followed by "FixedTime"
    /// (IcebreakerFixedTime for the location id "Icebreaker"). The lookup is by this name and not by
    /// a per-map list, so no map is hardcoded here.
    /// </summary>
    private const string NativeSuffix = "FixedTime";

    /// <summary>
    /// Explicit fallback for a map whose pinned time cannot be read from the native panel (the UI
    /// type missing, or the property renamed/removed by a game update).
    ///
    /// Deliberately EMPTY. The value exists in exactly one place - the body of the native getter in
    /// GameAssembly.dll - and no decompile of it is available (the 4.0 tree ends before this panel and
    /// 5.0 ships none), so every number written here would be a guess. A guessed time is worse than
    /// the status quo: the raid would silently start at an invented hour. Nothing is lost while the
    /// table is empty - "no pinned time found" means CoopGame keeps the player's own setting, which is
    /// exactly the behaviour before this class existed.
    ///
    /// To fill it, take the hour the diagnostic line prints on that map
    /// ("Raid time: ... fixed=HH:mm (native ...)") and add one entry, for example:
    ///     { "Icebreaker", (19, 0) },
    /// </summary>
    private static readonly Dictionary<string, (int Hour, int Minute)> Fallback =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly struct Entry
    {
        internal readonly bool Found;
        internal readonly int Hour;
        internal readonly int Minute;
        internal readonly string Source;

        internal Entry(bool found, int hour, int minute, string source)
        {
            Found = found;
            Hour = hour;
            Minute = minute;
            Source = source;
        }
    }

    /// <summary>Resolved lookups: the native read runs once per map per process, misses included.</summary>
    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cache is written from whoever creates a raid; the read itself is not thread safe.</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// The pinned time of <paramref name="locationId"/>, or false when the map has none (or this
    /// build cannot read it). Never throws: a raid start must not fail over a diagnostic lookup.
    /// </summary>
    /// <param name="source">Where the value came from, for the diagnostic line: "native &lt;name&gt;", "table" or "none".</param>
    internal static bool TryGet(string locationId, out int hour, out int minute, out string source)
    {
        hour = 0;
        minute = 0;
        source = "none";
        if (string.IsNullOrEmpty(locationId))
        {
            return false;
        }

        Entry entry;
        lock (Gate)
        {
            if (!Cache.TryGetValue(locationId, out entry))
            {
                entry = Resolve(locationId);
                Cache[locationId] = entry;
            }
        }

        hour = entry.Hour;
        minute = entry.Minute;
        source = entry.Source;
        return entry.Found;
    }

    private static Entry Resolve(string locationId)
    {
        if (TryResolveNative(locationId, out var hour, out var minute, out var nativeSource))
        {
            return new Entry(true, hour, minute, nativeSource);
        }

        if (Fallback.TryGetValue(locationId, out var table))
        {
            return new Entry(true, table.Hour, table.Minute, "table");
        }

        return new Entry(false, 0, 0, "none");
    }

    private static bool TryResolveNative(string locationId, out int hour, out int minute, out string source)
    {
        hour = 0;
        minute = 0;
        source = "none";
        var name = locationId + NativeSuffix;
        try
        {
            var panel = typeof(LocationConditionsPanel);

            // The name comparison is case insensitive on purpose: the location id the raid reports
            // ("Icebreaker") and the name the panel uses ("IcebreakerFixedTime") come from two different
            // places and their casing has to agree only on the identity of the map, not on spelling.
            object value = null;
            var found = false;
            foreach (var property in panel.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = property.GetValue(null);
                found = true;
                break;
            }

            if (!found)
            {
                foreach (var candidate in panel.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(candidate.Name, "get_" + name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    value = candidate.Invoke(null, null);
                    found = true;
                    break;
                }
            }

            if (!found || value is not Il2CppSystem.DateTime pinned)
            {
                return false;
            }

            hour = pinned.Hour;
            minute = pinned.Minute;
            source = "native " + name;
            return true;
        }
        catch (Exception exception)
        {
            
            return false;
        }
    }
}
