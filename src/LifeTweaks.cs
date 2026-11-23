using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Engine.Behaviours.Components;
using Engine.Common;
using Engine.Common.Services;
using Engine.Source.Commons;
using Engine.Source.Settings.External;
using HarmonyLib;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    internal enum LanternShadows
    {
        Game,
        Off,
        Hard,
        Soft
    }

    /// <summary>
    /// The world in the distance: street crowds, lamps, the player's lantern, birds. These pair with a longer draw distance.
    /// - Crowds: ExternalOptimizationSettings.CrowdSpawnMin/MaxDistance (50/75), CrowdDestroyDistance (100) and
    ///   MaxOutdoorCrowdEntityCount (500) are read by OutdoorCrowdComponent / OutdoorCrowdService on every use [code].
    /// - Lamps: LightCameraShadowDistance switches a lamp's shadows off beyond shadowDistance (15 m); LightCameraDistance scales
    ///   the light's range by rangeCurve(distance to the camera) and drops its shadows below 0.3 [code]. The mod scales the
    ///   distances (the curve along its time axis). Lamps enabled later are handled by an OnEnable prefix.
    /// - Lantern: the Light components under PivotPlayer.FlashlightLight.
    /// - Birds: FlockController._childAmount; the flock adds or removes one bird per update until the count matches [code].
    /// Everything is applied when a value changes; hooks are installed only while the related value is not at "Game".
    /// </summary>
    internal static class LifeTweaks
    {
        internal static ConfigEntry<float> CrowdSpawnMin, CrowdSpawnMax, CrowdDestroy, CrowdLimit;
        internal static ConfigEntry<float> LampShadows, LampRange;
        internal static ConfigEntry<float> LanternRange, LanternIntensity, LanternWarmth;
        internal static ConfigEntry<LanternShadows> LanternShadowMode;
        internal static ConfigEntry<float> Birds;

        // ---- game values
        private static bool crowdOriginalKnown;
        internal static float GameSpawnMin = 50f, GameSpawnMax = 75f, GameDestroy = 100f;
        internal static int GameLimit = 500;
        private static bool crowdDirty, lampsDirty, lanternDirty, birdsDirty;

        private static Harmony harmony;
        private static bool lampsPatched, birdsPatched;

        internal static void BindConfig(ConfigFile cfg)
        {
            CrowdSpawnMin = cfg.Bind("Life", "CrowdSpawnMinDistance", -1f, new ConfigDescription(
                "Passers-by appear between this and the max distance from the player, m. -1 = the game's (50).",
                new AcceptableValueRange<float>(-1f, 1000f)));
            CrowdSpawnMax = cfg.Bind("Life", "CrowdSpawnMaxDistance", -1f, new ConfigDescription(
                "Max distance at which passers-by appear, m. -1 = the game's (75).", new AcceptableValueRange<float>(-1f, 1000f)));
            CrowdDestroy = cfg.Bind("Life", "CrowdDestroyDistance", -1f, new ConfigDescription(
                "Passers-by farther than this are removed, m. -1 = the game's (100). Kept above the max spawn distance.",
                new AcceptableValueRange<float>(-1f, 1200f)));
            CrowdLimit = cfg.Bind("Life", "CrowdLimit", -1f, new ConfigDescription(
                "Max passers-by on roads and footpaths at once. -1 = the game's (500).", new AcceptableValueRange<float>(-1f, 3000f)));
            LampShadows = cfg.Bind("Life", "LampShadowDistance", 1f, new ConfigDescription(
                "Multiplier of the distance up to which street lamps and lights cast shadows (the game: 15 m). Shadows of lamps are expensive.",
                new AcceptableValueRange<float>(0.25f, 10f)));
            LampRange = cfg.Bind("Life", "LampFadeDistance", 1f, new ConfigDescription(
                "Multiplier of the distances at which lamps dim their light with the distance to the camera.",
                new AcceptableValueRange<float>(0.5f, 8f)));
            LanternRange = cfg.Bind("Lantern", "Range", 1f, new ConfigDescription("Lantern light range multiplier.",
                new AcceptableValueRange<float>(0.25f, 4f)));
            LanternIntensity = cfg.Bind("Lantern", "Brightness", 1f, new ConfigDescription("Lantern brightness multiplier.",
                new AcceptableValueRange<float>(0f, 4f)));
            LanternWarmth = cfg.Bind("Lantern", "Warmth", 0f, new ConfigDescription("Lantern colour: negative = colder, positive = warmer.",
                new AcceptableValueRange<float>(-1f, 1f)));
            LanternShadowMode = cfg.Bind("Lantern", "Shadows", LanternShadows.Game, "Shadows of the lantern light: Game, Off, Hard, Soft.");
            Birds = cfg.Bind("Life", "Birds", 1f, new ConfigDescription("Multiplier of the number of birds in each flock (the game: up to 250).",
                new AcceptableValueRange<float>(0f, 4f)));

            foreach (ConfigEntry<float> e in new[] { CrowdSpawnMin, CrowdSpawnMax, CrowdDestroy, CrowdLimit })
            {
                e.SettingChanged += (s, a) => crowdDirty = true;
            }
            LampShadows.SettingChanged += (s, a) => lampsDirty = true;
            LampRange.SettingChanged += (s, a) => lampsDirty = true;
            foreach (ConfigEntry<float> e in new[] { LanternRange, LanternIntensity, LanternWarmth })
            {
                e.SettingChanged += (s, a) => lanternDirty = true;
            }
            LanternShadowMode.SettingChanged += (s, a) => lanternDirty = true;
            Birds.SettingChanged += (s, a) => birdsDirty = true;

            crowdDirty = !CrowdDefault;
            lampsDirty = !LampsDefault;
            lanternDirty = !LanternDefault;
            birdsDirty = !BirdsDefault;
        }

        internal static bool CrowdDefault => CrowdSpawnMin.Value < 0f && CrowdSpawnMax.Value < 0f && CrowdDestroy.Value < 0f && CrowdLimit.Value < 0f;
        internal static bool LampsDefault => Math.Abs(LampShadows.Value - 1f) < 0.0001f && Math.Abs(LampRange.Value - 1f) < 0.0001f;
        internal static bool LanternDefault => Math.Abs(LanternRange.Value - 1f) < 0.0001f && Math.Abs(LanternIntensity.Value - 1f) < 0.0001f &&
                                               Math.Abs(LanternWarmth.Value) < 0.0001f &&
                                               LanternShadowMode.Value == LanternShadows.Game;
        internal static bool BirdsDefault => Math.Abs(Birds.Value - 1f) < 0.0001f;

        /// <summary>Twice a second; everything is applied only after a change (window, config) or when the game state changed.</summary>
        internal static void Tick()
        {
            if (crowdDirty)
            {
                ApplyCrowd();
            }
            if (lampsDirty)
            {
                ApplyLamps();
            }
            if (lanternDirty || (!LanternDefault && LanternChanged()))
            {
                ApplyLantern();
            }
            if (birdsDirty)
            {
                ApplyBirds();
            }
        }

        // ------------------------------------------------------------ crowds

        internal static bool CrowdAvailable => MemoryTweaks.Service != null;

        private static void ApplyCrowd()
        {
            // The first access to ExternalSettingsInstance loads the XML and initialises meta data: only in a running game.
            if (!CrowdAvailable)
            {
                return;
            }
            crowdDirty = false;
            ExternalOptimizationSettings s = ExternalSettingsInstance<ExternalOptimizationSettings>.Instance;
            if (!crowdOriginalKnown)
            {
                crowdOriginalKnown = true;
                GameSpawnMin = s.CrowdSpawnMinDistance;
                GameSpawnMax = s.CrowdSpawnMaxDistance;
                GameDestroy = s.CrowdDestroyDistance;
                GameLimit = s.MaxOutdoorCrowdEntityCount;
                Plugin.Log.LogInfo($"Crowds: the game spawns at {GameSpawnMin}-{GameSpawnMax} m, removes beyond {GameDestroy} m, limit {GameLimit}");
            }
            float min = CrowdSpawnMin.Value >= 0f ? CrowdSpawnMin.Value : GameSpawnMin;
            float max = CrowdSpawnMax.Value >= 0f ? CrowdSpawnMax.Value : GameSpawnMax;
            float destroy = CrowdDestroy.Value >= 0f ? CrowdDestroy.Value : GameDestroy;
            max = Mathf.Max(max, min + 5f);       // a ring: nobody appears if max <= min
            destroy = Mathf.Max(destroy, max + 10f); // otherwise new passers-by would be removed at once
            s.CrowdSpawnMinDistance = min;
            s.CrowdSpawnMaxDistance = max;
            s.CrowdDestroyDistance = destroy;
            s.MaxOutdoorCrowdEntityCount = CrowdLimit.Value >= 0f ? Mathf.RoundToInt(CrowdLimit.Value) : GameLimit;
        }

        internal static void EffectiveCrowd(out float min, out float max, out float destroy)
        {
            min = CrowdSpawnMin.Value >= 0f ? CrowdSpawnMin.Value : GameSpawnMin;
            max = Mathf.Max(CrowdSpawnMax.Value >= 0f ? CrowdSpawnMax.Value : GameSpawnMax, min + 5f);
            destroy = Mathf.Max(CrowdDestroy.Value >= 0f ? CrowdDestroy.Value : GameDestroy, max + 10f);
        }

        // ------------------------------------------------------------ lamps

        private static readonly AccessTools.FieldRef<LightCameraShadowDistance, float> shadowDistance =
            AccessTools.FieldRefAccess<LightCameraShadowDistance, float>("shadowDistance");
        private static readonly AccessTools.FieldRef<LightCameraDistance, AnimationCurve> rangeCurve =
            AccessTools.FieldRefAccess<LightCameraDistance, AnimationCurve>("rangeCurve");
        private static readonly Dictionary<int, float> shadowOriginals = new Dictionary<int, float>();
        private static readonly Dictionary<int, Keyframe[]> curveOriginals = new Dictionary<int, Keyframe[]>();
        internal static int ShadowLamps, FadeLamps;
        internal static float TypicalShadowDistance = 15f;
        private static bool curveLogged;

        private static void ApplyLamps()
        {
            lampsDirty = false;
            SetHook(ref lampsPatched, !LampsDefault, typeof(LampPatches));
            LightCameraShadowDistance[] shadows = UnityEngine.Object.FindObjectsOfType<LightCameraShadowDistance>();
            foreach (LightCameraShadowDistance c in shadows)
            {
                ApplyLamp(c);
            }
            LightCameraDistance[] fades = UnityEngine.Object.FindObjectsOfType<LightCameraDistance>();
            foreach (LightCameraDistance c in fades)
            {
                ApplyLamp(c);
            }
            ShadowLamps = shadows.Length;
            FadeLamps = fades.Length;
            Plugin.Log.LogInfo($"Lamps: shadow distance x{LampShadows.Value:0.##} on {shadows.Length} lights, fade distance x{LampRange.Value:0.##} on {fades.Length} lights");
        }

        internal static void ApplyLamp(LightCameraShadowDistance c)
        {
            if (c == null)
            {
                return;
            }
            int id = c.GetInstanceID();
            if (!shadowOriginals.TryGetValue(id, out float original))
            {
                if (Math.Abs(LampShadows.Value - 1f) < 0.0001f)
                {
                    return; // never changed
                }
                original = shadowDistance(c);
                shadowOriginals[id] = original;
                TypicalShadowDistance = original;
            }
            shadowDistance(c) = original * LampShadows.Value;
        }

        internal static void ApplyLamp(LightCameraDistance c)
        {
            if (c == null)
            {
                return;
            }
            AnimationCurve curve = rangeCurve(c);
            if (curve == null)
            {
                return;
            }
            int id = c.GetInstanceID();
            if (!curveOriginals.TryGetValue(id, out Keyframe[] original))
            {
                if (Math.Abs(LampRange.Value - 1f) < 0.0001f)
                {
                    return;
                }
                original = curve.keys;
                curveOriginals[id] = original;
                if (!curveLogged)
                {
                    curveLogged = true;
                    var text = new System.Text.StringBuilder();
                    foreach (Keyframe k in original)
                    {
                        text.Append($" ({k.time:0.#} m: x{k.value:0.##})");
                    }
                    Plugin.Log.LogInfo($"Lamps: fade curve of '{c.name}':{text}");
                }
            }
            float scale = LampRange.Value;
            var keys = new Keyframe[original.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                Keyframe k = original[i];
                k.time *= scale;
                k.inTangent /= scale;
                k.outTangent /= scale;
                keys[i] = k;
            }
            curve.keys = keys;
        }

        // ------------------------------------------------------------ lantern

        private sealed class LanternLight
        {
            public Light Light;
            public float Range, Intensity, Angle;
            public Color Color;
            public LightShadows Shadows;
            public LightFlicker Flicker;
            public float FlickerBase;
        }

        private static readonly AccessTools.FieldRef<LightFlicker, float> flickerBase =
            AccessTools.FieldRefAccess<LightFlicker, float>("baseIntensity");
        private static readonly List<LanternLight> lantern = new List<LanternLight>();
        private static GameObject lanternObject;
        internal static int LanternLights => lantern.Count;
        internal static string LanternInfo = "";

        private static GameObject FindLanternObject()
        {
            try
            {
                IEntity p = ServiceLocator.GetService<ISimulation>()?.Player;
                GameObject go = p != null ? ((IEntityView)p).GameObject : null;
                PivotPlayer pivot = go != null ? go.GetComponent<PivotPlayer>() : null;
                return pivot != null ? pivot.FlashlightLight : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The player object is replaced when a save is loaded: then the values are applied to the new lantern.</summary>
        private static bool LanternChanged()
        {
            GameObject go = FindLanternObject();
            return go != null && go != lanternObject;
        }

        private static void ApplyLantern()
        {
            GameObject go = FindLanternObject();
            if (go == null)
            {
                return; // main menu, loading; retried while dirty
            }
            lanternDirty = false;
            if (go != lanternObject)
            {
                lanternObject = go;
                lantern.Clear();
                var info = new System.Text.StringBuilder();
                foreach (Light l in go.GetComponentsInChildren<Light>(true))
                {
                    var ll = new LanternLight
                    {
                        Light = l, Range = l.range, Intensity = l.intensity, Angle = l.spotAngle, Color = l.color, Shadows = l.shadows,
                        Flicker = l.GetComponent<LightFlicker>()
                    };
                    if (ll.Flicker != null)
                    {
                        ll.FlickerBase = flickerBase(ll.Flicker);
                    }
                    lantern.Add(ll);
                    info.Append($"{l.name}: {l.type}, range {l.range:0.#}, intensity {l.intensity:0.##}, angle {l.spotAngle:0}, shadows {l.shadows}" +
                                (ll.Flicker != null ? ", flicker" : "") + "; ");
                }
                LanternInfo = info.ToString();
                Plugin.Log.LogInfo($"Lantern: {lantern.Count} light(s): {LanternInfo}");
            }
            float w = LanternWarmth.Value;
            foreach (LanternLight ll in lantern)
            {
                if (ll.Light == null)
                {
                    continue;
                }
                ll.Light.range = ll.Range * LanternRange.Value;
                ll.Light.intensity = ll.Intensity * LanternIntensity.Value;
                if (ll.Flicker != null)
                {
                    flickerBase(ll.Flicker) = ll.FlickerBase * LanternIntensity.Value;
                }
                Color c = ll.Color;
                ll.Light.color = new Color(c.r * (1f + 0.3f * w), c.g * (1f + 0.05f * w), Mathf.Max(0f, c.b * (1f - 0.3f * w)), c.a);
                switch (LanternShadowMode.Value)
                {
                    case LanternShadows.Off: ll.Light.shadows = LightShadows.None; break;
                    case LanternShadows.Hard: ll.Light.shadows = LightShadows.Hard; break;
                    case LanternShadows.Soft: ll.Light.shadows = LightShadows.Soft; break;
                    default: ll.Light.shadows = ll.Shadows; break;
                }
            }
        }

        // ------------------------------------------------------------ birds

        private static readonly Dictionary<int, int> birdOriginals = new Dictionary<int, int>();
        internal static int Flocks, BirdsNow;

        private static void ApplyBirds()
        {
            birdsDirty = false;
            SetHook(ref birdsPatched, !BirdsDefault, typeof(BirdPatches));
            FlockController[] flocks = UnityEngine.Object.FindObjectsOfType<FlockController>();
            BirdsNow = 0;
            foreach (FlockController f in flocks)
            {
                ApplyFlock(f);
                BirdsNow += f._childAmount;
            }
            Flocks = flocks.Length;
            Plugin.Log.LogInfo($"Birds: x{Birds.Value:0.##} on {flocks.Length} flock(s), {BirdsNow} birds wanted");
        }

        internal static void ApplyFlock(FlockController f)
        {
            if (f == null)
            {
                return;
            }
            int id = f.GetInstanceID();
            if (!birdOriginals.TryGetValue(id, out int original))
            {
                if (BirdsDefault)
                {
                    return;
                }
                original = f._childAmount;
                birdOriginals[id] = original;
            }
            if (original > 0)
            {
                f._childAmount = Mathf.RoundToInt(original * Birds.Value);
            }
        }

        // ------------------------------------------------------------ common

        private static void SetHook(ref bool patched, bool need, Type patches)
        {
            try
            {
                harmony = harmony ?? new Harmony(Plugin.Guid + ".life");
                if (need && !patched)
                {
                    harmony.PatchAll(patches);
                    patched = true;
                }
                else if (!need && patched)
                {
                    foreach (System.Reflection.MethodBase m in HookTargets(patches))
                    {
                        harmony.Unpatch(m, HarmonyPatchType.All, harmony.Id);
                    }
                    patched = false;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("lifehook" + patches.Name, $"{patches.Name}: {e}");
            }
        }

        private static IEnumerable<System.Reflection.MethodBase> HookTargets(Type patches)
        {
            if (patches == typeof(LampPatches))
            {
                yield return AccessTools.Method(typeof(LightCameraShadowDistance), "OnEnable");
                yield return AccessTools.Method(typeof(LightCameraDistance), "OnEnable");
            }
            else
            {
                yield return AccessTools.Method(typeof(FlockController), "Start");
            }
        }

        internal static void ResetCrowd()
        {
            CrowdSpawnMin.Value = CrowdSpawnMax.Value = CrowdDestroy.Value = CrowdLimit.Value = -1f;
        }
    }

    /// <summary>Installed while the lamp distances are not at "Game": lamps of houses and districts loaded later.</summary>
    [HarmonyPatch]
    internal static class LampPatches
    {
        [HarmonyPatch(typeof(LightCameraShadowDistance), "OnEnable")]
        [HarmonyPrefix]
        private static void ShadowEnable(LightCameraShadowDistance __instance)
        {
            try
            {
                LifeTweaks.ApplyLamp(__instance);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("lampshadow", "Lamp shadows: " + e);
            }
        }

        [HarmonyPatch(typeof(LightCameraDistance), "OnEnable")]
        [HarmonyPrefix]
        private static void FadeEnable(LightCameraDistance __instance)
        {
            try
            {
                LifeTweaks.ApplyLamp(__instance);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("lampfade", "Lamp fade: " + e);
            }
        }
    }

    /// <summary>Installed while the bird multiplier is not 1: flocks created later get their count before spawning.</summary>
    [HarmonyPatch]
    internal static class BirdPatches
    {
        [HarmonyPatch(typeof(FlockController), "Start")]
        [HarmonyPrefix]
        private static void FlockStart(FlockController __instance)
        {
            try
            {
                LifeTweaks.ApplyFlock(__instance);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("birds", "Birds: " + e);
            }
        }
    }
}
