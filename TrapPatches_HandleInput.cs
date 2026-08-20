using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CMF.Traps
{
    // Harmony patches pour faire respecter les flags de TrapManager
    [HarmonyPatch(typeof(CMF.MyWalkerController), "HandleInput")]
    static class TrapPatches_HandleInput
    {
        [HarmonyPostfix]
        static void Postfix(CMF.MyWalkerController __instance)
        {
            try
            {
                Type t = __instance.GetType();
                BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic;

                // champs privés dans MyWalkerController
                var fiInputH = t.GetField("inputHorizontal", bf);
                var fiInputV = t.GetField("inputVertical", bf);
                var fiInputJump = t.GetField("inputJump", bf);

                // Si movement locked, force 0 sur les inputs horizontaux/verticaux
                if (TrapManager.IsMovementLocked)
                {
                    if (fiInputH != null) fiInputH.SetValue(__instance, 0f);
                    if (fiInputV != null) fiInputV.SetValue(__instance, 0f);
                }

                // Si jump locked, force le flag de saut à false
                if (TrapManager.IsJumpLocked)
                {
                    if (fiInputJump != null) fiInputJump.SetValue(__instance, false);
                }
            }
            catch (Exception ex)
            {
                TrapManager.Log?.LogWarning($"[TrapPatches] Exception in HandleInput postfix: {ex.Message}");
            }
        }
    }
}