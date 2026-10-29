using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using Dissonance;
using Dissonance.Integrations.MirrorIgnorance;
using EFT;
using EFT.Settings;
using Fika.Core.Main.Utils;
using UnityEngine.SceneManagement;

namespace Fika.Core.Networking.VOIP;

/// <summary>A raid transport owns its voice scene, including unfinished scene loads.</summary>
internal sealed class FikaVoiceSession(IFikaNetworkManager owner, bool headless) : IDisposable
{
    // Unity scene loading cannot be cancelled. A cancelled load keeps this lease
    // until it finishes and is unloaded, so the next raid cannot adopt its objects.
    private static readonly SemaphoreSlim SceneLease = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private FikaCommsNetwork _network;
    private MirrorIgnoranceCommsNetwork _native;
    private Scene _scene;
    private bool _ownsScene;
    private bool _ownsLease;
    private bool _disposed;
    private bool _ready;
    private Task _startup;
    private Task _cleanup;

    internal bool IsAlive => !_disposed && _ready && _network != null && _network.IsActive;
    internal bool CanReuse => !_disposed && (_startup == null || !_startup.IsCompleted || IsAlive);

    internal Task InitializeAsync() => MainThreadDispatcher.RunAsync(async () =>
    {
        if (_disposed) return;
        _startup ??= StartAsync();
        try
        {
            await _startup.WaitAsync(TimeSpan.FromSeconds(60), _lifetime.Token);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception)
        {
            FikaGlobals.LogError($"VOIP initialization failed: {exception}");
            if (!headless) Singleton<SettingsManager>.Instance.Sound.Controller.VoipDisabledByInitializationFail();
            Dispose();
        }
    });

    private async Task StartAsync()
    {
        try
        {
            await SceneLease.WaitAsync(_lifetime.Token);
            _ownsLease = true;
            _lifetime.Token.ThrowIfCancellationRequested();
            var existingNetworks = new HashSet<IntPtr>();
            foreach (var native in UnityEngine.Object.FindObjectsOfType<MirrorIgnoranceCommsNetwork>())
                existingNetworks.Add(native.Pointer);
            var existingScenes = new HashSet<int>();
            for (var i = 0; i < SceneManager.sceneCount; i++) existingScenes.Add(SceneManager.GetSceneAt(i).handle);

            if (!headless)
            {
                Singleton<SettingsManager>.Instance.Sound.Controller.ResetVoipDisabledReason();
                DissonanceComms.ClientPlayerId = FikaGlobals.GetProfile(owner.RaidSide == ESideType.Savage).ProfileId;
            }
            await AssetsManagerExtension.LoadScene(EFT.Assets.Manager, Scenes.DissonanceSetupScene,
                LoadSceneMode.Additive).AsManaged();

            // Capture the scene before checking cancellation; a late scene still
            // belongs to this session and must not initialize in the next raid.
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!existingScenes.Contains(scene.handle) &&
                    (scene.path == Scenes.DissonanceSetupScene.path ||
                     scene.name == Scenes.DissonanceSetupScene.FileName ||
                     scene.name == System.IO.Path.GetFileNameWithoutExtension(Scenes.DissonanceSetupScene.path)))
                {
                    _scene = scene;
                    _ownsScene = true;
                    break;
                }
            }
            // Scene loading may complete before its voice prefab becomes active.
            // Keep the lease until the new component appears or this session is cancelled.
            while (_native == null)
            {
                foreach (var native in UnityEngine.Object.FindObjectsOfType<MirrorIgnoranceCommsNetwork>())
                {
                    if (existingNetworks.Contains(native.Pointer)) continue;
                    _native = native;
                    if (!_ownsScene && !existingScenes.Contains(native.gameObject.scene.handle) &&
                        native.gameObject.scene.name != "DontDestroyOnLoad")
                    {
                        _scene = native.gameObject.scene;
                        _ownsScene = true;
                    }
                    break;
                }
                if (_native != null) break;
                _lifetime.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            _lifetime.Token.ThrowIfCancellationRequested();

            _network = FikaCommsNetwork.Attach(_native, owner, headless);
            var comms = _native.GetComponent<DissonanceComms>();
            if (comms == null) throw new InvalidOperationException("The voice scene has no DissonanceComms.");
            comms.IsMuted = true;
            comms.Initialize();
            while ((owner is FikaServer && _network.Server == null) || (!headless && _network.Client == null))
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (_network == null || !_network.IsActive) throw new InvalidOperationException("Voice scene was destroyed during initialization.");
                await Task.Yield();
            }
            _lifetime.Token.ThrowIfCancellationRequested();
            _ready = true;
        }
        finally
        {
            if (!_ready || _disposed) await CleanupAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _network?.Shutdown();
        if (_startup == null || _startup.IsCompleted) _ = CleanupAsync();
    }

    private Task CleanupAsync() => _cleanup ??= MainThreadDispatcher.RunAsync(ReleaseAsync);

    private async Task ReleaseAsync()
    {
        try
        {
            _network?.Shutdown();
            if (_native != null)
            {
                var comms = _native.GetComponent<DissonanceComms>();
                if (comms != null)
                {
                    comms.CancelInvoke(nameof(DissonanceComms.Initialize));
                    comms.IsMuted = true;
                    comms.MuteAllRemoteVoices = true;
                }
                // Also handles a setup prefab which moved to DontDestroyOnLoad.
                UnityEngine.Object.Destroy(_native.gameObject);
                // Destroy is deferred until the end of the frame, including
                // clearing Dissonance's native singleton in OnDestroy.
                while (_native != null) await Task.Yield();
            }
            if (_ownsScene && _scene.IsValid() && _scene.isLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(_scene);
                while (unload != null && !unload.isDone) await Task.Yield();
            }
        }
        catch (Exception exception) { FikaGlobals.LogError($"VOIP scene cleanup failed: {exception}"); }
        finally
        {
            if (_ownsLease)
            {
                _ownsLease = false;
                SceneLease.Release();
            }
        }
    }
}
