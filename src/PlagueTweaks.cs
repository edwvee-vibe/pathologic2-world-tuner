using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// Infected districts (experiments). The game has no fog of its own for them [code]: the weather is the same as elsewhere.
    /// What marks them is PlagueZone: baked meshes of thin vertical "shafts" (a veil dense at the ground, thinning upwards),
    /// shown with the material property _Level and switched off at level 0, and PlagueZoneParticles, whose emission is
    /// maxRate x the infection level at their spot. PlagueZone.GetLevel(x, z) gives the infection level at a point (one ray
    /// down through all colliders); the mod asks it twice a second at the camera while an option below needs it.
    /// - Game fog in infected districts: inside one (any level above 0) the fog profile fades back to the game's.
    /// - Ground fog in infected districts: inside one the camera's height fog density fades up to x4.
    /// - Plague particles: maxRate of every particle source scaled (new ones in an Awake postfix while not x1).
    /// - Plague veil: the _Level given to the veil's material scaled (postfix of PlagueZone.ApplyLevel while not x1); 0 hides it.
    /// </summary>
    internal static class PlagueTweaks
    {
        internal static ConfigEntry<bool> GameFogInInfected, GroundFogInInfected;
        internal static ConfigEntry<float> ParticlesScale, VeilScale, FadeSeconds, FadeOutSeconds;

        internal static float Level = -1f;   // raw infection level at the camera (-1 = not measured)
        internal static float Weight;        // smoothed level used by the fog options
        private static float nextMeasure;
        private static float appliedFogWeight = -1f;

        private static Harmony harmony;
        private static bool particlesPatched, veilPatched;
        private static bool particlesDirty, veilDirty;

        private static readonly AccessTools.FieldRef<PlagueZoneParticles, float> maxRate = AccessTools.FieldRefAccess<PlagueZoneParticles, float>("maxRate");
        private static readonly AccessTools.FieldRef<PlagueZoneParticles, float> prevLevel = AccessTools.FieldRefAccess<PlagueZoneParticles, float>("prevLevel");
        private static readonly AccessTools.FieldRef<PlagueZone, float> zoneLevel = AccessTools.FieldRefAccess<PlagueZone, float>("_level");
        private static readonly Dictionary<int, float> rateOriginals = new Dictionary<int, float>();
        internal static int ParticleSources, VeilZones;

        internal static void BindConfig(ConfigFile cfg)
        {
            GameFogInInfected = cfg.Bind("Plague", "GameFogInInfected", false,
                "Experiment. In infected districts the fog goes back to the game's (fades in and out over ~2.5 s).");
            GroundFogInInfected = cfg.Bind("Plague", "GroundFogInInfected", false,
                "Experiment. In infected districts the height (ground) fog of the camera gets 4 times denser (fades in and out over ~2.5 s).");
            ParticlesScale = cfg.Bind("Plague", "ParticlesScale", 1f, new ConfigDescription(
                "Experiment. Multiplier of the falling plague particles in infected districts. 1 = as in the game.", new AcceptableValueRange<float>(0f, 10f)));
            VeilScale = cfg.Bind("Plague", "VeilScale", 1f, new ConfigDescription(
                "Experiment. Multiplier of the plague veil (the shafts of haze over infected districts). 1 = as in the game, 0 = hidden.",
                new AcceptableValueRange<float>(0f, 10f)));
            FadeSeconds = cfg.Bind("Plague", "FadeSeconds", 2.5f, new ConfigDescription(
                "How long the infected-district fog options take to fade in when entering a district, seconds. 0 = at once.",
                new AcceptableValueRange<float>(0f, 20f)));
            FadeOutSeconds = cfg.Bind("Plague", "FadeOutSeconds", 1f, new ConfigDescription(
                "How long they take to fade out when leaving a district, seconds. 0 = at once.", new AcceptableValueRange<float>(0f, 20f)));
            GameFogInInfected.SettingChanged += (s, e) =>
            {
                Measure(true);
                SetFogWeight(GameFogInInfected.Value ? Weight : 0f);
            };
            GroundFogInInfected.SettingChanged += (s, e) =>
            {
                Measure(true);
                FogTweaks.GroundWeight = GroundFogInInfected.Value ? Weight : 0f;
                FogTweaks.ApplyCameraFogNow();
            };
            ParticlesScale.SettingChanged += (s, e) => particlesDirty = true;
            VeilScale.SettingChanged += (s, e) => veilDirty = true;
            particlesDirty = Math.Abs(ParticlesScale.Value - 1f) > 0.0001f;
            veilDirty = Math.Abs(VeilScale.Value - 1f) > 0.0001f;
        }

        private static bool Tracking => GameFogInInfected.Value || GroundFogInInfected.Value;

        /// <summary>Infection level at the camera, measured at most twice a second (also used by the window).</summary>
        internal static void Measure(bool force)
        {
            float now = Time.unscaledTime;
            if (!force && now < nextMeasure)
            {
                return;
            }
            nextMeasure = now + 0.5f;
            GameCamera gc = GameCamera.Instance;
            if (gc == null || gc.CameraTransform == null)
            {
                Level = -1f;
                return;
            }
            Vector3 p = gc.CameraTransform.position;
            Level = Mathf.Clamp01(PlagueZone.GetLevel(new Vector2(p.x, p.z)));
        }

        /// <summary>Twice a second.</summary>
        internal static void Tick()
        {
            if (Tracking)
            {
                Measure(false);
            }
            if (particlesDirty)
            {
                ApplyParticles();
            }
            if (veilDirty)
            {
                ApplyVeil();
            }
        }

        /// <summary>Every frame while the smoothed level moves towards the measured one (the fog fades in over ~2.5 s).</summary>
        /// <summary>
        /// Any infection counts as a full zone: the level is the same 50% all over an infected district (it may matter for the
        /// game, not for the look), so the fog options are fully on inside and off outside, with a smooth ~2.5 s fade.
        /// </summary>
        internal static float Target => Level > 0.01f ? 1f : 0f;

        internal static bool NeedsFrame => Tracking && Level >= 0f && Math.Abs(Weight - Target) > 0.0001f;

        internal static void FrameTick()
        {
            float target = Target;
            float fade = target > Weight ? FadeSeconds.Value : FadeOutSeconds.Value;
            Weight = fade <= 0f ? target : Mathf.MoveTowards(Weight, target, Time.unscaledDeltaTime / fade);
            if (GameFogInInfected.Value && (Math.Abs(Weight - appliedFogWeight) >= 0.02f || Weight == target))
            {
                SetFogWeight(Weight);
            }
            if (GroundFogInInfected.Value)
            {
                FogTweaks.GroundWeight = Weight;
                FogTweaks.ApplyCameraFogNow();
            }
        }

        private static void SetFogWeight(float w)
        {
            if (Math.Abs(w - appliedFogWeight) < 0.0001f)
            {
                return;
            }
            appliedFogWeight = w;
            FogTweaks.InfectedWeight = w;
            FogTweaks.ApplyNow();
        }

        // ------------------------------------------------------------ particles

        private static void ApplyParticles()
        {
            particlesDirty = false;
            bool need = Math.Abs(ParticlesScale.Value - 1f) > 0.0001f;
            SetHook(ref particlesPatched, need, typeof(PlagueParticlesPatches));
            PlagueZoneParticles[] all = UnityEngine.Object.FindObjectsOfType<PlagueZoneParticles>();
            foreach (PlagueZoneParticles p in all)
            {
                ApplyParticles(p);
            }
            ParticleSources = all.Length;
            Plugin.Log.LogInfo($"Plague: particles x{ParticlesScale.Value:0.##} on {all.Length} source(s)");
        }

        internal static void ApplyParticles(PlagueZoneParticles p)
        {
            if (p == null)
            {
                return;
            }
            int id = p.GetInstanceID();
            if (!rateOriginals.TryGetValue(id, out float original))
            {
                if (Math.Abs(ParticlesScale.Value - 1f) < 0.0001f)
                {
                    return;
                }
                original = maxRate(p);
                rateOriginals[id] = original;
            }
            maxRate(p) = original * ParticlesScale.Value;
            prevLevel(p) = -1f; // the emission rate is set again on its next update
        }

        // ------------------------------------------------------------ veil

        private static void ApplyVeil()
        {
            veilDirty = false;
            bool need = Math.Abs(VeilScale.Value - 1f) > 0.0001f;
            SetHook(ref veilPatched, need, typeof(PlagueVeilPatches)); // removed first when going back to x1
            PlagueZone[] all = UnityEngine.Object.FindObjectsOfType<PlagueZone>();
            foreach (PlagueZone z in all)
            {
                try
                {
                    z.ApplyLevel(); // with the patch: scaled; without it: the game's
                }
                catch (Exception e)
                {
                    Plugin.LogOnce("plagueveil", "Plague veil: " + e.Message);
                }
            }
            VeilZones = all.Length;
            Plugin.Log.LogInfo($"Plague: veil x{VeilScale.Value:0.##} on {all.Length} zone(s)");
        }

        /// <summary>Postfix of PlagueZone.ApplyLevel: the game has set _Level (or hidden the veil at level 0).</summary>
        internal static void AfterApplyLevel(PlagueZone z)
        {
            float level = zoneLevel(z);
            if (level == 0f)
            {
                return;
            }
            LOD[] lods = z.GetComponent<LODGroup>().GetLODs();
            float k = VeilScale.Value;
            MaterialPropertyBlock block = null;
            if (k > 0f)
            {
                block = new MaterialPropertyBlock();
                block.SetFloat("_Level", level * k);
            }
            foreach (LOD lod in lods)
            {
                foreach (Renderer r in lod.renderers)
                {
                    if (r == null)
                    {
                        continue;
                    }
                    if (block == null)
                    {
                        r.enabled = false;
                    }
                    else
                    {
                        r.SetPropertyBlock(block);
                    }
                }
            }
        }

        private static void SetHook(ref bool patched, bool need, Type patches)
        {
            try
            {
                harmony = harmony ?? new Harmony(Plugin.Guid + ".plague");
                if (need && !patched)
                {
                    harmony.PatchAll(patches);
                    patched = true;
                }
                else if (!need && patched)
                {
                    System.Reflection.MethodBase m = patches == typeof(PlagueVeilPatches)
                        ? AccessTools.Method(typeof(PlagueZone), nameof(PlagueZone.ApplyLevel))
                        : AccessTools.Method(typeof(PlagueZoneParticles), "Awake");
                    harmony.Unpatch(m, HarmonyPatchType.All, harmony.Id);
                    patched = false;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("plaguehook" + patches.Name, $"{patches.Name}: {e}");
            }
        }
    }

    /// <summary>Installed while the particle multiplier is not 1: sources created later get it too.</summary>
    [HarmonyPatch]
    internal static class PlagueParticlesPatches
    {
        [HarmonyPatch(typeof(PlagueZoneParticles), "Awake")]
        [HarmonyPostfix]
        private static void AwakePostfix(PlagueZoneParticles __instance)
        {
            try
            {
                PlagueTweaks.ApplyParticles(__instance);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("plagueparticles", "Plague particles: " + e.Message);
            }
        }
    }

    /// <summary>Installed while the veil multiplier is not 1.</summary>
    [HarmonyPatch]
    internal static class PlagueVeilPatches
    {
        [HarmonyPatch(typeof(PlagueZone), nameof(PlagueZone.ApplyLevel))]
        [HarmonyPostfix]
        private static void ApplyLevelPostfix(PlagueZone __instance)
        {
            try
            {
                PlagueTweaks.AfterApplyLevel(__instance);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("plagueveil-apply", "Plague veil: " + e.Message);
            }
        }
    }
}
