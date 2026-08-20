using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

[BepInPlugin("com.votreid.spawncollectibles", "Spawn Collectibles", "1.0.0")]
public class CollectiblesManager : BaseUnityPlugin
{
    private static Harmony _harmony;
    private static BepInEx.Logging.ManualLogSource StaticLogger;

    private static AssetBundle _bundle;
    private static GameObject _cachedPrefab;

    // Sous-dossier contenant les assets externes (bundle + sons), a cote de
    // la DLL : "Blocky Ascent\BepInEx\plugins\BlockyArchipelago\Assets\".
    private const string AssetsSubfolder = "Assets";

    private const string BundleFileName = "archipelagoitem";
    private const string PrefabName = "logoap";
    private const float SpawnScale = 0.2f;
    private const float RotationSpeed = 45f;
    private const float PickupRadius = 1.5f;

    // --- Son de pickup ---
    // Place un fichier "pickup.wav" (PCM 8/16/24/32 bits) dans le sous-dossier
    // "Assets" du plugin. Charge et parse "a la main" (sans UnityWebRequest,
    // module pas toujours present dans les Managed/ des jeux BepInEx) des
    // Awake(), donc pret avant le premier ramassage.
    // Le format .ogg n'est pas supporte sans dependance externe.
    private const string PickupSoundBaseName = "pickup";
    private static AudioSource _audioSource;
    private static AudioClip _pickupClip;
    private const float PickupSoundVolume = 0.10f;

    private static string bundlePath;
    private static bool alreadySpawned = false;
    private static List<GameObject> spawnedItems = new List<GameObject>();

    private static readonly (string name, Vector3 pos, float radius)[] CollectibleLocations = new (string, Vector3, float)[]
    {
        ("Before Boss Fight - Collectible",     new Vector3(-111.41f, 1966.15f, 3.47f), PickupRadius),
        ("Above The Arrow - Collectible",       new Vector3(-205.04f, 1929.21f, -31.52f), PickupRadius),
        ("Yellow Pillars - After Tutorial - Collectible",       new Vector3(-183.99f, 1788f, -63.13f), PickupRadius),
        ("Bounce Pad Tower - Collectible",      new Vector3(-189.66f, 1858.52f, -28.33f), PickupRadius),
        ("Dropper 1st Tower - Collectible",        new Vector3(-227.45f, 1468.07f, -2.77f), PickupRadius),
        ("Dropper Bridge - Collectible",        new Vector3(-263.43f, 1392.21f, -9.05f), PickupRadius),
        ("Before End Of Tutorial - Collectible",        new Vector3(77.53f, 837f, 12.55f), PickupRadius),
        ("Bronze Blocks Parkour - Collectible", new Vector3(77.81f, 764.02f, 55.56f), PickupRadius),
        ("Mustard-Yellow Parkour - Collectible",new Vector3(149.57f, 704.40f, 15.95f), PickupRadius),
        ("Yellow - Collectible",         new Vector3(14.33f, 630.66f, 42.39f), PickupRadius),
        ("Yellow-Green - Collectible",         new Vector3(9.75f, 601.57f, 123.13f), PickupRadius),
        ("Labyrinth - Pink Room - Collectible", new Vector3(16.72f, 483f, 46.65f), 1f),
        ("Light Green / Labyrinth - Collectible", new Vector3(10.02f, 406.40f, 46.99f), PickupRadius),
        ("Blue - Collectible 2",         new Vector3(-36.57f, 316.09f, 80.05f), PickupRadius),
        ("Blue - Collectible 1",         new Vector3(8.55f, 208.50f, -5.91f), PickupRadius),
        ("Deep Blue - Flying - Collectible 2",             new Vector3(-0.04f, 106.08f, 23.79f), PickupRadius),
        ("Deep Blue - Collectible 1",             new Vector3(0.62f, 92f, -17.71f), 1f),
        ("Deep Purple - Collectible",           new Vector3(7.02f, 68.71f, 13.20f), PickupRadius),
        ("Light Purple - Collectible 2",  new Vector3(-1.61f, 28.50f, 35.66f), PickupRadius),
        ("Light Purple - Start - Collectible",  new Vector3(-0.11f, 2.77f, 15.82f), PickupRadius),
    };

    private void Awake()
    {
        StaticLogger = Logger;
        APConfig.Init(Logger);
        transform.SetParent(null);
        DontDestroyOnLoad(this.gameObject);
        bundlePath = Path.Combine(Path.GetDirectoryName(Info.Location), AssetsSubfolder, BundleFileName);

        if (_harmony == null)
        {
            _harmony = new Harmony("com.votreid.spawncollectibles");
            _harmony.PatchAll();
            Logger.LogInfo("SpawnCollectibles: pret, spawn au chargement du niveau. [BUILD-MARKER-GUIHOST-07]");
        }

        if (_pickupClip == null)
        {
            LoadPickupSound();
        }
    }

    private void LoadPickupSound()
    {
        string pluginDir = Path.GetDirectoryName(Info.Location);
        string wavPath = Path.Combine(pluginDir, AssetsSubfolder, PickupSoundBaseName + ".wav");

        if (!File.Exists(wavPath))
        {
            Logger.LogWarning($"SpawnCollectibles: aucun son de pickup trouve ('{wavPath}'), le ramassage restera silencieux.");
            return;
        }

        AudioClip clip = LoadWav(wavPath, PickupSoundBaseName);
        if (clip == null)
        {
            Logger.LogWarning($"SpawnCollectibles: echec du parsing du fichier WAV '{wavPath}'.");
            return;
        }

        _pickupClip = clip;
        Logger.LogInfo($"SpawnCollectibles: son de pickup charge depuis '{wavPath}'.");
    }

    // Parseur WAV (PCM) minimaliste, sans dependance a UnityWebRequestModule
    // (module pas toujours present dans les Managed/ des jeux BepInEx).
    // Supporte les WAV PCM standards 8/16/24/32 bits.
    private AudioClip LoadWav(string path, string clipName)
    {
        byte[] fileBytes;
        try
        {
            fileBytes = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            Logger.LogWarning($"SpawnCollectibles: impossible de lire '{path}' ({e.Message})");
            return null;
        }

        if (fileBytes.Length < 44
            || Encoding.ASCII.GetString(fileBytes, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(fileBytes, 8, 4) != "WAVE")
        {
            Logger.LogWarning($"SpawnCollectibles: '{path}' n'est pas un fichier WAV valide.");
            return null;
        }

        int channels = 0, sampleRate = 0, bitsPerSample = 0, audioFormat = 0;
        int dataStart = -1, dataSize = 0;

        int pos = 12;
        while (pos + 8 <= fileBytes.Length)
        {
            string chunkId = Encoding.ASCII.GetString(fileBytes, pos, 4);
            int chunkSize = BitConverter.ToInt32(fileBytes, pos + 4);
            int chunkDataStart = pos + 8;

            if (chunkId == "fmt " && chunkDataStart + 16 <= fileBytes.Length)
            {
                audioFormat = BitConverter.ToInt16(fileBytes, chunkDataStart + 0);
                channels = BitConverter.ToInt16(fileBytes, chunkDataStart + 2);
                sampleRate = BitConverter.ToInt32(fileBytes, chunkDataStart + 4);
                bitsPerSample = BitConverter.ToInt16(fileBytes, chunkDataStart + 14);

                // WAVE_FORMAT_EXTENSIBLE (0xFFFE) : le vrai format (PCM=1 /
                // IEEE float=3) est encode dans les 2 premiers octets du
                // GUID de sous-format, a l'offset 24 dans le chunk fmt.
                if (audioFormat == unchecked((short)0xFFFE) && chunkDataStart + 26 <= fileBytes.Length)
                {
                    audioFormat = BitConverter.ToInt16(fileBytes, chunkDataStart + 24);
                }
            }
            else if (chunkId == "data")
            {
                dataStart = chunkDataStart;
                dataSize = chunkSize;
            }

            // Les chunks WAV sont alignes sur un nombre pair d'octets.
            pos = chunkDataStart + chunkSize + (chunkSize % 2);
        }

        if (dataStart < 0 || channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0)
        {
            Logger.LogWarning($"SpawnCollectibles: en-tete WAV incomplet ou non supporte dans '{path}'.");
            return null;
        }

        // Certains encodeurs ecrivent une taille de "data" erronee : on
        // se protege d'un depassement de la fin du fichier.
        if (dataStart + dataSize > fileBytes.Length)
        {
            dataSize = fileBytes.Length - dataStart;
        }

        int bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            Logger.LogWarning($"SpawnCollectibles: bitsPerSample invalide ({bitsPerSample}) dans '{path}'.");
            return null;
        }

        int sampleCount = dataSize / bytesPerSample;
        float[] samples = new float[sampleCount];

        switch (bitsPerSample)
        {
            case 8:
                for (int i = 0; i < sampleCount; i++)
                    samples[i] = (fileBytes[dataStart + i] - 128) / 128f;
                break;

            case 16:
                for (int i = 0; i < sampleCount; i++)
                    samples[i] = BitConverter.ToInt16(fileBytes, dataStart + i * 2) / 32768f;
                break;

            case 24:
                for (int i = 0; i < sampleCount; i++)
                {
                    int offset = dataStart + i * 3;
                    int sample = fileBytes[offset] | (fileBytes[offset + 1] << 8) | (fileBytes[offset + 2] << 16);
                    if ((sample & 0x800000) != 0) sample = unchecked((int)(sample | 0xFF000000));
                    samples[i] = sample / 8388608f;
                }
                break;

            case 32:
                if (audioFormat == 3) // IEEE float
                {
                    for (int i = 0; i < sampleCount; i++)
                        samples[i] = BitConverter.ToSingle(fileBytes, dataStart + i * 4);
                }
                else // PCM entier 32 bits
                {
                    for (int i = 0; i < sampleCount; i++)
                        samples[i] = BitConverter.ToInt32(fileBytes, dataStart + i * 4) / 2147483648f;
                }
                break;

            default:
                Logger.LogWarning($"SpawnCollectibles: profondeur audio non supportee ({bitsPerSample} bits) dans '{path}'.");
                return null;
        }

        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float abs = Mathf.Abs(samples[i]);
            if (abs > peak) peak = abs;
        }

        Logger.LogInfo($"SpawnCollectibles: WAV parse - format={audioFormat}, channels={channels}, sampleRate={sampleRate}, bits={bitsPerSample}, samples={sampleCount}, peakAmplitude={peak:F4}");

        if (peak < 0.001f)
        {
            Logger.LogWarning("SpawnCollectibles: le son parse semble silencieux (peakAmplitude quasi nul) - le format WAV n'est probablement pas correctement interprete.");
        }

        AudioClip clip = AudioClip.Create(clipName, sampleCount / channels, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private static void EnsureAudioSource()
    {
        // Verifie le "fake null" Unity (composant/objet detruit cote
        // moteur, meme si la reference C# n'est pas litteralement null).
        if (_audioSource != null) return;

        GameObject host = new GameObject("SpawnCollectibles_AudioHost");
        UnityEngine.Object.DontDestroyOnLoad(host);

        _audioSource = host.AddComponent<AudioSource>();
        _audioSource.playOnAwake = false;
        _audioSource.spatialBlend = 0f; // son 2D, pas de position dans l'espace
        _audioSource.volume = PickupSoundVolume;
        StaticLogger?.LogInfo("SpawnCollectibles: AudioSource (re)cree.");
    }

    private static void PlayPickupSound()
    {
        EnsureAudioSource();

        if (_pickupClip == null)
        {
            StaticLogger?.LogWarning("SpawnCollectibles: son de pickup non joue (clip non charge).");
            return;
        }

        var listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
        if (listener == null)
        {
            StaticLogger?.LogWarning("SpawnCollectibles: aucun AudioListener trouve dans la scene, le son ne sera pas audible.");
        }

        _audioSource.PlayOneShot(_pickupClip);
        StaticLogger?.LogInfo($"SpawnCollectibles: PlayOneShot appele (clip='{_pickupClip.name}', duree={_pickupClip.length:F2}s, volume={_audioSource.volume}).");
    }

    private static void ShowPickupMessage(string text)
    {
        MessageHub.ShowMessage(text);
    }

    private static void RotateItems()
    {
        if (spawnedItems.Count == 0) return;

        float delta = RotationSpeed * Time.deltaTime;

        for (int i = spawnedItems.Count - 1; i >= 0; i--)
        {
            GameObject item = spawnedItems[i];
            if (item == null)
            {
                spawnedItems.RemoveAt(i);
                continue;
            }
            item.transform.Rotate(Vector3.up * delta, Space.World);
        }
    }

    [HarmonyPatch(typeof(CMF.MyWalkerController), "HandleInput")]
    static class H_PlayerStart_Patch
    {
        [HarmonyPostfix]
        static void Postfix(CMF.MyWalkerController __instance)
        {
            if (!alreadySpawned)
            {
                alreadySpawned = true;
                LoadBundleIfNeeded();
                SpawnAllCollectibles();
            }

            RotateItems();
        }
    }

    private static void SpawnAllCollectibles()
    {
        if (_cachedPrefab == null)
        {
            StaticLogger?.LogWarning("SpawnCollectibles: prefab non charge, spawn annule.");
            return;
        }

        int skipped = 0;
        foreach (var (name, pos, radius) in CollectibleLocations)
        {
            if (APConfig.IsCollectibleCollected(name))
            {
                skipped++;
                continue;
            }
            SpawnCustomMesh(name, pos, radius);
        }

        StaticLogger?.LogInfo($"SpawnCollectibles: {spawnedItems.Count} items spawnes, {skipped} deja recuperes (ap_config.json) ignores.");
    }

    private static void LoadBundleIfNeeded()
    {
        if (_bundle != null) return;

        if (!File.Exists(bundlePath))
        {
            StaticLogger?.LogWarning($"SpawnCollectibles: bundle introuvable a '{bundlePath}'");
            return;
        }

        _bundle = AssetBundle.LoadFromFile(bundlePath);
        if (_bundle == null)
        {
            StaticLogger?.LogWarning("SpawnCollectibles: echec du chargement de l'AssetBundle (version Unity incompatible ?)");
            return;
        }

        _cachedPrefab = _bundle.LoadAsset<GameObject>(PrefabName);

        if (_cachedPrefab == null)
            StaticLogger?.LogWarning($"SpawnCollectibles: prefab '{PrefabName}' introuvable dans le bundle.");
        else
            StaticLogger?.LogInfo("SpawnCollectibles: bundle et prefab charges avec succes.");
    }

    private static void SpawnCustomMesh(string label, Vector3 position, float pickupRadius)
    {
        GameObject clone = UnityEngine.Object.Instantiate(_cachedPrefab, position, Quaternion.identity);
        clone.name = label;
        clone.transform.localScale *= SpawnScale;

        var animators = clone.GetComponentsInChildren<Animator>(true);
        foreach (var a in animators) a.enabled = false;

        var legacyAnims = clone.GetComponentsInChildren<Animation>(true);
        foreach (var a in legacyAnims) a.enabled = false;

        Rigidbody rb = clone.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
        }

        FixShaders(clone);
        AddPickupTrigger(clone, label, pickupRadius);

        StaticLogger?.LogInfo($"SpawnCollectibles: '{clone.name}' spawn a {position}");

        spawnedItems.Add(clone);
    }

    private static void AddPickupTrigger(GameObject clone, string label, float pickupRadius)
    {
        SphereCollider trigger = clone.AddComponent<SphereCollider>();
        trigger.isTrigger = true;

        float uniformScale = clone.transform.lossyScale.x;
        trigger.radius = uniformScale > 0f ? pickupRadius / uniformScale : pickupRadius;

        CollectibleTrigger pickup = clone.AddComponent<CollectibleTrigger>();
        pickup.ItemLabel = label;
    }

    public class CollectibleTrigger : MonoBehaviour
    {
        public string ItemLabel;
        private bool _collected = false;

        private void OnTriggerEnter(Collider other)
        {
            if (_collected) return;

            var player = other.GetComponentInParent<CMF.MyWalkerController>();
            if (player == null) return;

            _collected = true;
            StaticLogger?.LogInfo($"SpawnCollectibles: '{ItemLabel}' found by player.");
            APConfig.MarkCollectibleCollected(ItemLabel);   // <-- ajout
            PlayPickupSound();
            ShowPickupMessage($"{ItemLabel} found!");
            RemoveItem(gameObject);
        }
    }

    private static void RemoveItem(GameObject item)
    {
        spawnedItems.Remove(item);
        UnityEngine.Object.Destroy(item);
    }

    private static void FixShaders(GameObject root)
    {
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
            if (replacement != null) break;
        }

        if (replacement == null) return;

        var renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            foreach (var mat in r.materials)
            {
                Texture mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                mat.shader = replacement;
                if (mainTex != null && mat.HasProperty("_BaseMap"))
                {
                    mat.SetTexture("_BaseMap", mainTex);
                }

                if (mat.HasProperty("_EmissionColor"))
                {
                    mat.SetColor("_EmissionColor", Color.black);
                    mat.DisableKeyword("_EMISSION");
                }
            }

            r.receiveShadows = true;
        }
    }
}