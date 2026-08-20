using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CMF.Traps
{
    [BepInPlugin("com.votreid.archipelago_traps", "Archipelago Traps", "1.0.0")]
    public class ArchipelagoTrapsPlugin : BaseUnityPlugin
    {
        private static Harmony _harmony;

        // Pour détecter le "appui" (front montant) avec le new Input System,
        // comme dans DisableJumpToggle_Clean.
        private bool lastTestKeyState;

        private void Awake()
        {
            transform.SetParent(null);
            DontDestroyOnLoad(this.gameObject);

            TrapManager.Log = Logger;

            this.gameObject.AddComponent<TrapManager>();

            if (_harmony == null)
            {
                _harmony = new Harmony("com.votreid.archipelago_traps");
                _harmony.PatchAll();
                Logger.LogInfo("[ArchipelagoTraps] Harmony patches appliqués.");
            }

            Logger.LogInfo("[ArchipelagoTraps] Plugin prêt. Exemple d'usage : " +
                "TrapManager.Instance.ApplyTrapById(\"DeadweightTrap\");");
        }

        private void Update()
        {
            bool isDown = false;

            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                isDown = kb.tKey.isPressed;
            }

            if (isDown && !lastTestKeyState)
            {
                Logger.LogInfo("[ArchipelagoTraps] Touche T détectée -> ApplyTrapById(\"DeadweightTrap\")");
                TrapManager.Instance?.ApplyTrapById("DeadweightTrap");
            }

            lastTestKeyState = isDown;
        }
    }
}