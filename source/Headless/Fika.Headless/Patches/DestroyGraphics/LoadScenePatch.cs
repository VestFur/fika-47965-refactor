using BSG.Unity.Wires;
using EFT.Interactive;
using SPTushonka.Reflection.Patching;
using HarmonyLib;
using Il2CppInterop.Runtime;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using System;
using TMPro;
using System.Collections;
using System.Reflection;
using UnityEngine.SceneManagement;

namespace Fika.Headless.Patches.DestroyGraphics;

public class LoadScenePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync), [typeof(string), typeof(LoadSceneMode)]);
    }

    /// <summary>
    /// Destroys every renderer and material of a freshly loaded scene. This is
    /// the patch that made the main menu of a playing install unreadable, so it
    /// is guarded twice: the plugin must be armed for a dedicated host, and the
    /// "Destroy Renderers" config entry must still be enabled.
    /// </summary>
    [PatchPostfix]
    static void Postfix(string sceneName, AsyncOperation __result)
    {
        if (!FikaHeadlessPlugin.IsArmed)
        {
            FikaHeadlessPlugin.FikaHeadlessLogger?.LogWarning($"[Headless] Refusing scene modification for '{sceneName}': " +
                "this process is not an armed dedicated host");
            return;
        }

        if (!FikaHeadlessPlugin.DestroyRenderersOnSceneLoad.Value)
        {
            FikaHeadlessPlugin.FikaHeadlessLogger?.LogInfo($"[Headless] Scene '{sceneName}' left untouched: 'Destroy Renderers' is disabled");
            return;
        }

        GameObject tempGameObject = new("SceneModificationHandler");
        SceneModificationHandler handler = tempGameObject.AddComponent<SceneModificationHandler>();

        handler.StartCoroutine(handler.WaitForSceneLoad(sceneName, __result).WrapToIl2Cpp());
    }
}

public class SceneModificationHandler : MonoBehaviour
{
    public SceneModificationHandler(IntPtr pointer) : base(pointer)
    {
    }

    public IEnumerator WaitForSceneLoad(string sceneName, AsyncOperation operation)
    {
        // Wait for the scene to finish loading
        while (!operation.isDone)
        {
            yield return null;
        }

        // The gate may have been closed again (forced mode + normal profile).
        if (!FikaHeadlessPlugin.IsArmed)
        {
            Destroy(gameObject);
            yield break;
        }

        FikaHeadlessPlugin.FikaHeadlessLogger.LogDebug($"Scene {sceneName} is fully loaded.");

        Scene loadedScene = SceneManager.GetSceneByName(sceneName);
        if (loadedScene.IsValid())
        {
            ModifyLoadedScene(loadedScene);
        }

        Destroy(gameObject);
    }

    private void ModifyLoadedScene(Scene scene)
    {
        foreach (GameObject RootGameObjects in scene.GetRootGameObjects())
        {
            //Logger.LogInfo($"Inspecting root object: {rootObj.name}");
            DestroyRenderers(RootGameObjects);
        }
    }

    private static readonly Type[] _protectedComponents = [
        typeof(WindowBreaker),
        typeof(AmbientLight),
        typeof(AreaLight),
        typeof(HotObject),
        typeof(RoadSplineGenerator),
        typeof(RoadSolidMarkGenerator),
        typeof(RoadMarksGenerator),
        typeof(WireGenerator), // These can't be removed, MeshRenderers keeps these alive at the moment
			typeof(WireSplineGenerator)
    ];

    private void DestroyRenderers(GameObject prefab)
    {
        Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
        ParticleSystem[] particles = prefab.GetComponentsInChildren<ParticleSystem>(true);

        foreach (ParticleSystem particle in particles)
        {
            if (particle.gameObject.name.ToLower().Contains("door"))
            {
                continue;
            }

            particle.Stop();
            Destroy(particle);
        }

        foreach (Renderer renderer in renderers)
        {
            if (renderer.GetComponent<TMP_Text>() != null)
            {
                continue;
            }

            // Check for protected components we absolutely cannot unload, these would break the game someway or another.
            bool hasProtectedRenderer = false;
            foreach (Type componentType in _protectedComponents)
            {
                if (renderer.gameObject.GetComponent(Il2CppType.From(componentType)) != null || renderer.gameObject.name.ToLower().Contains("door") || renderer.gameObject.name.ToLower().Contains("glass"))
                {
                    hasProtectedRenderer = true;
                    break;
                }
            }

            // Unload materials and textures.
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material != null)
                {
                    if (material.name.ToLower().Contains("glass") || (material.shader != null && material.shader.name.StartsWith("TextMeshPro")))
                    {
                        continue;
                    }

                    if (material.HasProperty("_MainTex"))
                    {
                        material.mainTexture = null;
                    }

                    Destroy(material);
                }
            }

            if (hasProtectedRenderer)
            {
                continue;
            }

            // Destroy the renderer itself, note: This should not remove any colliders and such.
#if DEBUG
            FikaHeadlessPlugin.FikaHeadlessLogger.LogDebug($"Removing Renderer: {renderer.gameObject.name}");
#endif
            Destroy(renderer);
        }
    }
}
