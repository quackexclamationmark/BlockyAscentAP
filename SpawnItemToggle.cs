/*using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

[BepInPlugin("com.votreid.spawnitem", "Spawn Item", "1.0.0")]
public class SpawnItemToggle : BaseUnityPlugin
{
    private static Harmony _harmony;
    private static BepInEx.Logging.ManualLogSource StaticLogger;

    private static KeyCode spawnKey = KeyCode.F4;
    private static bool lastSpawnKeyState = false;

    private static KeyCode deleteLastKey = KeyCode.F6;
    private static bool lastDeleteKeyState = false;

    private static Stack<GameObject> spawnedItems = new Stack<GameObject>();

    private static AssetBundle _bundle;
    private static GameObject _cachedPrefab;

    private const string BundleFileName = "archipelagoitem";
    private const string PrefabName = "logoap";

    private const float SpawnScale = 0.2f;

    private static string bundlePath;

    private void Awake()
    {
        StaticLogger = Logger;

        transform.SetParent(null);
        DontDestroyOnLoad(this.gameObject);

        bundlePath = Path.Combine(Path.GetDirectoryName(Info.Location), BundleFileName);

        if (_harmony == null)
        {
            _harmony = new Harmony("com.votreid.spawnitem");
            _harmony.PatchAll();
            Logger.LogInfo("SpawnItem: pret. F4 pour spawn, F6 pour supprimer le dernier item.");
        }
    }

    private static bool GetKeyIsDown(KeyCode key)
    {
        bool isDown = false;
        try
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                try
                {
                    Key keyEnum = (Key)Enum.Parse(typeof(Key), key.ToString(), true);
                    var control = kb[keyEnum];
                    if (control != null && control.isPressed) isDown = true;
                }
                catch
                {
                    foreach (var k in kb.allKeys)
                    {
                        if (k.isPressed && string.Equals(k.displayName, key.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            isDown = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                try { isDown = UnityEngine.Input.GetKey(key); } catch { }
            }
        }
        catch { }
        return isDown;
    }

    // ---------------- mesh custom (Blender -> FBX -> Unity -> AssetBundle) ----------------
    private static void LoadBundleIfNeeded()
    {
        if (_bundle != null) return;

        if (!File.Exists(bundlePath))
        {
            StaticLogger?.LogWarning($"SpawnItem: bundle introuvable a '{bundlePath}'");
            return;
        }

        _bundle = AssetBundle.LoadFromFile(bundlePath);
        if (_bundle == null)
        {
            StaticLogger?.LogWarning("SpawnItem: echec du chargement de l'AssetBundle (version Unity incompatible ?)");
            return;
        }

        _cachedPrefab = _bundle.LoadAsset<GameObject>(PrefabName);

        if (_cachedPrefab == null)
            StaticLogger?.LogWarning($"SpawnItem: prefab '{PrefabName}' introuvable dans le bundle.");
        else
            StaticLogger?.LogInfo("SpawnItem: bundle et prefab charges avec succes.");
    }

    private static void SpawnCustomMesh(Vector3 position)
    {
        LoadBundleIfNeeded();

        if (_cachedPrefab == null)
        {
            StaticLogger?.LogWarning("SpawnItem: prefab non charge, spawn annule.");
            return;
        }

        GameObject clone = UnityEngine.Object.Instantiate(_cachedPrefab, position, Quaternion.identity);
        clone.transform.localScale *= SpawnScale;
        StaticLogger?.LogInfo($"SpawnItem: '{clone.name}' spawn a {position}");

        FixShaders(clone);

        spawnedItems.Push(clone);
    }

    // Supprime le dernier item spawn (ignore les entrees deja detruites entre-temps).
    private static void DeleteLastSpawned()
    {
        while (spawnedItems.Count > 0)
        {
            GameObject last = spawnedItems.Pop();
            if (last != null)
            {
                StaticLogger?.LogInfo($"SpawnItem: suppression de '{last.name}'.");
                UnityEngine.Object.Destroy(last);
                return;
            }
            // Sinon (deja detruit par autre chose), on continue vers l'item precedent.
        }

        StaticLogger?.LogInfo("SpawnItem: aucun item a supprimer.");
    }

    // Tente de reparer les materiaux "Standard" (Built-in RP) qui ne s'affichent
    // pas si le jeu tourne en URP.
    private static void FixShaders(GameObject root)
    {
        // Essaye les noms de shader URP les plus courants, dans l'ordre.
        string[] candidateShaders = new string[]
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Unlit"
        };

        Shader replacement = null;
        foreach (var name in candidateShaders)
        {
            replacement = Shader.Find(name);
            if (replacement != null)
            {
                StaticLogger?.LogInfo($"SpawnItem: shader de remplacement trouve -> {name}");
                break;
            }
        }

        if (replacement == null)
        {
            StaticLogger?.LogInfo("SpawnItem: aucun shader URP trouve, le jeu n'est peut-etre pas en URP.");
            return;
        }

        var renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            foreach (var mat in r.materials) // instances, pas sharedMaterial, pour ne pas modifier l'asset bundle
            {
                Texture mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                mat.shader = replacement;
                if (mainTex != null && mat.HasProperty("_BaseMap"))
                {
                    mat.SetTexture("_BaseMap", mainTex);
                }
            }
        }

        StaticLogger?.LogInfo("SpawnItem: shaders corriges pour compatibilite URP.");
    }

    [HarmonyPatch(typeof(CMF.MyWalkerController), "HandleInput")]
    static class H_HandleInput_Patch
    {
        [HarmonyPostfix]
        static void Postfix(CMF.MyWalkerController __instance)
        {
            bool isSpawnDown = GetKeyIsDown(spawnKey);
            if (isSpawnDown && !lastSpawnKeyState)
            {
                // Position du joueur au moment de l'appui, avec un petit offset
                // vers le haut pour eviter de spawn dans le sol.
                Vector3 playerPos = __instance.transform.position + Vector3.up * 1f;
                SpawnCustomMesh(playerPos);
            }
            lastSpawnKeyState = isSpawnDown;

            bool isDeleteDown = GetKeyIsDown(deleteLastKey);
            if (isDeleteDown && !lastDeleteKeyState)
            {
                DeleteLastSpawned();
            }
            lastDeleteKeyState = isDeleteDown;
        }
    }
}*/