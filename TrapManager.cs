using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace CMF.Traps
{
    /// <summary>
    /// Gère le cycle de vie des traps Archipelago.
    ///
    /// N'a besoin d'être posé nulle part via l'éditeur Unity : le plugin
    /// (voir ArchipelagoTrapsPlugin.cs) l'ajoute lui-même en code avec
    /// gameObject.AddComponent&lt;TrapManager&gt;() sur son propre GameObject
    /// persistant (DontDestroyOnLoad). Il n'a aucune référence directe au
    /// joueur : la communication avec MyWalkerController se fait uniquement
    /// via les flags statiques IsMovementLocked / IsJumpLocked, lus par les
    /// patches Harmony dans TrapPatches.cs.
    ///
    /// Pour ajouter un nouveau trap plus tard :
    ///   1. Crée une classe qui implémente ITrap (comme DeadweightTrap.cs).
    ///   2. Ajoute une ligne RegisterTrap("MonTrapId", () => new MonTrap()); dans RegisterDefaultTraps().
    ///   3. Si le trap doit affecter autre chose que mouvement/saut, ajoute le flag
    ///      correspondant dans ITrap + le patch Harmony qui va avec dans TrapPatches.cs.
    /// </summary>
    public class TrapManager : MonoBehaviour
    {
        public static TrapManager Instance { get; private set; }
        public static ManualLogSource Log { get; set; }

        // Flags globaux lus par les patches Harmony.
        public static bool IsMovementLocked { get; private set; }
        public static bool IsJumpLocked { get; private set; }

        private readonly Dictionary<string, Func<ITrap>> trapRegistry =
            new Dictionary<string, Func<ITrap>>(StringComparer.OrdinalIgnoreCase);

        private class ActiveTrapEntry
        {
            public ITrap trap;
            public float remaining;
        }

        private readonly List<ActiveTrapEntry> activeTraps = new List<ActiveTrapEntry>();

        private void Awake()
        {
            Instance = this;
            RegisterDefaultTraps();
        }

        private void RegisterDefaultTraps()
        {
            RegisterTrap("DeadweightTrap", () => new DeadweightTrap());
            // Ajoute les futurs traps ici, par ex. :
            // RegisterTrap("ReverseControlsTrap", () => new ReverseControlsTrap());
            // RegisterTrap("SlowdownTrap", () => new SlowdownTrap());
        }

        public void RegisterTrap(string trapId, Func<ITrap> factory)
        {
            trapRegistry[trapId] = factory;
        }

        /// <summary>
        /// À appeler depuis ton handler de réception d'item Archipelago,
        /// avec le nom/id de l'item reçu.
        /// </summary>
        public void ApplyTrapById(string trapId)
        {
            if (!trapRegistry.TryGetValue(trapId, out Func<ITrap> factory))
            {
                Log?.LogWarning($"[TrapManager] Aucun trap enregistré pour l'id '{trapId}'.");
                return;
            }

            ApplyTrap(factory.Invoke());
        }

        public void ApplyTrap(ITrap trap)
        {
            Type trapType = trap.GetType();

            // Si le même type de trap est déjà actif, on l'interrompt proprement
            // avant de relancer celui-ci (= refresh de la durée, pas de cumul).
            int existingIndex = activeTraps.FindIndex(e => e.trap.GetType() == trapType);
            if (existingIndex >= 0)
            {
                activeTraps[existingIndex].trap.OnRemove();
                activeTraps.RemoveAt(existingIndex);
            }

            trap.OnApply();
            activeTraps.Add(new ActiveTrapEntry { trap = trap, remaining = trap.Duration });
            RecomputeFlags();
        }

        private void Update()
        {
            if (activeTraps.Count == 0)
            {
                return;
            }

            for (int i = activeTraps.Count - 1; i >= 0; i--)
            {
                ActiveTrapEntry entry = activeTraps[i];
                entry.remaining -= Time.deltaTime;
                if (entry.remaining <= 0f)
                {
                    entry.trap.OnRemove();
                    activeTraps.RemoveAt(i);
                }
            }

            RecomputeFlags();
        }

        private void RecomputeFlags()
        {
            bool movementLocked = false;
            bool jumpLocked = false;

            foreach (ActiveTrapEntry entry in activeTraps)
            {
                movementLocked |= entry.trap.LocksMovement;
                jumpLocked |= entry.trap.LocksJump;
            }

            IsMovementLocked = movementLocked;
            IsJumpLocked = jumpLocked;
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