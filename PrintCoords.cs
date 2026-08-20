using BepInEx;
using HarmonyLib;
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
[BepInPlugin("com.votreid.printcoords", "Print Coords (F3)", "1.0.0")]
public class PrintCoords : BaseUnityPlugin
{
    private static Harmony _harmony;
    private static BepInEx.Logging.ManualLogSource StaticLogger;
    private static KeyCode coordsKey = KeyCode.F3;
    private static bool lastCoordsKeyState = false;
    private void Awake()
    {
        StaticLogger = Logger;
        transform.SetParent(null);
        DontDestroyOnLoad(this.gameObject);
        if (_harmony == null)
        {
            _harmony = new Harmony("com.votreid.printcoords");
            _harmony.PatchAll();
            Logger.LogInfo("PrintCoords: Harmony patches applied.");
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
    private static void CheckCoordsKey(Transform playerTransform)
    {
        bool isDown = GetKeyIsDown(coordsKey);
        if (isDown && !lastCoordsKeyState)
        {
            Vector3 pos = playerTransform.position;
            string x = pos.x.ToString("F2", CultureInfo.InvariantCulture);
            string y = pos.y.ToString("F2", CultureInfo.InvariantCulture);
            string z = pos.z.ToString("F2", CultureInfo.InvariantCulture);
            StaticLogger?.LogInfo($"PrintCoords: Coordonnees joueur -> ({x}, {y}, {z})");
        }
        lastCoordsKeyState = isDown;
    }
    [HarmonyPatch(typeof(CMF.MyWalkerController), "HandleInput")]
    static class H_HandleInput_Patch
    {
        [HarmonyPostfix]
        static void Postfix(CMF.MyWalkerController __instance)
        {
            CheckCoordsKey(__instance.transform);
        }
    }
}