using System.Collections.Generic;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using Engine.Behaviours.Components;
using Engine.Behaviours.Engines.Controllers;
using Engine.Common;
using Engine.Common.Services;
using Engine.Source.Commons;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// Freezes NPCs only, while the player keeps moving. The game's own world pause (EngineApplication.IsPaused) cannot be
    /// used: BehaviorComponent passes it to every entity's movement controller, the player's too. So the mod does per NPC what
    /// the pause does, and a bit more:
    ///  - NpcState.Update (the NPC state machine: walking, idling, fighting) is skipped;
    ///  - running BehaviorDesigner trees are paused (DisableBehavior(pause: true), as BehaviorComponent.WorldPauseHandler does);
    ///  - EngineBehavior.IsPaused (movement controller) is set, except for the player's controller;
    ///  - animators get speed 0, NavMeshAgents are stopped; head tracking and aiming are held (IK prefixes below).
    /// No load while not in use: the Harmony patches below (NpcState.Update, IK callbacks) are installed when the freeze is
    /// switched on and removed on resume, and nothing runs per frame otherwise. NPCs are found once, on switching on
    /// (NPCs appearing later are not frozen); while frozen, a few of them per frame are re-checked, because the game may
    /// undo the pause (closing the inventory resumes the trees). Everything is restored on resume and when a save is loaded.
    /// </summary>
    internal static class NpcFreeze
    {
        private const int RecheckPerFrame = 8;

        private sealed class Entry
        {
            public GameObject Go;
            public EngineBehavior Mover;
            public BehaviorTree[] Trees;
            public bool[] TreePausedByUs;
            public Animator[] Animators;
            public float[] Speeds;
            public NavMeshAgent Agent;
            public bool AgentWasStopped;
            public bool AgentTouched;
        }

        private static bool active;
        private static GameObject player;
        private static float nextPlayerCheck;
        private static int cursor;

        private static Harmony harmony;
        private static readonly Dictionary<GameObject, Entry> frozen = new Dictionary<GameObject, Entry>();
        private static readonly List<Entry> entries = new List<Entry>();

        private static readonly AccessTools.FieldRef<EngineBehavior, MovementControllerEnum> movementKind =
            AccessTools.FieldRefAccess<EngineBehavior, MovementControllerEnum>("movementControllerKind");
        private static readonly AccessTools.FieldRef<BehaviorTree, bool> treePaused =
            AccessTools.FieldRefAccess<BehaviorTree, bool>("isPaused");

        internal static bool Active => active;
        internal static int Count => entries.Count;

        internal static void Set(bool on)
        {
            if (on == active)
            {
                return;
            }
            if (on)
            {
                try
                {
                    harmony = harmony ?? new Harmony(Plugin.Guid + ".npcfreeze");
                    harmony.PatchAll(typeof(NpcFreezePatches));
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError("NPC freeze: failed to patch: " + e);
                    harmony?.UnpatchSelf();
                    return;
                }
                active = true;
                RefreshPlayer();
                foreach (EngineBehavior b in Object.FindObjectsOfType<EngineBehavior>())
                {
                    if (movementKind(b) != MovementControllerEnum.Player)
                    {
                        Freeze(b.gameObject);
                    }
                }
                foreach (NpcState st in Object.FindObjectsOfType<NpcState>())
                {
                    Freeze(st.gameObject);
                }
                Plugin.ShowMessage($"NPCs frozen ({entries.Count})");
            }
            else
            {
                active = false;
                Restore();
                Plugin.ShowMessage("NPCs resumed");
            }
        }

        /// <summary>Every frame while frozen (called only then): re-check a few frozen NPCs, no allocations.</summary>
        internal static void Tick()
        {
            if (!active)
            {
                return;
            }
            int n = Mathf.Min(RecheckPerFrame, entries.Count);
            for (int i = 0; i < n && entries.Count > 0; i++)
            {
                if (cursor >= entries.Count)
                {
                    cursor = 0;
                }
                Entry e = entries[cursor];
                if (e.Go == null)
                {
                    entries.RemoveAt(cursor);
                    frozen.Remove(e.Go);
                    continue;
                }
                Hold(e);
                cursor++;
            }
        }

        /// <summary>A save is being loaded: let the NPCs go before the world is rebuilt.</summary>
        internal static void OnLoad()
        {
            if (active)
            {
                active = false;
                Restore();
            }
        }

        private static void RefreshPlayer()
        {
            nextPlayerCheck = Time.unscaledTime + 1f;
            try
            {
                IEntity p = ServiceLocator.GetService<ISimulation>()?.Player;
                player = p != null ? ((IEntityView)p).GameObject : null;
            }
            catch
            {
                player = null;
            }
        }

        private static bool IsPlayer(GameObject go)
        {
            return player != null && (go == player || go.transform.IsChildOf(player.transform));
        }

        /// <summary>For the prefixes (per NPC per frame, only while patched): this NPC was frozen when the freeze was switched on.</summary>
        internal static bool IsFrozen(Component c)
        {
            if (!active)
            {
                return false;
            }
            Transform t = c.transform;
            for (int i = 0; i < 4 && t != null; i++, t = t.parent) // IK/animator may sit below the NPC root
            {
                if (frozen.ContainsKey(t.gameObject))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Freeze(GameObject go)
        {
            if (frozen.ContainsKey(go) || IsPlayer(go))
            {
                return;
            }
            EngineBehavior mover = go.GetComponent<EngineBehavior>();
            if (mover != null && movementKind(mover) == MovementControllerEnum.Player)
            {
                return;
            }
            var e = new Entry
            {
                Go = go,
                Mover = mover,
                Trees = go.GetComponents<BehaviorTree>(),
                Animators = go.GetComponentsInChildren<Animator>(true),
                Agent = go.GetComponent<NavMeshAgent>()
            };
            e.TreePausedByUs = new bool[e.Trees.Length];
            e.Speeds = new float[e.Animators.Length];
            for (int i = 0; i < e.Animators.Length; i++)
            {
                e.Speeds[i] = e.Animators[i].speed;
            }
            if (e.Agent != null && e.Agent.isActiveAndEnabled && e.Agent.isOnNavMesh)
            {
                e.AgentWasStopped = e.Agent.isStopped;
                e.AgentTouched = true;
            }
            frozen[go] = e;
            entries.Add(e);
            Hold(e);
        }

        /// <summary>Apply (again) the frozen state; no allocations.</summary>
        private static void Hold(Entry e)
        {
            if (e.Mover != null && !e.Mover.IsPaused)
            {
                e.Mover.IsPaused = true;
            }
            for (int i = 0; i < e.Trees.Length; i++)
            {
                BehaviorTree tree = e.Trees[i];
                if (tree != null && tree.ExecutionStatus == TaskStatus.Running && !treePaused(tree))
                {
                    tree.DisableBehavior(pause: true);
                    e.TreePausedByUs[i] = true;
                }
            }
            for (int i = 0; i < e.Animators.Length; i++)
            {
                Animator a = e.Animators[i];
                if (a != null && a.speed != 0f)
                {
                    a.speed = 0f;
                }
            }
            if (e.AgentTouched && e.Agent != null && e.Agent.isActiveAndEnabled && e.Agent.isOnNavMesh)
            {
                if (!e.Agent.isStopped)
                {
                    e.Agent.isStopped = true;
                }
                e.Agent.velocity = Vector3.zero;
            }
        }

        private static void Restore()
        {
            bool gamePaused = InstanceByRequest<EngineApplication>.Instance.IsPaused;
            foreach (Entry e in entries)
            {
                if (e.Go == null)
                {
                    continue;
                }
                for (int i = 0; i < e.Trees.Length; i++)
                {
                    // Resume only what is still paused by us: the game may have resumed or replaced the tree meanwhile.
                    BehaviorTree tree = e.Trees[i];
                    if (e.TreePausedByUs[i] && tree != null && treePaused(tree) && !gamePaused)
                    {
                        tree.EnableBehavior();
                    }
                }
                if (e.Mover != null)
                {
                    e.Mover.IsPaused = gamePaused;
                }
                for (int i = 0; i < e.Animators.Length; i++)
                {
                    if (e.Animators[i] != null)
                    {
                        e.Animators[i].speed = e.Speeds[i];
                    }
                }
                if (e.AgentTouched && e.Agent != null && e.Agent.isActiveAndEnabled && e.Agent.isOnNavMesh)
                {
                    e.Agent.isStopped = e.AgentWasStopped;
                }
            }
            entries.Clear();
            frozen.Clear();
            cursor = 0;
            harmony?.UnpatchSelf(); // back to zero overhead
        }
    }

    /// <summary>Installed only while NPCs are frozen (NpcFreeze.Set).</summary>
    [HarmonyPatch]
    internal static class NpcFreezePatches
    {
        // The NPC state machine (walking, idling, fighting, dialogs): nothing advances while frozen.
        [HarmonyPatch(typeof(NpcState), "Update")]
        [HarmonyPrefix]
        private static bool NpcStateUpdatePrefix(NpcState __instance)
        {
            return !NpcFreeze.IsFrozen(__instance);
        }

        // Head tracking (FinalIK LookAtIK) and weapon aiming (AimIK). IKController.LateUpdate still runs the solvers every frame,
        // which is needed: the animator at speed 0 rewrites the bones each frame. Only these pre-update callbacks, which move
        // the look/aim target towards the player and change the weights, are skipped, so the head stays where it was.
        [HarmonyPatch(typeof(IKController), "OnPreLookAtIKUpdate")]
        [HarmonyPrefix]
        private static bool LookAtPrefix(IKController __instance)
        {
            return !NpcFreeze.IsFrozen(__instance);
        }

        [HarmonyPatch(typeof(IKController), nameof(IKController.PreAimUpdate))]
        [HarmonyPrefix]
        private static bool AimPrefix(IKController __instance)
        {
            return !NpcFreeze.IsFrozen(__instance);
        }
    }
}
