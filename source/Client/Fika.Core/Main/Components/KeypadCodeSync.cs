using System;
using System.Collections.Generic;
using EFT;
using EFT.Passcodes;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Fika.Core.Main.Components;

/// <summary>
/// BU (analysis/icebreaker-keypad-3): the raid's passcode set, made by the game and checked by the game.
///
/// The third round of the keypad repair. The first two rounds left the decision on the HOST (the client's
/// code was handed to the host and the host answered) and the client's world carried a controller with no
/// code table at all, so the two machines of one raid never held the same set: the client typed what its own
/// wall hints showed while the host compared against its own, and every attempt came back as 'wrong'
/// (r33: stage=result success=no x2, live diagnostics log 27220/27221).
///
/// The native chain of one raid is this (members from the 47888 interop, file:line in the report):
///   * the DATA is the location's <c>PasscodeLocationSettings.Entries</c> (id -> passcode list id);
///   * the CODES are drawn once per raid by <c>PasscodeGenerator.GeneratePasscodes(entries)</c>, which
///     resolves every list id against <c>GlobalConfiguration.PasscodeSettings</c> and takes one random
///     entry of that list (the optional icebreaker keypads draw out of a 60000 entry list, the quest one
///     has a single code) - this is what the user calls 'the code is generated inside the raid';
///   * the HINTS are <c>PasscodeController.GenerateHints(codes)</c>, applied to the scene's hint objects
///     by <c>ClientPasscodeController.SetupHints(hints)</c>;
///   * the CHECK happens inside <c>ClientLocalPasscodeController.HandleSubmitPasscode</c> against that
///     machine's own <c>_generatedPasscodes</c>, and it is the call that unlocks the door.
///
/// So a machine can only be right about a code if it holds the very same table. This class gives the raid
/// exactly one table: the HOST generates it with the game's own generator (or keeps the one the native
/// pipeline already made) and Fika carries that table to the clients - it never decides about a code.
/// A client builds the native LOCAL controller around the received table, so from then on the whole
/// submit -> check -> unlock -> panel path of that client is the single player one, with no network round
/// trip and no use of the relay (which stays as the fallback for the window in which no table arrived).
/// The host builds the same controller when the native pipeline of this build left its world without one,
/// which is what made the host's own keypad answer 'Tried to enter code on location without passcodes'.
/// </summary>
internal static class KeypadCodeSync
{
    /// <summary>Seconds between two attempts to build a controller (the location/scene may not be up yet).</summary>
    private const float RetrySeconds = 2f;

    /// <summary>How often the failure line repeats while the state stays broken.</summary>
    private const float FailureLineSeconds = 10f;

    private static bool _hostCodesReady;
    private static int _hostCodeCount;
    private static bool _clientHasHostCodes;
    private static bool _clientApplied;
    private static bool _loggedWaiting;
    private static float _nextAttempt;
    private static float _nextFailureLine;
    private static int _failures;
    private static int _terminalPopulations;

    private static Dictionary<string, string> _hostEntries;
    private static Dictionary<string, string> _hostCodes;
    private static Dictionary<string, string[]> _hostHints;

    /// <summary>The local controller this port installed on a client, so RaidInteractionSync can tell it from
    /// a native one it has to swap to the network shape.</summary>
    private static IntPtr _clientLocalController;

    /// <summary>This machine holds the raid's code table (host: its own, client: the host's).</summary>
    internal static bool Ready => FikaBackendUtils.IsServer ? _hostCodesReady : _clientApplied;

    /// <summary>A client that has not received the host's table yet keeps asking for the snapshot.</summary>
    internal static bool NeedsHostCodes => FikaBackendUtils.IsClient && !_clientHasHostCodes;

    /// <summary>The host's table as the wire payload, or null while it does not exist yet.</summary>
    internal static Dictionary<string, string> HostCodes() => _hostCodesReady ? _hostCodes : null;

    internal static void Reset()
    {
        _hostCodesReady = false;
        _hostCodeCount = 0;
        _clientHasHostCodes = false;
        _clientApplied = false;
        _loggedWaiting = false;
        _nextAttempt = 0f;
        _nextFailureLine = 0f;
        _failures = 0;
        _terminalPopulations = 0;
        _hostEntries = null;
        _hostCodes = null;
        _hostHints = null;
        _clientLocalController = IntPtr.Zero;
    }

    /// <summary>True when this client's world holds the local controller built around the host's table.</summary>
    internal static bool OwnsLocalController(ClientLocalPasscodeController controller)
    {
        try
        {
            return _clientLocalController != IntPtr.Zero && controller != null
                && controller.Pointer == _clientLocalController;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The host's answer reached this machine. Its code table is the raid's table; the entry ids and the
    /// hint set travel with it so a client needs nothing of its own to build the native controller.
    /// </summary>
    internal static void NoteHostSnapshot(Dictionary<string, string> codes, Dictionary<string, string> entries,
        Dictionary<string, string[]> hints)
    {
        if (FikaBackendUtils.IsServer) return;
        if (entries != null && entries.Count > 0) _hostEntries = entries;
        if (hints != null && hints.Count > 0) _hostHints = hints;
        if (codes == null || codes.Count == 0)
        {
            // An empty snapshot is legal while the host has not drawn its codes yet; the request loop of
            // RaidInteractionSync keeps asking and the wait is written down once.
            if (!_loggedWaiting && !_clientApplied)
            {
                _loggedWaiting = true;
                
            }

            return;
        }

        var changed = !_clientHasHostCodes || _hostCodes == null || _hostCodes.Count != codes.Count;
        _hostCodes = codes;
        _clientHasHostCodes = true;
        if (changed)
        {
            
        }
    }

    /// <summary>
    /// One step of the per-frame convergence of this raid's keypad state. Runs on the host and on every
    /// client, inside the raid only (the gate of RaidInteractionSync already keeps us out of foreign raids).
    /// </summary>
    internal static void Tick(GameWorld world)
    {
        if (world == null) return;
        try
        {
            if (FikaBackendUtils.IsServer) EnsureHostTable(world);
            else if (FikaBackendUtils.IsClient) EnsureClientTable(world);
        }
        catch (Exception exception)
        {
            Report("tick", -1, -1, "failed", "building the raid's passcode state threw " + exception.GetType().Name);
        }
    }

    // ---- host: the raid's table is drawn here ----------------------------------------------------------

    private static void EnsureHostTable(GameWorld world)
    {
        // The native pipeline may already have made the controller (it is the single player path of a
        // host's offline raid). Its table is the raid's table and is never redrawn - a redraw would move
        // the code under the players' feet.
        if (world.PasscodeController?.TryCast<ClientLocalPasscodeController>() is { } native)
        {
            Populate(native);
            if (HasCodes(native))
            {
                if (!_hostCodesReady)
                {
                    _hostCodesReady = true;
                    _hostCodeCount = native._generatedPasscodes.Count;
                    _hostCodes = CodesOf(native);
                    Report("host-native", native._passcodeLocationSettings?.Entries?.Length ?? 0,
                        native._passcodeTerminals?.Count ?? 0, "kept-native",
                        "this host's world already carries the game's own local passcode controller, so its table is the raid's table");
                    RaidInteractionSync.PushPasscodeSnapshot();
                }

                return;
            }

            // A native controller without a table (the state the r33 host log shows as
            // 'Tried to enter code on location without passcodes'): draw it with the game's own generator.
            if (TryGenerate(native, world, "host-native-repaired")) return;
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextAttempt) return;
        _nextAttempt = now + RetrySeconds;

        var settings = LocationSettings();
        var entries = settings?.Entries;
        var listIds = ListIds(entries);
        if (entries == null || entries.Length == 0)
        {
            // The location of this machine carries no entries: fall back to the keypads of this scene with
            // an empty list id (the generator then has no list to draw from and the doors keep their old
            // behaviour). The line names it so the next round can judge it from the log.
            var scene = new PasscodeLocationSettings { Entries = FromSceneIds() };
            settings = scene;
            entries = scene.Entries;
        }

        var controller = new ClientLocalPasscodeController(settings);
        var previous = world.PasscodeController;
        world.PasscodeController = controller;
        Populate(controller);
        if (!TryGenerate(controller, world, "host-created"))
        {
            // No table could be drawn (this machine's location carries no entry at all). The world keeps
            // whichever controller it had, so the state of the host stays readable in the log.
            world.PasscodeController = previous;
            controller.Dispose();
            return;
        }

        previous?.Dispose();
        
    }

    /// <summary>Draws the table with the game's own generator and applies the game's own hints.</summary>
    private static bool TryGenerate(ClientLocalPasscodeController controller, GameWorld world, string source)
    {
        var settings = controller._passcodeLocationSettings;
        var entries = settings?.Entries;
        if (entries == null || entries.Length == 0)
        {
            Report(source, 0, controller._passcodeTerminals?.Count ?? 0, "no-entries",
                "this machine's location carries no passcode entry at all, so there is nothing to draw a code from"
                + " (the keypads of this map stay unusable here)");
            return false;
        }

        var codes = PasscodeGenerator.GeneratePasscodes(entries);
        if (codes == null || codes.Count == 0)
        {
            Report(source, entries.Length, controller._passcodeTerminals?.Count ?? 0, "no-codes",
                "the game's own generator returned no code for this location's entries");
            return false;
        }

        controller._generatedPasscodes = codes;
        var hints = controller.GenerateHints(codes);
        controller._generatedHints = hints;
        if (hints != null && hints.Count > 0) controller.SetupHints(hints);

        _hostCodesReady = true;
        _hostCodeCount = codes.Count;
        _hostCodes = CodesOf(controller);
        Report(source, entries.Length, controller._passcodeTerminals?.Count ?? 0, "generated",
            "this host drew the raid's codes with the game's own PasscodeGenerator and applied the game's own hints"
            + " (hints=" + NativeCount(hints) + ")");
        // The table of this raid exists now: every peer is handed it without waiting for its next retry.
        RaidInteractionSync.PushPasscodeSnapshot();
        return true;
    }

    // ---- client: the native local controller around the host's table ------------------------------------

    private static void EnsureClientTable(GameWorld world)
    {
        if (_clientApplied)
        {
            // The scene of a client loads after its world: the keypads and hint groups are bound late.
            if (world.PasscodeController is { } applied) Populate(applied);
            return;
        }

        if (!_clientHasHostCodes || _hostCodes == null || _hostCodes.Count == 0) return;

        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextAttempt) return;
        _nextAttempt = now + RetrySeconds;

        var settings = ClientSettings();
        if (settings?.Entries == null || settings.Entries.Length == 0)
        {
            Report("client", 0, 0, "no-entries",
                "this client could not name a single keypad of its location (neither its own location data nor the"
                + " host's snapshot carried an entry), so no native controller is built here");
            return;
        }

        var controller = new ClientLocalPasscodeController(settings);
        Populate(controller);
        controller._generatedPasscodes = ToNativeCodes(_hostCodes);
        var hints = _hostHints != null && _hostHints.Count > 0
            ? ToNativeHints(_hostHints)
            : controller.GenerateHints(controller._generatedPasscodes);
        controller._generatedHints = hints;
        if (hints != null && hints.Count > 0) controller.SetupHints(hints);

        var previous = world.PasscodeController;
        world.PasscodeController = controller;
        previous?.Dispose();
        _clientLocalController = controller.Pointer;
        _clientApplied = true;
        Report("client", settings.Entries.Length, controller._passcodeTerminals?.Count ?? 0, "local-native",
            "this client now checks codes with the game's own local controller on the host's table"
            + " (controller=" + ControllerKind(previous) + " -> local, hints=" + NativeCount(hints)
            + "): no relay and no host answer is needed for a code any more");
    }

    /// <summary>
    /// The entries a client builds its controller from: its own location data first (the same file the
    /// host reads), the host's snapshot second, the keypads of this scene last.
    /// </summary>
    private static PasscodeLocationSettings ClientSettings()
    {
        var own = LocationSettings();
        if (own?.Entries != null && own.Entries.Length > 0) return own;
        if (_hostEntries != null && _hostEntries.Count > 0) return FromEntries(_hostEntries);
        return new PasscodeLocationSettings { Entries = FromSceneIds() };
    }

    // ---- the native pieces shared by both roles ---------------------------------------------------------

    /// <summary>
    /// The keypads and hint groups of this machine's scene, through the native population call of the
    /// controller. A client's scene loads after its world, so this is retried until the map really has
    /// them - the r33 log's 'terminals=0' is exactly a controller that was never bound to its scene.
    /// </summary>
    private static void Populate(PasscodeController controller)
    {
        if (controller == null) return;
        if ((controller._passcodeTerminals?.Count ?? 0) > 0) return;
        try
        {
            controller.GetAllTerminals();
            controller.GetAllHintGroups();
            if ((controller._passcodeTerminals?.Count ?? 0) == 0)
            {
                var map = SceneTerminalMap();
                if (map.Count > 0) controller._passcodeTerminals = map;
            }

            if ((controller._passcodeTerminals?.Count ?? 0) > 0 && _terminalPopulations++ < 4)
            {
                
            }
        }
        catch (Exception exception)
        {
            Report("keys", -1, -1, "failed", "binding the scene's keypads threw " + exception.GetType().Name);
        }
    }

    private static bool HasCodes(ClientLocalPasscodeController controller)
    {
        try
        {
            return controller._generatedPasscodes != null && controller._generatedPasscodes.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Dictionary<string, string> CodesOf(ClientLocalPasscodeController controller)
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (controller._generatedPasscodes == null) return codes;
            foreach (var pair in controller._generatedPasscodes)
            {
                if (pair.Key == null) continue;
                codes[pair.Key] = pair.Value ?? string.Empty;
            }
        }
        catch (Exception)
        {
        }

        return codes;
    }

    /// <summary>
    /// The native verdict of a submit this machine handled itself: whether the table of this raid holds this
    /// very code for the keypad. It is read (not decided) and only decides whether the host has to be told
    /// about the attempt - the door of this machine was opened by the game's own controller, and the host
    /// runs the same check on its own copy before anything changes there.
    /// </summary>
    internal static bool MatchesLocal(ClientPasscodeController controller, string passcodeId, string input)
    {
        try
        {
            if (controller?.TryCast<ClientLocalPasscodeController>() is not { } local) return false;
            if (string.IsNullOrEmpty(passcodeId) || string.IsNullOrEmpty(input)) return false;
            var codes = local._generatedPasscodes;
            return codes != null && codes.TryGetValue(passcodeId, out var expected)
                && string.Equals(expected, input, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when this machine's world checks codes itself instead of asking the host (the wanted end state).</summary>
    internal static bool IsLocal(ClientPasscodeController controller)
    {
        try
        {
            return controller?.TryCast<ClientLocalPasscodeController>() != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The raid model of this machine, the one the game built the world from.</summary>
    private static PasscodeLocationSettings LocationSettings()
    {
        try
        {
            var game = FikaSingleton<IFikaGame>.Instance;
            var location = game?.GameController?.Location;
            return location?.PasscodeLocationSettings;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PasscodeLocationSettings FromEntries(Dictionary<string, string> entries)
    {
        var list = new List<PasscodeEntry>(entries.Count);
        foreach (var pair in entries)
        {
            if (string.IsNullOrEmpty(pair.Key)) continue;
            list.Add(new PasscodeEntry { PasscodeId = pair.Key, PasscodeListId = pair.Value ?? string.Empty });
        }

        return new PasscodeLocationSettings { Entries = new Il2CppReferenceArray<PasscodeEntry>(list.ToArray()) };
    }

    private static Il2CppReferenceArray<PasscodeEntry> FromSceneIds()
    {
        var ids = new List<string>();
        try
        {
            foreach (var terminal in LocationScene.GetAllObjects<PasscodeTerminal>(true).AsManaged())
            {
                if (terminal == null) continue;
                var id = terminal.PasscodeId;
                if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
            }
        }
        catch (Exception)
        {
        }

        var entries = new List<PasscodeEntry>(ids.Count);
        foreach (var id in ids) entries.Add(new PasscodeEntry { PasscodeId = id, PasscodeListId = string.Empty });
        return new Il2CppReferenceArray<PasscodeEntry>(entries.ToArray());
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<PasscodeTerminal>>
        SceneTerminalMap()
    {
        var map = new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<PasscodeTerminal>>();
        try
        {
            foreach (var terminal in LocationScene.GetAllObjects<PasscodeTerminal>(true).AsManaged())
            {
                if (terminal == null || string.IsNullOrEmpty(terminal.PasscodeId)) continue;
                if (!map.ContainsKey(terminal.PasscodeId))
                {
                    map.Add(terminal.PasscodeId, new Il2CppSystem.Collections.Generic.List<PasscodeTerminal>());
                }

                map[terminal.PasscodeId].Add(terminal);
            }
        }
        catch (Exception)
        {
        }

        return map;
    }

    private static string ListIds(Il2CppReferenceArray<PasscodeEntry> entries)
    {
        if (entries == null || entries.Length == 0) return "0";
        var text = new System.Text.StringBuilder();
        var named = 0;
        for (var i = 0; i < entries.Length && i < 8; i++)
        {
            var entry = entries[i];
            if (entry == null) continue;
            if (named++ > 0) text.Append(',');
            text.Append(entry.PasscodeId).Append("->").Append(string.IsNullOrEmpty(entry.PasscodeListId) ? "none" : entry.PasscodeListId);
        }

        return entries.Length + (named > 0 ? "[" + text + "]" : string.Empty);
    }

    private static int Count<TValue>(Dictionary<string, TValue> map) => map?.Count ?? 0;

    /// <summary>The count of an Il2Cpp dictionary (the shape the native hints and codes use).</summary>
    private static int NativeCount<TValue>(Il2CppSystem.Collections.Generic.Dictionary<string, TValue> map)
        => map?.Count ?? 0;

    private static string ControllerKind(PasscodeController controller)
    {
        try
        {
            if (controller == null) return "none";
            if (controller.TryCast<ClientNetworkPasscodeController>() != null) return "network";
            if (controller.TryCast<ClientLocalPasscodeController>() != null) return "local";
            return "other";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<string, string> ToNativeCodes(Dictionary<string, string> codes)
    {
        var native = new Il2CppSystem.Collections.Generic.Dictionary<string, string>();
        foreach (var pair in codes)
        {
            if (string.IsNullOrEmpty(pair.Key)) continue;
            native[pair.Key] = pair.Value ?? string.Empty;
        }

        return native;
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<string>>
        ToNativeHints(Dictionary<string, string[]> hints)
    {
        var native = new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.List<string>>();
        foreach (var pair in hints)
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;
            var values = new Il2CppSystem.Collections.Generic.List<string>();
            for (var i = 0; i < pair.Value.Length; i++)
            {
                if (pair.Value[i] == null) continue;
                values.Add(pair.Value[i]);
            }

            native[pair.Key] = values;
        }

        return native;
    }

    /// <summary>One line per distinct state of the keypad code layer, never more than one per 10 s.</summary>
    private static void Report(string source, int entries, int terminals, string result, string reason)
    {
        var now = UnityEngine.Time.unscaledTime;
        if (result != "generated" && result != "kept-native" && result != "local-native" && now < _nextFailureLine) return;
        _nextFailureLine = now + FailureLineSeconds;
        _failures++;
        
    }
}
