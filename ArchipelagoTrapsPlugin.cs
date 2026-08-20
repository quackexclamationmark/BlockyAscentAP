using BepInEx;
using HarmonyLib;
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using BepInEx.Logging;

namespace CMF.Traps
{
    [BepInPlugin("com.votreid.archipelago_traps", "Archipelago Traps", "1.0.0")]
    public class ArchipelagoTrapsPlugin : BaseUnityPlugin
    {
        private static Harmony _harmony;

        // Detection keys
        private static KeyCode testKey = KeyCode.T; // existing test trap
        private static KeyCode invertKey = KeyCode.Y; // new: invert controls trap via Y
        private static bool lastTestKeyState = false;
        private static bool lastInvertKeyState = false;
        private static ManualLogSource StaticLogger;

        private void Awake()
        {
            StaticLogger = Logger;

            transform.SetParent(null);
            DontDestroyOnLoad(this.gameObject);

            TrapManager.Log = Logger;

            var existing = FindObjectOfType<TrapManager>();
            if (existing == null)
            {
                this.gameObject.AddComponent<TrapManager>();
                Logger.LogInfo("[ArchipelagoTraps] TrapManager component added to plugin GameObject.");
            }
            else
            {
                Logger.LogInfo("[ArchipelagoTraps] Found existing TrapManager in scene.");
            }

            if (_harmony == null)
            {
                _harmony = new Harmony("com.votreid.archipelago_traps");
                _harmony.PatchAll();
                Logger.LogInfo("[ArchipelagoTraps] Harmony patches appliqués.");
            }

            Logger.LogInfo("[ArchipelagoTraps] Plugin prêt. Exemple d'usage : TrapManager.Instance.ApplyTrapById(\"DeadweightTrap\");");
        }

        private static void EnsureTrapManagerExists()
        {
            if (TrapManager.Instance != null) return;

            var found = UnityEngine.Object.FindObjectOfType<TrapManager>();
            if (found != null)
            {
                TrapManager.Log = StaticLogger;
                return;
            }

            try
            {
                var go = new GameObject("ArchipelagoTrapManager");
                UnityEngine.Object.DontDestroyOnLoad(go);
                var tm = go.AddComponent<TrapManager>();
                TrapManager.Log = StaticLogger;
                StaticLogger?.LogInfo("[ArchipelagoTraps] Created fallback ArchipelagoTrapManager GameObject and added TrapManager component.");
            }
            catch (Exception ex)
            {
                StaticLogger?.LogError($"[ArchipelagoTraps] Failed to create TrapManager fallback: {ex}");
            }
        }

        public static void ReceiveRemoteTrap(string trapId, string senderName = null)
        {
            if (string.IsNullOrEmpty(trapId))
            {
                TrapManager.Log?.LogWarning("ReceiveRemoteTrap called with null/empty trapId.");
                return;
            }

            if (TrapManager.Instance != null)
            {
                TrapManager.Instance.EnqueueTrap(trapId, senderName);
                TrapManager.Log?.LogInfo($"ReceiveRemoteTrap enqueued '{trapId}' from sender '{senderName ?? "<unknown>"}'.");
            }
            else
            {
                TrapManager.Log?.LogWarning("ReceiveRemoteTrap: TrapManager.Instance is null; cannot enqueue trap.");
            }
        }

        // Detecte front-edge pour T (test) et Y (invert). Supporte clavier et manette (Gamepad.current).
        private static void CheckKeysAndApplyTraps()
        {
            bool isTestDown = false;
            bool isInvertDown = false;

            try
            {
                var kb = Keyboard.current;
                var gp = Gamepad.current;

                if (kb != null)
                {
                    // Keyboard detection
                    try
                    {
                        Key keyTestEnum = (Key)Enum.Parse(typeof(Key), testKey.ToString(), true);
                        if (kb[keyTestEnum].isPressed) isTestDown = true;
                    }
                    catch
                    {
                        foreach (var k in kb.allKeys)
                        {
                            if (k.isPressed && string.Equals(k.displayName, testKey.ToString(), StringComparison.OrdinalIgnoreCase))
                            {
                                isTestDown = true; break;
                            }
                        }
                    }

                    try
                    {
                        Key keyInvEnum = (Key)Enum.Parse(typeof(Key), invertKey.ToString(), true);
                        if (kb[keyInvEnum].isPressed) isInvertDown = true;
                    }
                    catch
                    {
                        foreach (var k in kb.allKeys)
                        {
                            if (k.isPressed && string.Equals(k.displayName, invertKey.ToString(), StringComparison.OrdinalIgnoreCase))
                            {
                                isInvertDown = true; break;
                            }
                        }
                    }
                }
                else
                {
                    // Legacy Input fallback for keyboard
                    try { isTestDown = UnityEngine.Input.GetKey(testKey); } catch { }
                    try { isInvertDown = UnityEngine.Input.GetKey(invertKey); } catch { }
                }

                // Gamepad detection (buttonNorth = Y on Xbox, also covers controllers using Input System)
                if (gp != null)
                {
                    if (gp.buttonNorth.isPressed) isInvertDown = true;
                    // you could also map testKey to a gamepad button if desired
                }
            }
            catch (Exception ex)
            {
                StaticLogger?.LogWarning($"[ArchipelagoTraps] Exception detecting keys/gamepad: {ex.Message}");
            }

            // Front-edge: T
            if (isTestDown && !lastTestKeyState)
            {
                StaticLogger?.LogInfo($"[ArchipelagoTraps] Test key {testKey} front-edge detected -> applying DeadweightTrap");
                EnsureTrapManagerExists();
                if (TrapManager.Instance != null)
                {
                    TrapManager.Instance.ApplyTrapById("DeadweightTrap");
                }
                else
                {
                    StaticLogger?.LogWarning("[ArchipelagoTraps] TrapManager.Instance is still null after EnsureTrapManagerExists().");
                }
            }

            // Front-edge: Y / gamepad buttonNorth
            if (isInvertDown && !lastInvertKeyState)
            {
                StaticLogger?.LogInfo($"[ArchipelagoTraps] Invert key {invertKey} front-edge detected -> applying InvertControlsTrap");
                EnsureTrapManagerExists();
                if (TrapManager.Instance != null)
                {
                    TrapManager.Instance.ApplyTrapById("InvertCamTrap");
                }
                else
                {
                    StaticLogger?.LogWarning("[ArchipelagoTraps] TrapManager.Instance is still null after EnsureTrapManagerExists().");
                }
            }

            lastTestKeyState = isTestDown;
            lastInvertKeyState = isInvertDown;
        }

        [HarmonyPatch(typeof(CMF.MyWalkerController), "HandleInput")]
        static class H_HandleInput_Patch
        {
            [HarmonyPostfix]
            static void Postfix(CMF.MyWalkerController __instance)
            {
                CheckKeysAndApplyTraps();
            }
        }
    }
}