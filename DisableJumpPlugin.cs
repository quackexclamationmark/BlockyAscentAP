using BepInEx;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

[BepInPlugin("com.votreid.disablejump_toggle", "DisableJump Toggle (clean)", "1.8.0")]
public class DisableJumpToggle_Clean : BaseUnityPlugin
{
    private static Harmony _harmony;

    // IMPORTANT : ce flag est static, il est donc lu/écrit par les patches Harmony
    // (qui sont globaux/statiques) SANS dépendre de la survie de l'instance du plugin.
    private static bool blockingEnabled = true;

    private static KeyCode toggleKey = KeyCode.C;
    private static bool lastToggleKeyState = false;

    private static BepInEx.Logging.ManualLogSource StaticLogger;

    private void OnEnable() => Logger.LogInfo("[LIFECYCLE] OnEnable");
    private void OnDisable() => Logger.LogInfo("[LIFECYCLE] OnDisable");
    private void OnDestroy() => Logger.LogInfo("[LIFECYCLE] OnDestroy (frame=" + Time.frameCount + ") - sans impact : la detection tourne maintenant dans le patch Harmony, pas ici.");

    private void Awake()
    {
        StaticLogger = Logger;

        Logger.LogInfo($"[DEBUG-PARENT] transform.parent={(transform.parent == null ? "NULL (root)" : transform.parent.name)} | transform.root={transform.root.name} | scene={gameObject.scene.name}");

        transform.SetParent(null);
        DontDestroyOnLoad(this.gameObject);

        Logger.LogInfo("DisableJumpToggle_Clean Awake - starting (1.8.0, detection deplacee dans le patch Harmony)");

        if (_harmony == null)
        {
            _harmony = new Harmony("com.votreid.disablejump_toggle_clean");
            _harmony.PatchAll();
            Logger.LogInfo("DisableJumpToggle_Clean: Harmony patches appliques une seule fois, ils restent actifs meme si ce GameObject est detruit.");
        }

        blockingEnabled = true;
    }

    private static void CheckToggleKey()
    {
        bool isDown = false;

        try
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                try
                {
                    Key keyEnum = (Key)Enum.Parse(typeof(Key), toggleKey.ToString(), true);
                    var control = kb[keyEnum];
                    if (control != null && control.isPressed)
                        isDown = true;
                }
                catch
                {
                    foreach (var k in kb.allKeys)
                    {
                        if (k.isPressed && string.Equals(k.displayName, toggleKey.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            isDown = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                try { isDown = UnityEngine.Input.GetKey(toggleKey); } catch { }
            }
        }
        catch { }

        if (isDown && !lastToggleKeyState)
        {
            blockingEnabled = !blockingEnabled;
            StaticLogger?.LogInfo($"DisableJumpToggle_Clean: Toggle (C) -> blockingEnabled = {blockingEnabled}");
        }

        lastToggleKeyState = isDown;
    }

    // ---------------- Harmony patches ----------------

    [HarmonyPatch(typeof(CMF.MyWalkerController), "HandleJump")]
    static class H_HandleJump_Patch
    {
        [HarmonyPrefix]
        static bool Prefix()
        {
            // Si le blocage est desactive, on laisse la methode originale s'executer normalement.
            return !blockingEnabled;
        }
    }

    [HarmonyPatch(typeof(CMF.MyWalkerController), "HandleInput")]
    static class H_HandleInput_Patch
    {
        [HarmonyPostfix]
        static void Postfix(CMF.MyWalkerController __instance)
        {
            // Detection du toggle ici : cette methode tourne chaque frame tant que
            // le controller du joueur est actif, independamment de notre plugin GameObject.
            CheckToggleKey();

            if (!blockingEnabled) return;

            try
            {
                var fi = __instance.GetType().GetField("inputJump", BindingFlags.Instance | BindingFlags.NonPublic);
                if (fi != null) fi.SetValue(__instance, false);
            }
            catch { }
        }
    }

    // Reactive automatiquement le saut (comme un appui sur C) quand le texte de fin de
    // tutoriel est reellement declenche (TutorialCompleteTriggered), pas juste quand
    // l'objet demarre.
    [HarmonyPatch(typeof(TutorialCompleteMessage), "TutorialCompleteTriggered")]
    static class H_TutorialCompleteMessage_Triggered_Patch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (blockingEnabled)
            {
                blockingEnabled = false;
                StaticLogger?.LogInfo("DisableJumpToggle_Clean: TutorialCompleteTriggered detecte -> blockingEnabled = False (saut reactive automatiquement)");
            }
        }
    }
}