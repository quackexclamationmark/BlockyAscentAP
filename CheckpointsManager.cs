using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[BepInPlugin("com.votreid.checkpointsmanager", "Checkpoints Manager", "1.0.0")]
public class CheckpointsManager : BaseUnityPlugin
{
    private static Harmony _harmony;
    private static BepInEx.Logging.ManualLogSource StaticLogger;

    // Sous-dossier contenant les assets externes (sons), a cote de la DLL :
    // "Blocky Ascent\BepInEx\plugins\BlockyArchipelago\Assets\".
    private const string AssetsSubfolder = "Assets";

    // --- Touches ---
    private static readonly KeyCode RespawnKey = KeyCode.F8;
    private static readonly KeyCode DebugToggleKey = KeyCode.F9;
    private static readonly KeyCode CheckpointsMenuKey = KeyCode.F10;
    private static bool _lastRespawnKeyState = false;
    private static bool _lastDebugKeyState = false;
    private static bool _lastCheckpointsMenuKeyState = false;
    private static bool _debugMode = false;

    // --- Checkpoint courant (celui utilise par F8) ---
    // "Light Purple Checkpoint" est le point de depart, defini directement
    // ici (il se declenche automatiquement au lancement, pas besoin de zone).
    // Il ne declenche ni message ni son (c'est le point de depart, pas une
    // vraie progression).
    private const string StartCheckpointName = "Light Purple Checkpoint";
    private static readonly Vector3 StartCheckpointPos = new Vector3(0f, 0.12f, 0f);
    private static Vector3 _lastCheckpointPos = StartCheckpointPos;
    private static string _lastCheckpointName = StartCheckpointName;
    private static bool _initialized = false;

    // --- Son de checkpoint ---
    // Place un fichier "checkpoint.wav" (PCM 8/16/24/32 bits) dans le
    // sous-dossier "Assets" du plugin. Meme parseur WAV maison que pour les
    // collectibles (pas de dependance a UnityWebRequestModule).
    private const string CheckpointSoundBaseName = "checkpoint";
    private static AudioSource _audioSource;
    private static AudioClip _checkpointClip;
    private const float CheckpointSoundVolume = 0.10f;

    private enum CheckpointType { Height, Zone }

    private class CheckpointDef
    {
        public string Name;
        public Vector3 RespawnPos;
        public CheckpointType Type;

        // Type Height : se declenche des que pos.y >= HeightThreshold
        public float HeightThreshold;

        // Type Zone : boite AABB
        public float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;

        public bool Triggered;
        public Color DebugColor;
        public GameObject DebugVisual;

        // Taille (X/Z) du plan de visualisation debug pour les checkpoints
        // de type Height. 40 par defaut, personnalisable si la zone reelle
        // de passage est plus large.
        public float DebugPlaneSize = 40f;
    }

    private static List<CheckpointDef> _checkpoints;

    private void Awake()
    {
        StaticLogger = Logger;
        APConfig.Init(Logger);
        transform.SetParent(null);
        DontDestroyOnLoad(this.gameObject);

        if (_harmony == null)
        {
            _harmony = new Harmony("com.votreid.checkpointsmanager");
            _harmony.PatchAll();
            Logger.LogInfo("CheckpointsManager: Harmony patches appliques.");
        }

        if (_checkpoints == null)
        {
            _checkpoints = BuildCheckpoints();
            AssignDebugColors(_checkpoints);
            RestoreCheckpointStates();
            Logger.LogInfo($"CheckpointsManager: {_checkpoints.Count} checkpoints charges (+ 1 checkpoint de depart).");
        }

        if (_checkpointClip == null)
        {
            LoadCheckpointSound();
        }
    }

    // ==========================================================================
    // Son de checkpoint
    // ==========================================================================
    private void LoadCheckpointSound()
    {
        string pluginDir = Path.GetDirectoryName(Info.Location);
        string wavPath = Path.Combine(pluginDir, AssetsSubfolder, CheckpointSoundBaseName + ".wav");

        if (!File.Exists(wavPath))
        {
            Logger.LogWarning($"CheckpointsManager: aucun son de checkpoint trouve ('{wavPath}'), les checkpoints resteront silencieux.");
            return;
        }

        AudioClip clip = LoadWav(wavPath, CheckpointSoundBaseName);
        if (clip == null)
        {
            Logger.LogWarning($"CheckpointsManager: echec du parsing du fichier WAV '{wavPath}'.");
            return;
        }

        _checkpointClip = clip;
        Logger.LogInfo($"CheckpointsManager: son de checkpoint charge depuis '{wavPath}'.");
    }

    private static void RestoreCheckpointStates()
    {
        if (_checkpoints == null) return;

        int restoredCount = 0;
        foreach (var cp in _checkpoints)
        {
            if (APConfig.IsCheckpointUnlocked(cp.Name))
            {
                cp.Triggered = true;
                restoredCount++;
            }
        }

        StaticLogger?.LogInfo($"CheckpointsManager: {restoredCount} checkpoint(s) restaure(s) depuis ap_config.json.");
    }

    private AudioClip LoadWav(string path, string clipName)
    {
        byte[] fileBytes;
        try
        {
            fileBytes = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            Logger.LogWarning($"CheckpointsManager: impossible de lire '{path}' ({e.Message})");
            return null;
        }

        if (fileBytes.Length < 44
            || Encoding.ASCII.GetString(fileBytes, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(fileBytes, 8, 4) != "WAVE")
        {
            Logger.LogWarning($"CheckpointsManager: '{path}' n'est pas un fichier WAV valide.");
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
            Logger.LogWarning($"CheckpointsManager: en-tete WAV incomplet ou non supporte dans '{path}'.");
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
            Logger.LogWarning($"CheckpointsManager: bitsPerSample invalide ({bitsPerSample}) dans '{path}'.");
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
                Logger.LogWarning($"CheckpointsManager: profondeur audio non supportee ({bitsPerSample} bits) dans '{path}'.");
                return null;
        }

        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float abs = Mathf.Abs(samples[i]);
            if (abs > peak) peak = abs;
        }

        Logger.LogInfo($"CheckpointsManager: WAV parse - format={audioFormat}, channels={channels}, sampleRate={sampleRate}, bits={bitsPerSample}, samples={sampleCount}, peakAmplitude={peak:F4}");

        if (peak < 0.001f)
        {
            Logger.LogWarning("CheckpointsManager: le son parse semble silencieux (peakAmplitude quasi nul) - le format WAV n'est probablement pas correctement interprete.");
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

        GameObject host = new GameObject("CheckpointsManager_AudioHost");
        UnityEngine.Object.DontDestroyOnLoad(host);

        _audioSource = host.AddComponent<AudioSource>();
        _audioSource.playOnAwake = false;
        _audioSource.spatialBlend = 0f; // son 2D, pas de position dans l'espace
        _audioSource.volume = CheckpointSoundVolume;
        StaticLogger?.LogInfo("CheckpointsManager: AudioSource (re)cree.");
    }

    private static void PlayCheckpointSound()
    {
        EnsureAudioSource();

        if (_checkpointClip == null)
        {
            StaticLogger?.LogWarning("CheckpointsManager: son de checkpoint non joue (clip non charge).");
            return;
        }

        var listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
        if (listener == null)
        {
            StaticLogger?.LogWarning("CheckpointsManager: aucun AudioListener trouve dans la scene, le son ne sera pas audible.");
        }

        _audioSource.PlayOneShot(_checkpointClip);
        StaticLogger?.LogInfo($"CheckpointsManager: PlayOneShot appele (clip='{_checkpointClip.name}', duree={_checkpointClip.length:F2}s, volume={_audioSource.volume}).");
    }

    // ==========================================================================
    // Definition des checkpoints
    // ==========================================================================
    private static List<CheckpointDef> BuildCheckpoints()
    {
        var list = new List<CheckpointDef>();

        list.Add(Height("Deep Purple Checkpoint", new Vector3(12.44f, 60.89f, 16.10f), 60f));

        list.Add(Zone("Deep Blue Checkpoint 1", new Vector3(8.42f, 81.88f, -0.51f),
            minX: 6.93f, maxX: 9.89f, minZ: -8.15f, maxZ: 0.87f, minY: 81.88f, maxY: 92f));

        list.Add(Zone("Deep Blue Checkpoint 2", new Vector3(0.27f, 148.89f, 51.44f),
            minX: -2.17f, maxX: 2.92f, minZ: 48.93f, maxZ: 54.06f, minY: 148.88f, maxY: 158f));

        list.Add(Zone("Blue Checkpoint", new Vector3(-10.82f, 179.88f, 34.04f),
            minX: -12.08f, maxX: -9.81f, minZ: 32.85f, maxZ: 35.16f, minY: 179.88f, maxY: 185f));

        list.Add(Zone("Cyan Checkpoint", new Vector3(-3.68f, 305.89f, -10.73f),
            minX: -5.12f, maxX: 5.19f, minZ: -16.24f, maxZ: -5.83f, minY: 305.88f, maxY: 315f));

        list.Add(Height("Light Green / Labyrinth Checkpoint", new Vector3(9.82f, 404.89f, 36.85f), 404f));

        list.Add(Zone("Yellow-Green Checkpoint 1", new Vector3(10.05f, 560.88f, -27.13f),
            minX: 2.85f, maxX: 17.06f, minZ: -35.23f, maxZ: -21.80f, minY: 560.89f, maxY: 570f));

        list.Add(Zone("Yellow-Green Checkpoint 2", new Vector3(9.82f, 598.89f, -30.26f),
            minX: -0.11f, maxX: 20.18f, minZ: -38.02f, maxZ: -17.80f, minY: 598.88f, maxY: 700f));

        list.Add(Zone("Yellow Checkpoint 1", new Vector3(10.08f, 622.88f, 131.27f),
            minX: 8.05f, maxX: 12.12f, minZ: 128.90f, maxZ: 134.00f, minY: 622.88f, maxY: 630f));

        list.Add(Zone("Yellow Checkpoint 2", new Vector3(12.38f, 633.89f, 142.33f),
            minX: 9.84f, maxX: 15.02f, minZ: 139.80f, maxZ: 145.18f, minY: 633.89f, maxY: 643f));

        list.Add(Zone("Mustard-Yellow Checkpoint", new Vector3(101.47f, 651.88f, 15.10f),
            minX: 99.89f, maxX: 109.41f, minZ: 6.56f, maxZ: 17.24f, minY: 651.89f, maxY: 661f));

        list.Add(Zone("Red & Blue Checkpoint", new Vector3(75.38f, 697.88f, -2.95f),
            minX: 68.07f, maxX: 84.02f, minZ: -6.16f, maxZ: 0.16f, minY: 697.88f, maxY: 707f));

        list.Add(Zone("Bronze Checkpoint", new Vector3(77.43f, 724.88f, -26.03f),
            minX: 74.92f, maxX: 80.03f, minZ: -35.06f, maxZ: -18.94f, minY: 724.88f, maxY: 734f));

        list.Add(Zone("Orange Checkpoint 1", new Vector3(77.46f, 779.89f, 229.04f),
            minX: 71.51f, maxX: 83.56f, minZ: 223.18f, maxZ: 233.72f, minY: 778.88f, maxY: 788f));

        list.Add(Zone("Orange Checkpoint 2", new Vector3(77.62f, 780.89f, 140.80f),
            minX: 74.52f, maxX: 80.46f, minZ: 110.00f, maxZ: 138.27f, minY: 778.88f, maxY: 788f));

        list.Add(Zone("Red Checkpoint", new Vector3(77.45f, 791.88f, 32.08f),
            minX: 71.38f, maxX: 83.60f, minZ: 38.96f, maxZ: 51.72f, minY: 790.89f, maxY: 795f));

        list.Add(Zone("Deep Red Checkpoint", new Vector3(77.50f, 807.89f, 35.47f),
            minX: 76.48f, maxX: 78.51f, minZ: 18.27f, maxZ: 41.09f, minY: 807.88f, maxY: 817f));

        var iceParkourCp = Height("Ice Parkour Checkpoint", new Vector3(77.52f, 1388.88f, -9.49f), 1295f);
        iceParkourCp.DebugPlaneSize = 100f;
        list.Add(iceParkourCp);

        list.Add(Zone("Dropper Checkpoint", new Vector3(-198.42f, 1375.88f, -9.09f),
            minX: -200.22f, maxX: -192.84f, minZ: -12.56f, maxZ: -5.50f, minY: 1375.88f, maxY: 1395f));

        list.Add(Height("Moving Slabs Checkpoint", new Vector3(-211.80f, 1713.43f, -9.16f), 1710f));

        list.Add(Zone("Orange Pillars Checkpoint", new Vector3(-184.01f, 1770.38f, -35.83f),
            minX: -187.47f, maxX: -180.40f, minZ: -42.07f, maxZ: -35.00f, minY: 1770.38f, maxY: 1780f));

        list.Add(Zone("Dark Stone Checkpoint", new Vector3(-183.91f, 1783.38f, -8.81f),
            minX: -186.23f, maxX: -181.77f, minZ: -9.99f, maxZ: -3.01f, minY: 1783.38f, maxY: 1793f));

        list.Add(Zone("City Start Checkpoint", new Vector3(-181.77f, 1894.88f, -28.55f),
            minX: -184.00f, maxX: -177.10f, minZ: -31.20f, maxZ: -25.88f, minY: 1894.89f, maxY: 1904f));

        list.Add(Zone("City 1st Bumper Parkour", new Vector3(-103.61f, 1896.89f, -45.94f),
            minX: -104.58f, maxX: -102.40f, minZ: -46.61f, maxZ: -43.42f, minY: 1896.88f, maxY: 1900f));

        list.Add(Height("City 2nd Bumper Parkour", new Vector3(-175.73f, 1926.89f, -55.95f), 1925f));

        list.Add(Height("Boss Fight Checkpoint", new Vector3(-86.87f, 2000.89f, -5.15f), 1999f));

        return list;
    }

    private static CheckpointDef Zone(string name, Vector3 respawn, float minX, float maxX, float minZ, float maxZ, float minY, float maxY)
    {
        return new CheckpointDef
        {
            Name = name,
            RespawnPos = respawn,
            Type = CheckpointType.Zone,
            MinX = minX,
            MaxX = maxX,
            MinZ = minZ,
            MaxZ = maxZ,
            MinY = minY,
            MaxY = maxY
        };
    }

    private static CheckpointDef Height(string name, Vector3 respawn, float threshold)
    {
        return new CheckpointDef
        {
            Name = name,
            RespawnPos = respawn,
            Type = CheckpointType.Height,
            HeightThreshold = threshold
        };
    }

    private static void AssignDebugColors(List<CheckpointDef> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            float hue = (float)i / list.Count;
            list[i].DebugColor = Color.HSVToRGB(hue, 0.85f, 1f);
        }
    }

    // ==========================================================================
    // Detection des triggers + touches, chaque frame (via le meme patch que
    // les autres plugins du projet)
    // ==========================================================================
    [HarmonyPatch(typeof(CMF.MyWalkerController), "HandleInput")]
    static class H_HandleInput_Patch
    {
        [HarmonyPostfix]
        static void Postfix(CMF.MyWalkerController __instance)
        {
            // Memorise le controller joueur courant : reutilise par
            // GoToCheckpoint() pour teleporter depuis le menu Checkpoints,
            // qui n'a pas acces a __instance directement.
            _lastController = __instance;

            Transform playerTransform = __instance.transform;

            if (!_initialized)
            {
                _initialized = true;
                // Checkpoint de depart : pas de message ni de son, c'est le
                // point de depart et non une vraie progression.
                StaticLogger?.LogInfo($"CheckpointsManager: checkpoint de depart '{StartCheckpointName}' actif.");

                // Cree l'overlay OnGUI ici (premier frame de gameplay reel,
                // scene chargee, jeu deja en cours) plutot que dans Awake().
                // Un objet OnGUI cree trop tot (avant le chargement de la
                // scene, pendant le demarrage du plugin) ne semble jamais
                // recevoir d'appels OnGUI dans ce jeu. MessageHub.GuiHost
                // fonctionne car il est cree tardivement, au premier message
                // affiche pendant le gameplay : on reproduit exactement ce
                // timing ici.
                EnsurePersistentGui();
            }

            CheckCheckpointTriggers(playerTransform.position);
            CheckRespawnKey(playerTransform, __instance);
            CheckDebugToggleKey();
            CheckCheckpointsMenuKey();
        }
    }

    // ==========================================================================
    // Detection du menu pause : cache l'overlay "Latest Checkpoint" quand le
    // jeu est en pause, le fait reapparaitre quand on reprend la partie.
    // ==========================================================================
    private static bool _isPaused = false;

    [HarmonyPatch(typeof(PauseMenu), "ActivatePauseMenu")]
    static class H_PauseMenu_Activate_Patch
    {
        [HarmonyPostfix]
        static void Postfix(PauseMenu __instance)
        {
            _isPaused = true;

            // Construction paresseuse (une seule fois, au premier passage
            // en pause) du panneau de liste des checkpoints. Comme pour
            // l'overlay OnGUI plus haut, on cree cette UI tard (au premier
            // vrai usage en jeu, scene/Canvas deja charges), pas dans
            // Awake().
            EnsureCheckpointsMenuUI(__instance);
        }
    }

    [HarmonyPatch(typeof(PauseMenu), "DeactivatePauseMenu")]
    static class H_PauseMenu_Deactivate_Patch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            _isPaused = false;

            if (_checkpointsPanel != null) _checkpointsPanel.SetActive(false);
        }
    }

    // ==========================================================================
    // Touche F10 : ouvre/ferme un panneau listant tous les checkpoints
    // (celui de depart + les 26 autres), avec teleportation vers n'importe
    // lequel de ceux deja declenches. Les checkpoints pas encore atteints
    // apparaissent non-interactifs (grises).
    //
    // IMPORTANT : F10 NE PASSE PLUS PAR PauseMenu.ActivatePauseMenu() /
    // DeactivatePauseMenu(). Le vrai menu pause (Echap, panneau a 6 boutons)
    // reste totalement independant. F10 reproduit uniquement les EFFETS de
    // la pause (flou de profondeur de champ, Time.timeScale = 0f, curseur
    // visible/deverrouille, activation de l'action map "UI" + desactivation
    // de l'input joueur) via OpenCheckpointsOverlay()/CloseCheckpointsOverlay(),
    // sans jamais activer base.transform.GetChild(0) (le panneau principal).
    //
    // On continue neanmoins de cloner les boutons DEJA PRESENTS dans la
    // hierarchie du menu pause (plutot que d'en creer de zero), car un
    // Canvas cree entierement par le plugin ne s'affichait pas dans ce jeu
    // (cf. l'overlay "Latest Checkpoint", qui a du etre reecrit en OnGUI).
    // Pour ca, l'enfant 0 de PauseMenu doit etre actif au moment de la
    // construction (une seule fois) : on l'active temporairement puis on le
    // redesactive aussitot apres, sans toucher au flou/temps/curseur.
    //
    // HYPOTHESES (a corriger si le jeu est structure differemment) :
    // - "PauseMenu.bigPanel" est le panneau contenant les 6 boutons actuels.
    // - Chaque bouton a un UnityEngine.UI.Button + un UnityEngine.UI.Text
    //   (ou, en repli, un composant nomme "TextMeshProUGUI") comme label.
    // ==========================================================================
    private static CMF.MyWalkerController _lastController;
    private static bool _checkpointsUiBuilt = false;
    private static PauseMenu _pauseMenuInstance;
    private static GameObject _checkpointsPanel;
    private static GameObject _mainPausePanel;
    private static readonly Dictionary<string, Button> _checkpointRowButtons = new Dictionary<string, Button>();

    // --- Reproduction manuelle des effets de PauseMenu.Activate/DeactivatePauseMenu ---
    private static DepthOfField _blur;
    private static bool _blurLookedUp = false;
    private static bool _checkpointsOverlayActive = false;

    // Recupere la reference au DepthOfField du Volume global, exactement
    // comme le fait PauseMenu.Start(), via le champ public "globalVolume"
    // (pas besoin de reflexion, le champ est public).
    private static void EnsureBlurReference(PauseMenu pauseMenu)
    {
        if (_blurLookedUp) return;
        _blurLookedUp = true;

        if (pauseMenu.globalVolume != null && pauseMenu.globalVolume.profile != null
            && pauseMenu.globalVolume.profile.TryGet(out DepthOfField dof))
        {
            _blur = dof;
        }
        else
        {
            StaticLogger?.LogWarning("CheckpointsManager: DepthOfField introuvable dans le Volume global, pas de flou pour le menu Checkpoints (F10).");
        }
    }

    // Reproduit les effets de PauseMenu.ActivatePauseMenu() (flou, pause du
    // temps, curseur, input map) SANS jamais activer base.transform.GetChild(0)
    // (le panneau principal a 6 boutons) et SANS toucher au champ prive
    // "pauseMenuActive" du vrai PauseMenu.
    private static void OpenCheckpointsOverlay(PauseMenu pauseMenu)
    {
        if (_checkpointsOverlayActive) return;

        EnsureBlurReference(pauseMenu);
        if (_blur != null) _blur.active = true;

        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        Time.timeScale = 0f;

        ReferenceManager.Instance.inputActionAsset.FindActionMap("UI", false)?.Enable();
        ReferenceManager.Instance.characterInput.DisableInput();

        _checkpointsOverlayActive = true;
        _isPaused = true; // masque l'overlay "Latest Checkpoint" pendant que le menu est ouvert
    }

    private static void CloseCheckpointsOverlay()
    {
        if (!_checkpointsOverlayActive) return;

        if (_blur != null) _blur.active = false;

        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
        Time.timeScale = 1f;

        ReferenceManager.Instance.characterInput.EnableInput();
        ReferenceManager.Instance.inputActionAsset.FindActionMap("UI", false)?.Disable();

        _checkpointsOverlayActive = false;
        _isPaused = false;
    }

    private static void CheckCheckpointsMenuKey()
    {
        bool isDown = GetKeyIsDown(CheckpointsMenuKey);
        if (isDown && !_lastCheckpointsMenuKeyState)
        {
            ToggleCheckpointsMenuViaKey();
        }
        _lastCheckpointsMenuKeyState = isDown;
    }

    private static void ToggleCheckpointsMenuViaKey()
    {
        PauseMenu pauseMenu = _pauseMenuInstance != null
            ? _pauseMenuInstance
            : UnityEngine.Object.FindObjectOfType<PauseMenu>(true);

        if (pauseMenu == null)
        {
            StaticLogger?.LogWarning("CheckpointsManager: PauseMenu introuvable dans la scene, impossible d'ouvrir le menu Checkpoints (F10).");
            return;
        }

        EnsureCheckpointsMenuUI(pauseMenu);
        if (_checkpointsPanel == null) return; // echec de construction, deja logge plus haut

        if (_checkpointsPanel.activeSelf)
        {
            // Deja ouvert : F10 referme tout. On ne touche jamais au vrai
            // PauseMenu (qu'on n'a jamais active) : on retire seulement les
            // effets qu'on a nous-memes appliques.
            _checkpointsPanel.SetActive(false);
            CloseCheckpointsOverlay();
            return;
        }

        // On ne touche JAMAIS a pauseMenu.ActivatePauseMenu() : on reproduit
        // seulement ses effets (flou + pause du temps + curseur + input map)
        // sans jamais afficher son panneau principal a 6 boutons.
        OpenCheckpointsOverlay(pauseMenu);

        RefreshCheckpointRows();
        _checkpointsPanel.SetActive(true);
    }

    private static void EnsureCheckpointsMenuUI(PauseMenu pauseMenu)
    {
        if (_checkpointsUiBuilt) return;
        _pauseMenuInstance = pauseMenu;

        // Le panneau principal (enfant 0) doit etre actif dans la hierarchie
        // pour qu'on y retrouve des Button "activeInHierarchy" a cloner. On
        // l'active temporairement UNIQUEMENT le temps de la construction
        // (sans flou/timeScale/curseur, contrairement a un vrai
        // ActivatePauseMenu), puis on le desactive aussitot : le joueur ne
        // doit jamais le voir via le menu Checkpoints (F10). Si on est
        // appele depuis le vrai menu pause (Echap), il est deja actif et on
        // ne le touche pas.
        _mainPausePanel = pauseMenu.transform.childCount > 0
            ? pauseMenu.transform.GetChild(0).gameObject
            : null;

        bool tempActivated = false;
        if (_mainPausePanel != null && !_mainPausePanel.activeSelf)
        {
            _mainPausePanel.SetActive(true);
            tempActivated = true;
        }

        try
        {
            // --- Diagnostic complet ---
            // Les tentatives precedentes en se fiant a "pauseMenu.bigPanel"
            // ont echoue systematiquement (meme en pleine partie, pas
            // seulement au demarrage) : ce champ ne pointe probablement pas
            // vers le panneau reellement affiche/cache par
            // Activate/DeactivatePauseMenu. On arrete de supposer et on
            // inspecte directement la vraie hierarchie de PauseMenu.
            StaticLogger?.LogInfo(
                $"CheckpointsManager: [diag] pauseMenu.transform.childCount={pauseMenu.transform.childCount}, " +
                $"bigPanel={(pauseMenu.bigPanel != null ? pauseMenu.bigPanel.name + " (active=" + pauseMenu.bigPanel.activeInHierarchy + ")" : "null")}, " +
                $"infoPanel={(pauseMenu.infoPanel != null ? pauseMenu.infoPanel.name + " (active=" + pauseMenu.infoPanel.activeInHierarchy + ")" : "null")}.");

            for (int i = 0; i < pauseMenu.transform.childCount; i++)
            {
                Transform child = pauseMenu.transform.GetChild(i);
                StaticLogger?.LogInfo(
                    $"CheckpointsManager: [diag] enfant[{i}] '{child.name}' " +
                    $"activeSelf={child.gameObject.activeSelf} activeInHierarchy={child.gameObject.activeInHierarchy}");
            }

            // Recherche TOUS les boutons sous PauseMenu (pas seulement sous
            // bigPanel), actifs ou non, pour voir precisement ce qui existe
            // reellement dans la scene.
            Button[] allButtonsAnywhere = pauseMenu.GetComponentsInChildren<Button>(true);
            StaticLogger?.LogInfo($"CheckpointsManager: [diag] {allButtonsAnywhere.Length} bouton(s) trouve(s) sous PauseMenu au total.");
            foreach (var b in allButtonsAnywhere)
            {
                StaticLogger?.LogInfo(
                    $"CheckpointsManager: [diag]   - '{b.name}' activeSelf={b.gameObject.activeSelf} " +
                    $"activeInHierarchy={b.gameObject.activeInHierarchy} parent='{b.transform.parent?.name}'");
            }

            Button[] buttons = allButtonsAnywhere.Where(b => b.gameObject.activeInHierarchy).ToArray();
            if (buttons.Length == 0)
            {
                // On NE marque PAS _checkpointsUiBuilt ici, pour reessayer
                // automatiquement au prochain appel (prochaine pause, ou
                // appui sur F10).
                StaticLogger?.LogWarning("CheckpointsManager: toujours aucun bouton actif trouve sous PauseMenu (voir logs [diag] ci-dessus), nouvelle tentative au prochain appel.");
                return;
            }

            Button template = buttons[buttons.Length - 1];

            // --- Diagnostic : RectTransform de chaque bouton actif trouve ---
            for (int i = 0; i < buttons.Length; i++)
            {
                RectTransform brt = buttons[i].GetComponent<RectTransform>();
                var layoutOnParent = buttons[i].transform.parent != null
                    ? buttons[i].transform.parent.GetComponent<LayoutGroup>()
                    : null;
                StaticLogger?.LogInfo(
                    $"CheckpointsManager: [diag] bouton actif[{i}] '{buttons[i].name}' parent='{buttons[i].transform.parent?.name}' " +
                    $"anchorMin={brt.anchorMin} anchorMax={brt.anchorMax} pivot={brt.pivot} " +
                    $"anchoredPos={brt.anchoredPosition} sizeDelta={brt.sizeDelta} " +
                    $"parentLayoutGroup={(layoutOnParent != null ? layoutOnParent.GetType().Name : "aucun")}");
            }

            BuildCheckpointsPanel(template);

            // Marque comme construit UNIQUEMENT en cas de succes complet.
            _checkpointsUiBuilt = true;
            StaticLogger?.LogInfo("CheckpointsManager: panneau de liste des checkpoints cree (ouverture via la touche F10).");
        }
        catch (Exception e)
        {
            // On ne marque pas _checkpointsUiBuilt non plus ici : on
            // retentera au prochain appel plutot que d'abandonner
            // definitivement sur un echec potentiellement transitoire.
            StaticLogger?.LogWarning($"CheckpointsManager: echec de la creation du menu Checkpoints ({e.Message}).\n{e.StackTrace}");
        }
        finally
        {
            // On redesactive le panneau principal si c'est nous qui l'avons
            // active temporairement pour la construction : le joueur ne
            // doit jamais le voir apparaitre via F10.
            if (tempActivated && _mainPausePanel != null)
            {
                _mainPausePanel.SetActive(false);
            }
        }
    }

    // ==========================================================================
    // Neutralise tout composant de localisation (ex: "GameObjectLocalizer",
    // confirme par les logs) trouve sur un objet clone. On DETRUIT le
    // composant plutot que de le desactiver : le simple `enabled = false`
    // ne suffisait pas ici, ce qui indique que le systeme de localisation
    // du jeu retrouve probablement toutes ses instances via un
    // FindObjectsOfType (ou une liste statique) et leur reapplique le texte
    // localise directement, sans se soucier de leur etat active/inactive.
    // Detruire le composant l'empeche d'etre retrouve par ce mecanisme.
    // ==========================================================================
    private static void DisableLocalizationComponents(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;

            string typeName = c.GetType().Name;
            if (typeName.IndexOf("localiz", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            StaticLogger?.LogInfo($"CheckpointsManager: composant de localisation '{typeName}' detruit sur '{c.gameObject.name}'.");
            UnityEngine.Object.DestroyImmediate(c);
        }
    }

    // ==========================================================================
    // NOUVEAU : filet de securite en plus de DisableLocalizationComponents.
    // Meme en detruisant le composant de localisation connu, on ne peut pas
    // garantir qu'aucun autre mecanisme (event de langue, re-application
    // differee, etc.) ne vienne un jour re-ecraser le texte de nos labels.
    // Ce composant force donc la valeur voulue sur tous les composants de
    // texte (Text classique ET TextMeshPro via reflexion) de son
    // GameObject a chaque frame (LateUpdate, donc apres tout autre script),
    // ce qui rend l'affichage du texte des boutons totalement fiable quel
    // que soit ce qui tente de le modifier ailleurs.
    // ==========================================================================
    public class PinnedLabelText : MonoBehaviour
    {
        public string Value = "";

        private Text[] _legacyTexts;
        private (Component comp, PropertyInfo prop)[] _tmpTexts;
        private bool _cached = false;

        private void CacheIfNeeded()
        {
            if (_cached) return;

            _legacyTexts = GetComponentsInChildren<Text>(true);
            _tmpTexts = GetComponentsInChildren<Component>(true)
                .Where(c => c != null && c.GetType().Name.Contains("TextMeshPro"))
                .Select(c => (comp: c, prop: c.GetType().GetProperty("text")))
                .Where(t => t.prop != null && t.prop.CanWrite)
                .ToArray();

            _cached = true;
        }

        private void Apply()
        {
            CacheIfNeeded();

            if (_legacyTexts != null)
            {
                foreach (var t in _legacyTexts)
                {
                    if (t != null && t.text != Value) t.text = Value;
                }
            }

            if (_tmpTexts != null)
            {
                foreach (var (comp, prop) in _tmpTexts)
                {
                    if (comp == null) continue;
                    string current = prop.GetValue(comp) as string;
                    if (current != Value) prop.SetValue(comp, Value);
                }
            }
        }

        private void LateUpdate()
        {
            Apply();
        }
    }

    private static void SetButtonLabel(GameObject buttonGo, string label)
    {
        bool foundAny = false;

        // Legacy Text
        Text[] allTexts = buttonGo.GetComponentsInChildren<Text>(true);
        foreach (Text txt in allTexts)
        {
            if (txt == null) continue;
            foundAny = true;

            txt.text = label;
            txt.enabled = true;
            txt.gameObject.SetActive(true);
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            // Les templates ont ete concus pour des boutons 400x110 ; dans la
            // grille les lignes ne font que ~38px de haut, donc une taille de
            // police heritee du template est presque toujours trop grande.
            if (txt.fontSize > 18 || txt.resizeTextForBestFit == false)
            {
                txt.fontSize = 18;
            }

            RectTransform txtRt = txt.rectTransform;
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = Vector2.zero;
            txtRt.offsetMax = Vector2.zero;
        }

        // TextMeshPro (via reflexion, sans dependance directe au package TMPro)
        var tmpComponents = buttonGo.GetComponentsInChildren<Component>(true)
            .Where(c => c != null && c.GetType().Name.Contains("TextMeshPro"));
        foreach (Component tmp in tmpComponents)
        {
            var textProp = tmp.GetType().GetProperty("text");
            if (textProp == null || !textProp.CanWrite) continue;
            foundAny = true;

            textProp.SetValue(tmp, label);
            tmp.gameObject.SetActive(true);

            // --- Forcer un rendu lisible quelle que soit la taille du template ---
            // Meme cause probable que pour Text : la police du template est
            // calee pour un bouton 400x110, beaucoup trop grande pour une
            // ligne de grille de 38px de haut, ce qui la fait disparaitre
            // entierement (overflow "Truncate"/"Page" par defaut de TMP) au
            // lieu de simplement deborder visuellement.
            TrySetTmpProperty(tmp, "enableAutoSizing", true);
            TrySetTmpProperty(tmp, "fontSizeMin", 8f);
            TrySetTmpProperty(tmp, "fontSizeMax", 20f);
            TrySetTmpProperty(tmp, "fontSize", 20f);
            TrySetTmpProperty(tmp, "enableWordWrapping", false);
            TrySetTmpProperty(tmp, "color", Color.white);

            // alignment : type TMPro.TextAlignmentOptions (enum), evite la
            // dependance directe en passant par le nom du membre "Center".
            var alignProp = tmp.GetType().GetProperty("alignment");
            if (alignProp != null && alignProp.CanWrite)
            {
                try
                {
                    var enumType = alignProp.PropertyType;
                    object centerValue = Enum.Parse(enumType, "Center");
                    alignProp.SetValue(tmp, centerValue);
                }
                catch { /* best effort */ }
            }

            RectTransform tmpRt = tmp.GetComponent<RectTransform>();
            if (tmpRt != null)
            {
                tmpRt.anchorMin = Vector2.zero;
                tmpRt.anchorMax = Vector2.one;
                tmpRt.offsetMin = Vector2.zero;
                tmpRt.offsetMax = Vector2.zero;
            }

            // Force la reconstruction immediate du maillage TMP : sans ca, le
            // texte peut rester invisible jusqu'a un rebuild de canvas
            // ulterieur (qui n'arrive pas forcement avant que le panneau ne
            // soit affiche).
            var forceMeshUpdate = tmp.GetType().GetMethod("ForceMeshUpdate", Type.EmptyTypes);
            if (forceMeshUpdate != null)
            {
                try { forceMeshUpdate.Invoke(tmp, null); } catch { }
            }
            else
            {
                var setAllDirty = tmp.GetType().GetMethod("SetAllDirty", Type.EmptyTypes);
                if (setAllDirty != null)
                {
                    try { setAllDirty.Invoke(tmp, null); } catch { }
                }
            }

            StaticLogger?.LogInfo(
                $"CheckpointsManager: [diag-label] '{buttonGo.name}' TMP '{tmp.GetType().Name}' texte='{label}' " +
                $"autoSize=applique fontSizeMax=20.");
        }

        if (!foundAny)
        {
            string allComponents = string.Join(", ", buttonGo.GetComponentsInChildren<Component>(true)
                .Select(c => c != null ? c.GetType().Name : "null")
                .Distinct());
            StaticLogger?.LogWarning(
                $"CheckpointsManager: aucun composant de texte trouve sur '{buttonGo.name}' pour y ecrire '{label}'. " +
                $"Composants presents (enfants inclus) : {allComponents}.");
        }

        PinnedLabelText pin = buttonGo.GetComponent<PinnedLabelText>();
        if (pin == null) pin = buttonGo.AddComponent<PinnedLabelText>();
        pin.Value = label;
    }

    // Petit helper reflexion pour poser une propriete TMP sans depedance
    // directe au package TMPro, en ignorant silencieusement toute propriete
    // absente ou de type incompatible (best effort).
    private static void TrySetTmpProperty(Component tmp, string propertyName, object value)
    {
        try
        {
            var prop = tmp.GetType().GetProperty(propertyName);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(tmp, value);
            }
        }
        catch { /* best effort */ }
    }

    private static void BuildCheckpointsPanel(Button buttonTemplate)
    {
        // Remonte jusqu'au Canvas parent : c'est la hierarchie UI qui
        // fonctionne deja dans ce jeu, contrairement a un Canvas cree de
        // toutes pieces par le plugin.
        Canvas parentCanvas = buttonTemplate.GetComponentInParent<Canvas>();
        Transform parent = parentCanvas != null ? parentCanvas.transform : buttonTemplate.transform.parent;

        // --- Calcul dynamique de la taille du panneau ---
        // Objectif : que TOUS les boutons (depart + checkpoints) soient
        // visibles sans avoir a scroller. On utilise une grille sur 2
        // colonnes plutot qu'une liste verticale pour limiter la hauteur
        // necessaire. Le ScrollRect reste present comme filet de securite
        // (ecran tres petit, ou liste qui grossirait plus tard) mais ne
        // devrait normalement jamais avoir besoin d'etre utilise.
        int totalEntries = GetAllCheckpointEntries().Count();
        const int Columns = 2;
        int rowCount = Mathf.CeilToInt(totalEntries / (float)Columns);

        const float RowHeight = 44f;
        const float RowSpacingY = 4f;
        const float RowSpacingX = 10f;
        const float GridPadding = 8f;
        const float TitleAreaHeight = 56f;
        const float CloseAreaHeight = 64f;
        const float PanelPaddingV = 24f; // marge haut/bas du ScrollView dans le panel

        float gridHeight = rowCount * RowHeight + Mathf.Max(0, rowCount - 1) * RowSpacingY + GridPadding * 2f;
        float desiredPanelHeight = TitleAreaHeight + CloseAreaHeight + PanelPaddingV + gridHeight;
        float panelHeight = Mathf.Min(desiredPanelHeight, Screen.height * 0.92f);

        const float PanelWidth = 640f;

        GameObject panel = new GameObject("CheckpointsListPanel");
        panel.transform.SetParent(parent, false);

        RectTransform panelRt = panel.AddComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.5f, 0.5f);
        panelRt.anchorMax = new Vector2(0.5f, 0.5f);
        panelRt.pivot = new Vector2(0.5f, 0.5f);
        panelRt.sizeDelta = new Vector2(PanelWidth, panelHeight);
        panelRt.anchoredPosition = Vector2.zero;

        Image bg = panel.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.85f);

        Font templateFont = buttonTemplate.GetComponentInChildren<Text>(true)?.font;

        // --- Titre ---
        GameObject titleGo = new GameObject("Title");
        titleGo.transform.SetParent(panel.transform, false);
        Text titleTxt = titleGo.AddComponent<Text>();
        titleTxt.text = "Checkpoints";
        titleTxt.font = templateFont;
        titleTxt.fontSize = 28;
        titleTxt.fontStyle = FontStyle.Bold;
        titleTxt.alignment = TextAnchor.UpperCenter;
        titleTxt.color = Color.white;
        RectTransform titleRt = titleTxt.rectTransform;
        titleRt.anchorMin = new Vector2(0f, 1f);
        titleRt.anchorMax = new Vector2(1f, 1f);
        titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.anchoredPosition = new Vector2(0f, -16f);
        titleRt.sizeDelta = new Vector2(0f, 40f);

        // --- Zone de defilement (filet de securite, plus utile en usage normal) ---
        GameObject scrollGo = new GameObject("ScrollView");
        scrollGo.transform.SetParent(panel.transform, false);
        RectTransform scrollRt = scrollGo.AddComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0f, 0f);
        scrollRt.anchorMax = new Vector2(1f, 1f);
        scrollRt.offsetMin = new Vector2(16f, CloseAreaHeight);
        scrollRt.offsetMax = new Vector2(-16f, -TitleAreaHeight);

        Image scrollMaskImage = scrollGo.AddComponent<Image>();
        scrollMaskImage.color = new Color(1f, 1f, 1f, 0.02f); // quasi invisible, requis par le Mask
        Mask scrollMask = scrollGo.AddComponent<Mask>();
        scrollMask.showMaskGraphic = false;
        ScrollRect scrollRect = scrollGo.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.viewport = scrollRt;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;

        GameObject contentGo = new GameObject("Content");
        contentGo.transform.SetParent(scrollGo.transform, false);
        RectTransform contentRt = contentGo.AddComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0f, 1f);
        contentRt.anchorMax = new Vector2(1f, 1f);
        contentRt.pivot = new Vector2(0.5f, 1f);
        contentRt.sizeDelta = new Vector2(0f, 0f);

        // Grille 2 colonnes au lieu d'une liste verticale : reduit la hauteur
        // totale necessaire pour afficher les 27 lignes (depart + checkpoints)
        // d'un coup, sans scroll.
        float columnWidth = (PanelWidth - 32f - GridPadding * 2f - RowSpacingX) / Columns;

        GridLayoutGroup grid = contentGo.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(columnWidth, RowHeight);
        grid.spacing = new Vector2(RowSpacingX, RowSpacingY);
        grid.padding = new RectOffset((int)GridPadding, (int)GridPadding, (int)GridPadding, (int)GridPadding);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = Columns;
        grid.startAxis = GridLayoutGroup.Axis.Vertical; // remplit une colonne avant de passer a la suivante
        grid.childAlignment = TextAnchor.UpperLeft;

        ContentSizeFitter fitter = contentGo.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scrollRect.content = contentRt;

        GameObject closeGo = UnityEngine.Object.Instantiate(buttonTemplate.gameObject, panel.transform, false);
        closeGo.name = "Button_Close";
        RectTransform closeRt = closeGo.GetComponent<RectTransform>();
        if (closeRt != null)
        {
            closeRt.anchorMin = new Vector2(0.5f, 0f);
            closeRt.anchorMax = new Vector2(0.5f, 0f);
            closeRt.pivot = new Vector2(0.5f, 0f);
            closeRt.anchoredPosition = new Vector2(0f, 16f);

            // Réduire la hauteur du bouton (valeur en pixels)
            float desiredHeight = 32f; // essaye 32, 28, 24 selon ce que tu veux
            closeRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, desiredHeight);

            // Optionnel : forcer aussi une largeur si nécessaire
            // float desiredWidth = 160f;
            // closeRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, desiredWidth);
        }
        DisableLocalizationComponents(closeGo);
        SetButtonLabel(closeGo, "Exit");
        Button closeButton = closeGo.GetComponent<Button>();
        closeButton.onClick = new Button.ButtonClickedEvent();
        closeButton.onClick.AddListener(() =>
        {
            // On ne passe plus par pauseMenu.DeactivatePauseMenu() (jamais
            // active pour F10) : on retire seulement les effets qu'on a
            // nous-memes appliques via OpenCheckpointsOverlay().
            _checkpointsPanel.SetActive(false);
            CloseCheckpointsOverlay();
        });

        // --- Une ligne par checkpoint, dans la grille ---
        _checkpointRowButtons.Clear();
        foreach (var entry in GetAllCheckpointEntries())
        {
            GameObject rowGo = UnityEngine.Object.Instantiate(buttonTemplate.gameObject, contentGo.transform, false);
            rowGo.name = $"Row_{entry.name}";

            DisableLocalizationComponents(rowGo);

            Button rowButton = rowGo.GetComponent<Button>();
            rowButton.onClick = new Button.ButtonClickedEvent();
            rowButton.interactable = entry.triggered;

            string capturedName = entry.name;
            rowButton.onClick.AddListener(() =>
            {
                GoToCheckpoint(capturedName);
                if (_checkpointsPanel != null) _checkpointsPanel.SetActive(false);
                CloseCheckpointsOverlay();
            });

            _checkpointRowButtons[entry.name] = rowButton;
        }

        RefreshCheckpointRows();

        panel.SetActive(false);
        _checkpointsPanel = panel;
    }

    private static void RefreshCheckpointRows()
    {
        foreach (var entry in GetAllCheckpointEntries())
        {
            if (_checkpointRowButtons.TryGetValue(entry.name, out Button btn) && btn != null)
            {
                btn.interactable = entry.triggered;
                SetButtonLabel(btn.gameObject, entry.triggered ? entry.name : $"{entry.name} (?)");
            }
        }
    }

    /// <summary>
    /// Enumere tous les checkpoints (celui de depart inclus, toujours
    /// "declenche") avec leur etat actuel, pour l'UI du menu Checkpoints.
    /// </summary>
    public static IEnumerable<(string name, bool triggered)> GetAllCheckpointEntries()
    {
        yield return (StartCheckpointName, true);

        if (_checkpoints != null)
        {
            foreach (var cp in _checkpoints)
            {
                yield return (cp.Name, cp.Triggered);
            }
        }
    }

    /// <summary>
    /// Teleporte le joueur vers un checkpoint donne par son nom, uniquement
    /// s'il s'agit du checkpoint de depart ou d'un checkpoint deja
    /// declenche. Met aussi a jour le "dernier checkpoint" utilise par F8.
    /// </summary>
    public static void GoToCheckpoint(string checkpointName)
    {
        Vector3 pos;

        if (checkpointName == StartCheckpointName)
        {
            pos = StartCheckpointPos;
        }
        else
        {
            CheckpointDef target = _checkpoints?.Find(c => c.Name == checkpointName);
            if (target == null || !target.Triggered)
            {
                StaticLogger?.LogWarning($"CheckpointsManager: teleportation refusee vers '{checkpointName}' (checkpoint inconnu ou non declenche).");
                return;
            }
            pos = target.RespawnPos;
        }

        if (_lastController == null)
        {
            StaticLogger?.LogWarning("CheckpointsManager: aucun controller joueur connu, teleportation impossible.");
            return;
        }

        Transform playerTransform = _lastController.transform;

        var cc = playerTransform.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        playerTransform.position = pos;

        var rb = playerTransform.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        TryResetVelocity(_lastController);

        if (cc != null) cc.enabled = true;

        _lastCheckpointPos = pos;
        _lastCheckpointName = checkpointName;

        string posStr = string.Format(CultureInfo.InvariantCulture,
            "({0:F2}, {1:F2}, {2:F2})", pos.x, pos.y, pos.z);
        StaticLogger?.LogInfo($"CheckpointsManager: teleportation manuelle vers '{checkpointName}' {posStr}.");
    }

    private static float _previousY;
    private static bool _hasPreviousY = false;

    private static void CheckCheckpointTriggers(Vector3 pos)
    {
        if (_checkpoints == null) return;

        // Memorise la hauteur de la frame precedente pour ne declencher un
        // checkpoint "Height" que sur un vrai FRANCHISSEMENT vers le haut
        // (previousY < seuil <= pos.y), pas simplement parce qu'on se
        // trouve au-dessus (ex: apres un teleport/respawn F8 directement
        // au-dessus du seuil, ce qui ne doit pas compter comme un passage).
        if (!_hasPreviousY)
        {
            _previousY = pos.y;
            _hasPreviousY = true;
        }

        foreach (var cp in _checkpoints)
        {
            if (cp.Triggered) continue;

            bool inside;
            if (cp.Type == CheckpointType.Height)
            {
                inside = _previousY < cp.HeightThreshold && pos.y >= cp.HeightThreshold;
            }
            else // Zone
            {
                inside = pos.x >= cp.MinX && pos.x <= cp.MaxX
                      && pos.y >= cp.MinY && pos.y <= cp.MaxY
                      && pos.z >= cp.MinZ && pos.z <= cp.MaxZ;
            }

            if (inside)
            {
                cp.Triggered = true;
                _lastCheckpointPos = cp.RespawnPos;
                _lastCheckpointName = cp.Name;

                string posStr = string.Format(CultureInfo.InvariantCulture,
                    "({0:F2}, {1:F2}, {2:F2})", cp.RespawnPos.x, cp.RespawnPos.y, cp.RespawnPos.z);
                StaticLogger?.LogInfo($"CheckpointsManager: '{cp.Name}' declenche {posStr}.");
                APConfig.MarkCheckpointUnlocked(cp.Name);
                PlayCheckpointSound();
                ShowFoundMessage(cp.Name);
            }
        }

        _previousY = pos.y;
    }

    // ==========================================================================
    // F8 : retour au dernier checkpoint
    // ==========================================================================
    private static void CheckRespawnKey(Transform playerTransform, CMF.MyWalkerController controller)
    {
        bool isDown = GetKeyIsDown(RespawnKey);
        if (isDown && !_lastRespawnKeyState)
        {
            RespawnPlayer(playerTransform, controller);
        }
        _lastRespawnKeyState = isDown;
    }

    private static void RespawnPlayer(Transform playerTransform, CMF.MyWalkerController controller)
    {
        var cc = playerTransform.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        playerTransform.position = _lastCheckpointPos;

        var rb = playerTransform.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // Best-effort : le controller CMF garde probablement sa propre
        // velocite interne (pas forcement un Rigidbody standard). On tente
        // de remettre a zero tout champ/propriete Vector3 dont le nom
        // contient "veloc" via reflexion, sans connaitre l'API exacte.
        TryResetVelocity(controller);

        if (cc != null) cc.enabled = true;

        string posStr = string.Format(CultureInfo.InvariantCulture,
            "({0:F2}, {1:F2}, {2:F2})", _lastCheckpointPos.x, _lastCheckpointPos.y, _lastCheckpointPos.z);
        StaticLogger?.LogInfo($"CheckpointsManager: retour au checkpoint '{_lastCheckpointName}' {posStr}.");
    }

    private static void TryResetVelocity(Component controller)
    {
        if (controller == null) return;

        Type type = controller.GetType();
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var f in fields)
        {
            try
            {
                if (f.FieldType == typeof(Vector3) && f.Name.ToLowerInvariant().Contains("veloc"))
                    f.SetValue(controller, Vector3.zero);
            }
            catch { /* best effort */ }
        }

        foreach (var p in props)
        {
            try
            {
                if (p.PropertyType == typeof(Vector3) && p.CanWrite && p.Name.ToLowerInvariant().Contains("veloc"))
                    p.SetValue(controller, Vector3.zero);
            }
            catch { /* best effort */ }
        }
    }

    // ==========================================================================
    // F9 : mode debug (visualisation des zones)
    // ==========================================================================
    private static void CheckDebugToggleKey()
    {
        bool isDown = GetKeyIsDown(DebugToggleKey);
        if (isDown && !_lastDebugKeyState)
        {
            _debugMode = !_debugMode;
            if (_debugMode) CreateAllDebugVisuals();
            else DestroyAllDebugVisuals();
            StaticLogger?.LogInfo($"CheckpointsManager: mode debug {(_debugMode ? "active" : "desactive")}.");
        }
        _lastDebugKeyState = isDown;
    }

    private static Material _debugMaterialTemplate;

    private static Material GetDebugMaterial(Color color)
    {
        // "Sprites/Default" est un shader unlit quasi toujours present (URP
        // ou non), qui respecte l'alpha du Material.color sans avoir besoin
        // de configurer des proprietes specifiques a une version d'URP.
        if (_debugMaterialTemplate == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            _debugMaterialTemplate = new Material(shader);
        }

        Material mat = new Material(_debugMaterialTemplate);
        Color c = color;
        c.a = 0.35f;
        mat.color = c;
        return mat;
    }

    private static void CreateAllDebugVisuals()
    {
        if (_checkpoints == null) return;

        foreach (var cp in _checkpoints)
        {
            if (cp.DebugVisual != null) continue;

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = $"CheckpointDebug_{cp.Name}";
            UnityEngine.Object.DontDestroyOnLoad(cube);

            var col = cube.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);

            var renderer = cube.GetComponent<Renderer>();
            renderer.material = GetDebugMaterial(cp.DebugColor);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            if (cp.Type == CheckpointType.Zone)
            {
                Vector3 center = new Vector3(
                    (cp.MinX + cp.MaxX) / 2f,
                    (cp.MinY + cp.MaxY) / 2f,
                    (cp.MinZ + cp.MaxZ) / 2f);
                Vector3 size = new Vector3(
                    Mathf.Max(cp.MaxX - cp.MinX, 0.1f),
                    Mathf.Max(cp.MaxY - cp.MinY, 0.1f),
                    Mathf.Max(cp.MaxZ - cp.MinZ, 0.1f));

                cube.transform.position = center;
                cube.transform.localScale = size;
            }
            else // Height : plan fin a l'altitude de seuil, centre sur le point de respawn
            {
                cube.transform.position = new Vector3(cp.RespawnPos.x, cp.HeightThreshold, cp.RespawnPos.z);
                cube.transform.localScale = new Vector3(cp.DebugPlaneSize, 0.2f, cp.DebugPlaneSize);
            }

            cp.DebugVisual = cube;
        }
    }

    private static void DestroyAllDebugVisuals()
    {
        if (_checkpoints == null) return;

        foreach (var cp in _checkpoints)
        {
            if (cp.DebugVisual != null)
            {
                UnityEngine.Object.Destroy(cp.DebugVisual);
                cp.DebugVisual = null;
            }
        }
    }

    // ==========================================================================
    // Entree clavier (Input System avec repli sur l'ancien Input si besoin)
    // ==========================================================================
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
                    if (control != null && control.isPressed)
                        isDown = true;
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

    private static void ShowFoundMessage(string checkpointName)
    {
        MessageHub.ShowMessage($"{checkpointName} found!");
    }


    // ==========================================================================
    // Affichage permanent en bas a droite : "Latest Checkpoint: ... (F8)"
    // Independant de MessageHub (qui gere les messages temporaires "found!"),
    // toujours visible pour rappeler ou F8 va teleporter le joueur.
    //
    // IMPORTANT : implemente en OnGUI (IMGUI), PAS en Canvas uGUI. Un essai
    // avec un Canvas (Screen Space - Overlay, sortingOrder tres eleve) ne
    // s'affichait pas du tout dans ce jeu (probablement un pipeline de
    // rendu / post-traitement custom qui ne composite pas le Canvas uGUI
    // correctement), alors que MessageHub (OnGUI) fonctionne et s'affiche
    // bien. On reste donc sur OnGUI, en calquant la structure de
    // MessageHub.GuiHost qui est confirmee fonctionnelle dans ce jeu.
    // ==========================================================================
    private static PersistentCheckpointGui _persistentGui;

    private static void EnsurePersistentGui()
    {
        // Verifie le "fake null" Unity (objet detruit cote moteur, meme si
        // la reference C# n'est pas litteralement null).
        if (_persistentGui != null) return;

        GameObject go = new GameObject("CheckpointsManager_PersistentGui");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _persistentGui = go.AddComponent<PersistentCheckpointGui>();
        StaticLogger?.LogInfo("CheckpointsManager: overlay OnGUI du dernier checkpoint cree.");
    }

    public class PersistentCheckpointGui : MonoBehaviour
    {
        private GUIStyle _style;
        private GUIStyle _shadowStyle;
        private bool _needsWarmup = true;
        private bool _hasLoggedRect = false;

        private void BuildStyle()
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.LowerRight,
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                wordWrap = false
            };
            _style.normal.textColor = Color.white;

            _shadowStyle = new GUIStyle(_style);
            _shadowStyle.normal.textColor = Color.black;
        }

        private void OnGUI()
        {
            // Meme technique de "matrix identity" + depth tres bas (donc
            // dessine par-dessus) que MessageHub.GuiHost, qui est confirme
            // visible dans ce jeu.
            GUI.matrix = Matrix4x4.identity;
            GUI.color = Color.white;
            GUI.depth = -1000;

            if (_needsWarmup)
            {
                BuildStyle();

                // Warm-up invisible : force la generation de l'atlas de la
                // police avant le premier vrai affichage, comme le fait
                // MessageHub.GuiHost, pour eviter tout probleme de premiere
                // frame.
                float warmWidth = 600f;
                float warmHeight = 60f;
                Rect warmRect = new Rect((Screen.width - warmWidth) / 2f, (Screen.height - warmHeight) / 2f, warmWidth, warmHeight);

                Color prevColor = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0f);
                GUI.Label(warmRect, "Latest Checkpoint: AaBbCc0123 (F8)", _style);
                GUI.Label(warmRect, "Checkpoint Menu (F10)", _style);
                GUI.color = prevColor;

                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    _needsWarmup = false;
                }
            }

            // Le menu pause (ou le menu Checkpoints, qui reproduit la meme
            // pause) a sa propre UI : on ne dessine pas l'overlay par-dessus
            // tant qu'on y est. Le warm-up ci-dessus continue de tourner
            // meme en pause pour eviter tout souci d'atlas de police au
            // moment de reprendre la partie.
            if (CheckpointsManager._isPaused)
            {
                return;
            }

            string text = $"Latest Checkpoint: {_lastCheckpointName} (F8)";
            string menuText = "Checkpoint Menu (F10)";

            float width = 560f;
            float height = 30f;
            float margin = 16f;
            float lineSpacing = 4f;

            Rect rect = new Rect(Screen.width - width - margin, Screen.height - height - margin, width, height);
            Rect shadowRect = new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height);

            // Ligne "Checkpoint Menu (F10)" juste au-dessus de "Latest Checkpoint",
            // meme style, meme largeur, decalee vers le haut d'une hauteur de ligne
            // + un petit espacement.
            Rect menuRect = new Rect(rect.x, rect.y - height - lineSpacing, rect.width, height);
            Rect menuShadowRect = new Rect(menuRect.x + 1.5f, menuRect.y + 1.5f, menuRect.width, menuRect.height);

            if (!_hasLoggedRect && Event.current != null && Event.current.type == EventType.Repaint)
            {
                _hasLoggedRect = true;
                StaticLogger?.LogInfo($"CheckpointsManager: overlay OnGUI - Screen=({Screen.width}x{Screen.height}), rect=({rect.x:F0},{rect.y:F0},{rect.width:F0},{rect.height:F0}).");
            }

            GUI.Label(menuShadowRect, menuText, _shadowStyle);
            GUI.Label(menuRect, menuText, _style);

            GUI.Label(shadowRect, text, _shadowStyle);
            GUI.Label(rect, text, _style);
        }
    }
}