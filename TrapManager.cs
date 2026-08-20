using BepInEx.Logging;
using CMF.Traps;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace CMF.Traps
{
    public class TrapManager : MonoBehaviour
    {
        public static TrapManager Instance { get; private set; }
        public static ManualLogSource Log { get; set; }

        public static bool IsMovementLocked { get; private set; }
        public static bool IsJumpLocked { get; private set; }

        // registry: id -> factory
        private readonly Dictionary<string, Func<ITrap>> trapRegistry =
            new Dictionary<string, Func<ITrap>>(StringComparer.OrdinalIgnoreCase);

        // optional display names: id -> display name
        private readonly Dictionary<string, string> displayNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private class ActiveTrapEntry
        {
            public ITrap trap;
            public float remaining;
        }

        private readonly List<ActiveTrapEntry> activeTraps = new List<ActiveTrapEntry>();

        private struct TrapRequest
        {
            public string Id;
            public string Sender;
        }

        private readonly Queue<TrapRequest> pendingTrapRequests = new Queue<TrapRequest>();
        private readonly object pendingLock = new object();

        // --- Trap sound resources (loaded from Assets/trap.wav next to the plugin DLL) ---
        private static AudioSource _audioSource;
        private static AudioClip _trapClip;
        private const string AssetsSubfolder = "Assets";
        private const string TrapSoundBaseName = "trap";
        private const float TrapSoundVolume = 0.4f;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                RegisterDefaultTraps();
                TryLoadTrapSoundFromPluginFolder();
                Log?.LogInfo("[TrapManager] Awake complete.");
            }
            else
            {
                Destroy(this);
            }
        }

        private void RegisterDefaultTraps()
        {
            RegisterTrap("DeadweightTrap", () => new DeadweightTrap(), "Deadweight Trap");
            RegisterTrap("InvertCamTrap", () => new InvertControlsTrap(), "Invert Camera Trap");
        }

        // --- registration API ---

        // classique : register sans displayName
        public void RegisterTrap(string trapId, Func<ITrap> factory)
        {
            RegisterTrap(trapId, factory, null);
        }

        // overload : register avec displayName optionnel
        public void RegisterTrap(string trapId, Func<ITrap> factory, string displayName)
        {
            if (string.IsNullOrEmpty(trapId) || factory == null) return;
            trapRegistry[trapId] = factory;
            if (!string.IsNullOrEmpty(displayName))
                displayNames[trapId] = displayName;
            Log?.LogInfo($"[TrapManager] Registered trap '{trapId}' (display='{GetDisplayName(trapId)}').");
        }

        // permet modifier le nom d'affichage a la volée
        public void SetTrapDisplayName(string trapId, string displayName)
        {
            if (string.IsNullOrEmpty(trapId)) return;
            if (string.IsNullOrEmpty(displayName))
                displayNames.Remove(trapId);
            else
                displayNames[trapId] = displayName;
            Log?.LogInfo($"[TrapManager] Set display name for '{trapId}' to '{displayName}'.");
        }

        public string GetDisplayName(string trapId)
        {
            if (string.IsNullOrEmpty(trapId)) return trapId;
            if (displayNames.TryGetValue(trapId, out var dn) && !string.IsNullOrEmpty(dn)) return dn;
            return trapId;
        }

        // --- enqueue / apply API (sender optionnel) ---

        public void EnqueueTrap(string trapId, string sender = null)
        {
            if (string.IsNullOrEmpty(trapId)) return;
            lock (pendingLock)
            {
                pendingTrapRequests.Enqueue(new TrapRequest { Id = trapId, Sender = sender });
            }
        }

        public bool ApplyTrapById(string trapId, string sender = null)
        {
            if (string.IsNullOrEmpty(trapId))
            {
                Log?.LogWarning("[TrapManager] ApplyTrapById called with empty id.");
                return false;
            }

            if (!trapRegistry.TryGetValue(trapId, out Func<ITrap> factory))
            {
                Log?.LogWarning($"[TrapManager] Aucun trap enregistré pour l'id '{trapId}'.");
                return false;
            }

            try
            {
                var trap = factory.Invoke();

                // Notification + sound
                ShowTrapNotification(trapId, sender);

                ApplyTrap(trap);
                return true;
            }
            catch (Exception ex)
            {
                Log?.LogError($"[TrapManager] Exception while applying trap '{trapId}': {ex}");
                return false;
            }
        }

        private void ApplyTrap(ITrap trap)
        {
            Type trapType = trap.GetType();

            int existingIndex = activeTraps.FindIndex(e => e.trap.GetType() == trapType);
            if (existingIndex >= 0)
            {
                activeTraps[existingIndex].trap.OnRemove();
                activeTraps.RemoveAt(existingIndex);
            }

            trap.OnApply();
            activeTraps.Add(new ActiveTrapEntry { trap = trap, remaining = trap.Duration });
            RecomputeFlags();
            Log?.LogInfo($"[TrapManager] Applied trap '{trap.TrapId}' (duration={trap.Duration}s).");
        }

        private void Update()
        {
            DrainPendingQueue();

            if (activeTraps.Count == 0) return;

            for (int i = activeTraps.Count - 1; i >= 0; i--)
            {
                var entry = activeTraps[i];
                entry.remaining -= Time.deltaTime;
                if (entry.remaining <= 0f)
                {
                    entry.trap.OnRemove();
                    activeTraps.RemoveAt(i);
                }
            }

            RecomputeFlags();
        }

        private void DrainPendingQueue()
        {
            while (true)
            {
                TrapRequest req;
                lock (pendingLock)
                {
                    if (pendingTrapRequests.Count == 0) break;
                    req = pendingTrapRequests.Dequeue();
                }

                if (!string.IsNullOrEmpty(req.Id))
                {
                    bool ok = ApplyTrapById(req.Id, req.Sender);
                    if (!ok)
                        Log?.LogWarning($"[TrapManager] Failed to apply enqueued trap '{req.Id}'.");
                }
            }
        }

        private void RecomputeFlags()
        {
            bool movementLocked = false;
            bool jumpLocked = false;

            foreach (var entry in activeTraps)
            {
                movementLocked |= entry.trap.LocksMovement;
                jumpLocked |= entry.trap.LocksJump;
            }

            IsMovementLocked = movementLocked;
            IsJumpLocked = jumpLocked;
        }

        private void ShowTrapNotification(string trapId, string sender)
        {
            try
            {
                string display = GetDisplayName(trapId);
                string message = string.IsNullOrEmpty(sender)
                    ? $"{display} sent!"
                    : $"{display} sent by {sender}";
                MessageHub.ShowMessage(message);

                // play trap sound if loaded
                PlayTrapSound();
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[TrapManager] Failed to show trap notification: {ex.Message}");
            }
        }

        // ---------------- Trap sound loader/player ----------------

        private void TryLoadTrapSoundFromPluginFolder()
        {
            try
            {
                // Best-effort: locate assembly folder where this class is defined (plugin DLL)
                string asmPath = Path.GetDirectoryName(typeof(TrapManager).Assembly.Location);
                if (string.IsNullOrEmpty(asmPath)) return;

                string wavPath = Path.Combine(asmPath, AssetsSubfolder, TrapSoundBaseName + ".wav");
                if (!File.Exists(wavPath))
                {
                    Log?.LogInfo($"[TrapManager] trap.wav not found at '{wavPath}'. Skipping sound load.");
                    return;
                }

                _trapClip = LoadWav(wavPath, TrapSoundBaseName);
                if (_trapClip != null)
                {
                    Log?.LogInfo($"[TrapManager] Loaded trap sound from '{wavPath}'.");
                }
                else
                {
                    Log?.LogWarning($"[TrapManager] Failed to parse trap.wav at '{wavPath}'.");
                }
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[TrapManager] Exception while loading trap sound: {ex.Message}");
            }
        }

        private static void EnsureAudioSource()
        {
            if (_audioSource != null) return;

            GameObject host = new GameObject("TrapSound_AudioHost");
            UnityEngine.Object.DontDestroyOnLoad(host);

            _audioSource = host.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f; // 2D sound
            _audioSource.volume = TrapSoundVolume;
        }

        private static void PlayTrapSound()
        {
            if (_trapClip == null)
            {
                // nothing loaded
                return;
            }

            EnsureAudioSource();
            _audioSource.PlayOneShot(_trapClip);
        }

        // Minimal WAV parser copied/adapted from CollectiblesManager.LoadWav
        private static AudioClip LoadWav(string path, string clipName)
        {
            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                Log?.LogWarning($"[TrapManager] cannot read '{path}' ({e.Message})");
                return null;
            }

            if (fileBytes.Length < 44
                || Encoding.ASCII.GetString(fileBytes, 0, 4) != "RIFF"
                || Encoding.ASCII.GetString(fileBytes, 8, 4) != "WAVE")
            {
                Log?.LogWarning($"[TrapManager] '{path}' is not a valid WAV file.");
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

                pos = chunkDataStart + chunkSize + (chunkSize % 2);
            }

            if (dataStart < 0 || channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0)
            {
                Log?.LogWarning($"[TrapManager] WAV header incomplete or unsupported in '{path}'.");
                return null;
            }

            if (dataStart + dataSize > fileBytes.Length)
                dataSize = fileBytes.Length - dataStart;

            int bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample <= 0)
            {
                Log?.LogWarning($"[TrapManager] invalid bitsPerSample ({bitsPerSample}) in '{path}'.");
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
                    else // PCM 32-bit int
                    {
                        for (int i = 0; i < sampleCount; i++)
                            samples[i] = BitConverter.ToInt32(fileBytes, dataStart + i * 4) / 2147483648f;
                    }
                    break;

                default:
                    Log?.LogWarning($"[TrapManager] unsupported bit depth ({bitsPerSample}) in '{path}'.");
                    return null;
            }

            AudioClip clip = AudioClip.Create(clipName, sampleCount / channels, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }

    public class DeadweightTrap : ITrap
    {
        public string TrapId => "DeadweightTrap";
        public float Duration => 5f;
        public bool LocksMovement => true;
        public bool LocksJump => true;

        public void OnApply()
        {
            TrapManager.Log?.LogInfo("[DeadweightTrap] Appliqué : mouvement et saut bloqués pendant 5s.");
        }

        public void OnRemove()
        {
            TrapManager.Log?.LogInfo("[DeadweightTrap] Terminé : mouvement et saut débloqués.");
        }
    }
}

public class InvertControlsTrap : ITrap
{
    public string TrapId => "InvertCamTrap";
    public float Duration => 10f;
    public bool LocksMovement => false;
    public bool LocksJump => false;

    private bool prevInvertX;
    private bool prevInvertY;
    private bool applied = false;

    public void OnApply()
    {
        prevInvertX = PlayerPrefs.GetInt("InvertX", 0) == 1;
        prevInvertY = PlayerPrefs.GetInt("InvertY", 0) == 1;

        bool newInvertX = !prevInvertX;
        bool newInvertY = !prevInvertY;

        PlayerPrefs.SetInt("InvertX", newInvertX ? 1 : 0);
        PlayerPrefs.SetInt("InvertY", newInvertY ? 1 : 0);
        PlayerPrefs.Save();

        TryApplyGameSettings();

        TrapManager.Log?.LogInfo($"[InvertControlsTrap] Applied: InvertX {prevInvertX} -> {newInvertX}, InvertY {prevInvertY} -> {newInvertY}");
        applied = true;
    }

    public void OnRemove()
    {
        if (!applied) return;

        PlayerPrefs.SetInt("InvertX", prevInvertX ? 1 : 0);
        PlayerPrefs.SetInt("InvertY", prevInvertY ? 1 : 0);
        PlayerPrefs.Save();

        TryApplyGameSettings();

        TrapManager.Log?.LogInfo("[InvertControlsTrap] Removed: restored previous invert settings.");
        applied = false;
    }

    private void TryApplyGameSettings()
    {
        try
        {
            // Cherche un type ReferenceManager dans les assemblies chargés
            var rmType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); } catch { return Type.EmptyTypes; }
                })
                .FirstOrDefault(t => string.Equals(t.Name, "ReferenceManager", StringComparison.OrdinalIgnoreCase));

            if (rmType == null) return;

            // Récupère Instance (prop ou field)
            object rmInstance = null;
            var instProp = rmType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (instProp != null) rmInstance = instProp.GetValue(null);
            else
            {
                var instField = rmType.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (instField != null) rmInstance = instField.GetValue(null);
            }
            if (rmInstance == null) return;

            // Cherche un champ/propriété 'applySettings' sur ReferenceManager
            var applyField = rmType.GetField("applySettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object applyObj = null;
            if (applyField != null) applyObj = applyField.GetValue(rmInstance);
            else
            {
                var applyProp = rmType.GetProperty("applySettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (applyProp != null) applyObj = applyProp.GetValue(rmInstance);
            }

            if (applyObj == null) return;

            // Appelle la méthode Apply() si présente
            var applyMethod = applyObj.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            applyMethod?.Invoke(applyObj, null);
        }
        catch (Exception ex)
        {
            TrapManager.Log?.LogWarning($"[InvertControlsTrap] Failed to apply settings via ReferenceManager: {ex.Message}");
        }
    }
}