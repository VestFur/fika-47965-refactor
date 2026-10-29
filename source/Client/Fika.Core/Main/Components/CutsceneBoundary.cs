using System;
using Comfort.Common;
using Fika.Core.Main.Utils;
using EFT;
using UnityEngine;

using CommonAssets.Scripts.Cutscenes;

namespace Fika.Core.Main.Components;

/// <summary>
/// (2026-10-18 refactor W3-cutscene, plan B) Minimal cutscene bridge.
/// A Fika raid needs three facts from Fika here: both native controllers exist, both know the map's
/// MapTimelinesBank (without it the native StartCutscene throws), and neither runs in offline mode.
/// Which process plays, who may skip and how a scene ends stays with the game.
/// </summary>
internal static class CutsceneBoundary
{
    internal readonly struct SceneSample
    {
        public readonly bool Director;
        public readonly bool Playing;
        public readonly string Scene;
        public readonly float Time;
        public readonly bool Visible;

        public SceneSample(bool director, bool playing, string scene, float time, bool visible)
        {
            Director = director;
            Playing = playing;
            Scene = scene;
            Time = time;
            Visible = visible;
        }
    }

    private static nint _preparedWorld = -1;
    private static MapTimelinesBank _bank;
    // (2026-10-19 hotfix) The bank lookup is a full scene scan (FindObjectOfType). It must not run per frame:
    // the reset build called it on every Tick until both controllers had a bank, which is the in-raid frame
    // drop the user reported. Bounded attempts with a growing interval instead.
    private static float _nextBankAttempt;
    private static int _bankAttempts;
    private const int MaxBankAttempts = 6;

    internal static bool JoinSharedCutscene => true;

    internal static void Reset()
    {
        _preparedWorld = -1;
        _startupDispatched = false;
        _bank = null;
    }

    internal static void InitCutsceneControllers(GameWorld world) => CreateCutsceneControllers(world, "InitCutsceneControllers", false);

    internal static void CreateCutsceneControllers(GameWorld world, string source, bool viaNativePosition)
    {
        _ = source;
        _ = viaNativePosition;
        if (world == null)
        {
            return;
        }

        try
        {
            if (world.Pointer == _preparedWorld)
            {
                return;
            }

            _preparedWorld = world.Pointer;

            if (world.CutscenesClientController == null)
            {
                world.CutscenesClientController = new CutscenesClientController();
            }

            if (world.CutscenesServerController == null)
            {
                world.CutscenesServerController = new CutscenesServerController();
            }

            try { world.CutscenesClientController._offlineMode = false; } catch (Exception) { }
            try { world.CutscenesServerController._offlineMode = false; } catch (Exception) { }

            // (2026-10-19) The controllers link themselves: the native FindMapTimelinesBank is what wires the
            // timeline bank and the event subscriptions a process needs. Calling it is the whole "setup" the
            // hand-written bank assignment was imitating.
            try { world.CutscenesClientController.FindMapTimelinesBank(); }
            catch (Exception exception) { FikaGlobals.LogWarning("[CutsceneBoundary] client FindMapTimelinesBank failed: " + exception.Message); }
            try { world.CutscenesServerController.FindMapTimelinesBank(); }
            catch (Exception exception) { FikaGlobals.LogWarning("[CutsceneBoundary] server FindMapTimelinesBank failed: " + exception.Message); }

            LinkBank(world);
        }
        catch (Exception)
        {
        }
    }

    internal static void Tick(GameWorld world)
    {
        if (world == null)
        {
            return;
        }

        if (_bank != null || _bankAttempts >= MaxBankAttempts)
        {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextBankAttempt)
        {
            return;
        }

        _nextBankAttempt = now + 1f + _bankAttempts;
        _bankAttempts++;

        LinkBank(world);
    }

    private static int _pumpFrame = -1;
    private static int _pumpProcess;
    private static float _nextPump;
    private static bool _startupDispatched;
    private static float _lastElapsed = -1f;

    /// <summary>
    /// (2026-10-19) The minimal playback pump. The native controllers have no managed caller for OnUpdate on
    /// this runtime (interop reports CallerCount 0), so a process Fika starts is never advanced by the engine:
    /// the subtitles/UI state is switched but the timeline never plays, which is the opening Terminal scene
    /// showing text over the spawn view. The pump advances the client controller when a start is pending and
    /// stops touching it as soon as the engine advances the process on its own.
    /// </summary>
    internal static void TickPlayback(GameWorld world)
    {
        try
        {
            if (world == null)
            {
                return;
            }

            var client = world.CutscenesClientController;
            if (client == null || client.Pointer == IntPtr.Zero || client._timelinesMapBank == null)
            {
                return;
            }

            var now = UnityEngine.Time.unscaledTime;
            var pid = client._cutsceneProcessId;

            if (pid != 0 && pid != _pumpProcess)
            {
                _pumpProcess = pid;
                _lastElapsed = -1f;
                PumpClientOnce(client);
                return;
            }

            if (pid == 0)
            {
                _pumpProcess = 0;
                if (!client._needStartTimeline && client._waitFramesLeft <= 0)
                {
                    return;
                }

                if (now < _nextPump)
                {
                    return;
                }

                _nextPump = now + 0.25f;
                PumpClientOnce(client);
                return;
            }

            if (now < _nextPump)
            {
                return;
            }

            _nextPump = now + 0.25f;

            var server = world.CutscenesServerController;
            if (server?._cutsceneProcesses != null && server._cutsceneProcesses.TryGetValue(pid, out var process) && process != null)
            {
                var elapsed = process.afterStartTimeElapsed;
                if (_lastElapsed >= 0f && System.Math.Abs(elapsed - _lastElapsed) > 0.0001f)
                {
                    _lastElapsed = elapsed;
                    return;
                }

                _lastElapsed = elapsed;
            }

            PumpClientOnce(client);
        }
        catch (Exception)
        {
        }
    }

    private static void PumpClientOnce(CutscenesClientController client)
    {
        if (UnityEngine.Time.frameCount == _pumpFrame)
        {
            return;
        }

        _pumpFrame = UnityEngine.Time.frameCount;
        try { client.OnUpdate(UnityEngine.Time.deltaTime); } catch (Exception) { }
    }

    private static void LinkBank(GameWorld world)
    {
        try
        {
            if (_bank == null)
            {
                _bank = UnityEngine.Object.FindObjectOfType<MapTimelinesBank>();
            }

            if (_bank == null)
            {
                return;
            }

            var client = world.CutscenesClientController;
            if (client != null && client._timelinesMapBank == null)
            {
                client._timelinesMapBank = _bank;
            }

            var server = world.CutscenesServerController;
            if (server != null && server._timelinesMapBank == null)
            {
                server._timelinesMapBank = _bank;
            }
        }
        catch (Exception)
        {
        }
    }

    internal static SceneSample SampleSceneOf(string cutscene) => new(true, true, cutscene, 0f, true);

    internal static float TickSceneOf() => 0f;

    internal static string StopSceneOf(string cutscene) => "native";

    internal static string RescueSceneOf(string cutscene) => "native";
}
