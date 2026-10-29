// © 2026 Lacyway All Rights Reserved

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Fika.Core.Main.Utils;

namespace Fika.Core.Bundles;

/// <summary>
/// Created by Nexus / pandahhcorp <br/>
/// Refactored by Lacyway to load bundles directly from memory
/// </summary>
internal class InternalBundleLoader
{
    public static InternalBundleLoader Instance { get; private set; }

    private AssetBundle _masterBundle;

    public InternalBundleLoader()
    {
        // Plugin.Load runs on Unity's main thread. Finish loading before any
        // patches or UI consumers can request an asset from this bundle.
        LoadBundles();
        Instance = this;
    }

    private void LoadBundles()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resource = "Fika.Core.Bundles.Files.masterbundle.bundle";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException("Fika master bundle is missing from the plugin", resource);
        using MemoryStream memoryStream = new();
        stream.CopyTo(memoryStream);
        _masterBundle = AssetBundle.LoadFromMemory(memoryStream.ToArray());
        if (_masterBundle == null)
            throw new InvalidOperationException("Unity could not load the Fika master bundle");
    }

    /// <summary>
    /// Loads a Fika asset from the Master Bundle
    /// </summary>
    /// <param name="asset">The <see cref="EFikaAsset"/> type to load</param>
    /// <returns></returns>
    /// <exception cref="NullReferenceException">Master Bundle could not be found</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="EFikaAsset"/> was out of range</exception>
    internal GameObject GetFikaAsset(EFikaAsset asset)
    {
        if (_masterBundle == null)
        {
            throw new NullReferenceException("GetFikaAsset::MasterBundle did not exist!");
        }

        return asset switch
        {
            EFikaAsset.Ping => LoadPrefab("BasePingPrefab.prefab"),
            EFikaAsset.SendItemMenu => LoadPrefab("SendItemMenu.prefab"),
            EFikaAsset.MainMenuUI => LoadPrefab("MainMenuUI.prefab"),
            EFikaAsset.MatchmakerUI => LoadPrefab("NewMatchMakerUI.prefab"),
            EFikaAsset.PlayerUI => LoadPrefab("PlayerFriendlyUI.prefab"),
            EFikaAsset.FreecamUI => LoadPrefab("FreecamUI.prefab"),
            EFikaAsset.ListPlayer => LoadPrefab("ListPlayer.prefab"),
            EFikaAsset.AdminUI => LoadPrefab("AdminSettingsUI.prefab"),
            EFikaAsset.FikaChatUI => LoadPrefab("FikaChatUI.prefab"),
            EFikaAsset.RaidAdminUI => LoadPrefab("RaidAdminUI.prefab"),
            EFikaAsset.LoadingScreenUI => LoadPrefab("LoadingScreenUI.prefab"),
            EFikaAsset.DebugUI => LoadPrefab("DebugUI.prefab"),
            _ => throw new ArgumentOutOfRangeException(nameof(asset), "Invalid type was given")
        };
    }

    /// <summary>
    /// Loads a prefab and reports a missing asset instead of forwarding it to
    /// <c>Cast&lt;T&gt;()</c>.
    /// </summary>
    /// <remarks>
    /// <c>Cast&lt;T&gt;()</c> resolves the il2cpp class of the wrapped pointer. When the bundle
    /// does not contain the asset the returned wrapper carries a null native pointer, and that
    /// class lookup faults inside GameAssembly (0xC0000005, read at address 0x0) instead of
    /// throwing a managed exception - it is not a CLR NullReferenceException and cannot be
    /// caught by the callers. Returning null makes the consumer skip its own feature.
    /// </remarks>
    private GameObject LoadPrefab(string name)
    {
        var loaded = _masterBundle.LoadAsset(name, Il2CppInterop.Runtime.Il2CppType.Of<GameObject>());
        if (loaded is null || loaded.Pointer == IntPtr.Zero)
        {
            FikaGlobals.LogError($"[BundleLoader] '{name}' is missing from the Fika master bundle; the feature that needs it is skipped");
            return null;
        }

        return loaded.Cast<GameObject>();
    }

    /// <summary>
    /// Sprite counterpart of <see cref="LoadPrefab(string)"/>.
    /// </summary>
    private Sprite LoadSprite(string name)
    {
        var loaded = _masterBundle.LoadAsset(name, Il2CppInterop.Runtime.Il2CppType.Of<Sprite>());
        if (loaded is null || loaded.Pointer == IntPtr.Zero)
        {
            FikaGlobals.LogError($"[BundleLoader] '{name}' is missing from the Fika master bundle; the feature that needs it is skipped");
            return null;
        }

        return loaded.Cast<Sprite>();
    }

    /// <summary>
    /// Loads all <see cref="EFikaSprite"/> into memory and caches them
    /// </summary>
    /// <returns>All sprites in a <see cref="Dictionary{TKey, TValue}"/></returns>
    internal Dictionary<EFikaSprite, Sprite> GetFikaSprites()
    {
        Dictionary<EFikaSprite, Sprite> sprites = [];

        sprites.Add(EFikaSprite.PingPoint, LoadSprite("PingPoint.png"));
        sprites.Add(EFikaSprite.PingPlayer, LoadSprite("PingPlayer.png"));
        sprites.Add(EFikaSprite.PingLootableContainer, LoadSprite("PingLootableContainer.png"));
        sprites.Add(EFikaSprite.PingDoor, LoadSprite("PingDoor.png"));
        sprites.Add(EFikaSprite.PingDeadBody, LoadSprite("PingDeadBody.png"));
        sprites.Add(EFikaSprite.PingLootItem, LoadSprite("PingLootItem.png"));

        return sprites;
    }

    public enum EFikaSprite
    {
        PingPoint,
        PingPlayer,
        PingLootableContainer,
        PingDoor,
        PingDeadBody,
        PingLootItem
    }

    public enum EFikaAsset
    {
        Ping,
        SendItemMenu,
        MainMenuUI,
        MatchmakerUI,
        PlayerUI,
        FreecamUI,
        ListPlayer,
        AdminUI,
        FikaChatUI,
        RaidAdminUI,
        LoadingScreenUI,
        DebugUI
    }
}
