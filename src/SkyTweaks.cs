using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Engine.Common.Services;
using Engine.Common.Weather;
using Engine.Impl.Services;
using Engine.Impl.Weather;
using Engine.Proxy.Weather;
using Engine.Services.Engine;
using Engine.Source.Connections;
using HarmonyLib;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    internal enum WetMode
    {
        Game,
        Fixed
    }

    internal enum WeatherLock
    {
        Off,
        BaseLayer,
        AllLayers
    }

    /// <summary>One multiplier (or offset) applied to the blended weather after the game has copied it into the engine.</summary>
    internal sealed class SkyParam
    {
        public readonly string Label;
        public readonly ConfigEntry<float> Entry;
        public readonly float Min, Max, Default;
        public readonly bool Offset; // Default 0, added (colour warmth); otherwise a multiplier with Default 1
        public readonly Action<float> Apply;

        public SkyParam(string label, ConfigEntry<float> entry, float min, float max, bool offset, Action<float> apply)
        {
            Label = label;
            Entry = entry;
            Min = min;
            Max = max;
            Offset = offset;
            Default = offset ? 0f : 1f;
            Apply = apply;
        }

        public bool IsDefault => Math.Abs(Entry.Value - Default) < 0.0001f;
    }

    /// <summary>
    /// Weather and sky editor. The game blends the weather layers into one snapshot and copies it into the engine
    /// (WeatherSnapshotUtility.CopyFrom: TOD_Sky, RainManager, LeafManager, wind) only when the blend changes; a postfix there
    /// scales the copied values. So the cost is a few multiplications per weather update, and none per frame; the patch is
    /// installed only while some value is not at "Game". Changing a value re-runs the game's copy (WeatherController.IsEnabled
    /// setter sets its invalidate flag), which restores the game's values before the postfix scales them again.
    /// The sky date is GameSettingsData.OffsetTime (15.08.1915, + solar days) and is used only by the sky [code]; the place is
    /// the weather's Location (52 N, 85 E, UTC+8 in all seven weathers). Nothing here is written to saves.
    /// </summary>
    internal static class SkyTweaks
    {
        internal static readonly List<SkyParam> Clouds = new List<SkyParam>();
        internal static readonly List<SkyParam> Day = new List<SkyParam>();
        internal static readonly List<SkyParam> Night = new List<SkyParam>();
        internal static readonly List<SkyParam> Bodies = new List<SkyParam>();
        internal static readonly List<SkyParam> Colors = new List<SkyParam>();
        internal static readonly List<SkyParam> Elements = new List<SkyParam>();
        private static readonly List<SkyParam> all = new List<SkyParam>();

        internal const float GameSentinel = -999f;
        internal static ConfigEntry<float> Latitude, Longitude, Utc, DateShift;
        internal static ConfigEntry<WetMode> Wetness;
        internal static ConfigEntry<float> GroundWetness, PuddleWetness;
        internal static WeatherLock Lock = WeatherLock.Off; // session only

        // The game's place and date, read from the weather / settings before the mod changes them.
        internal static float GameLatitude = 52f, GameLongitude = 85f, GameUtc = 8f;
        private static DateTime gameOffset = DateTime.MinValue;
        private static bool dateApplied;

        private static Harmony harmony, lockHarmony;
        private static bool patched, lockPatched;

        // Values used by the appliers while the postfix runs.
        private static TOD_Sky tod;

        internal static void BindConfig(ConfigFile cfg)
        {
            // --- clouds
            Add(Clouds, cfg, "Sky.Clouds", "Coverage", "Cloud coverage", 0f, 3f, v => tod.Clouds.Coverage = Mathf.Clamp01(tod.Clouds.Coverage * v));
            Add(Clouds, cfg, "Sky.Clouds", "Opacity", "Cloud opacity", 0f, 2f, v => tod.Clouds.Opacity = Mathf.Clamp01(tod.Clouds.Opacity * v));
            Add(Clouds, cfg, "Sky.Clouds", "Brightness", "Cloud brightness", 0f, 4f, v => tod.Clouds.Brightness *= v);
            Add(Clouds, cfg, "Sky.Clouds", "Size", "Cloud size", 0.25f, 4f, v => tod.Clouds.Size = Mathf.Max(1f, tod.Clouds.Size * v));
            Add(Clouds, cfg, "Sky.Clouds", "Sharpness", "Cloud sharpness", 0f, 3f, v => tod.Clouds.Sharpness = Mathf.Clamp01(tod.Clouds.Sharpness * v));
            Add(Clouds, cfg, "Sky.Clouds", "Attenuation", "Sunlight through clouds", 0f, 3f, v => tod.Clouds.Attenuation = Mathf.Clamp01(tod.Clouds.Attenuation * v));
            Add(Clouds, cfg, "Sky.Clouds", "Saturation", "Cloud colour", 0f, 3f, v => tod.Clouds.Saturation = Mathf.Clamp01(tod.Clouds.Saturation * v));
            Add(Clouds, cfg, "Sky.Clouds", "Scattering", "Cloud glow (scattering)", 0f, 4f, v => tod.Clouds.Scattering *= v);

            // --- day
            Add(Day, cfg, "Sky.Day", "LightIntensity", "Sunlight", 0f, 4f, v => tod.Day.LightIntensity *= v);
            Add(Day, cfg, "Sky.Day", "AmbientMultiplier", "Ambient light", 0f, 5f, v => tod.Day.AmbientMultiplier *= v);
            Add(Day, cfg, "Sky.Day", "ShadowStrength", "Shadow strength", 0f, 1f, v => tod.Day.ShadowStrength = Mathf.Clamp01(tod.Day.ShadowStrength * v));
            Add(Day, cfg, "Sky.Day", "ReflectionMultiplier", "Sky reflections", 0f, 3f, v => tod.Day.ReflectionMultiplier *= v);
            Add(Day, cfg, "Sky.Day", "Brightness", "Sky brightness", 0f, 3f, v => tod.Day.Brightness *= v);
            Add(Day, cfg, "Sky.Day", "Contrast", "Sky contrast", 0.25f, 3f, v => tod.Day.Contrast *= v);
            Add(Day, cfg, "Sky.Day", "RayleighMultiplier", "Sky blue (Rayleigh)", 0f, 3f, v => tod.Day.RayleighMultiplier *= v);
            Add(Day, cfg, "Sky.Day", "MieMultiplier", "Sun glow (Mie)", 0f, 4f, v => tod.Day.MieMultiplier *= v);
            Add(Day, cfg, "Sky.Day", "Directionality", "Glow around the sun", 0f, 1.4f, v => tod.Day.Directionality = Mathf.Clamp01(tod.Day.Directionality * v));

            // --- night
            Add(Night, cfg, "Sky.Night", "LightIntensity", "Moonlight", 0f, 15f, v => tod.Night.LightIntensity *= v);
            Add(Night, cfg, "Sky.Night", "AmbientMultiplier", "Ambient light", 0f, 10f, v => tod.Night.AmbientMultiplier *= v);
            Add(Night, cfg, "Sky.Night", "ShadowStrength", "Shadow strength", 0f, 1f, v => tod.Night.ShadowStrength = Mathf.Clamp01(tod.Night.ShadowStrength * v));
            Add(Night, cfg, "Sky.Night", "ReflectionMultiplier", "Sky reflections", 0f, 3f, v => tod.Night.ReflectionMultiplier *= v);
            Add(Night, cfg, "Sky.Night", "Brightness", "Sky brightness", 0f, 5f, v => tod.Night.Brightness *= v);
            Add(Night, cfg, "Sky.Night", "Contrast", "Sky contrast", 0.25f, 3f, v => tod.Night.Contrast *= v);

            // --- sun, moon, stars
            Add(Bodies, cfg, "Sky.Bodies", "SunSize", "Sun size", 0.25f, 6f, v => tod.Sun.MeshSize *= v);
            Add(Bodies, cfg, "Sky.Bodies", "SunBrightness", "Sun disc brightness", 0f, 3f, v => tod.Sun.MeshBrightness *= v);
            Add(Bodies, cfg, "Sky.Bodies", "MoonSize", "Moon size", 0.25f, 6f, v => tod.Moon.MeshSize *= v);
            Add(Bodies, cfg, "Sky.Bodies", "MoonBrightness", "Moon brightness", 0f, 4f, v => tod.Moon.MeshBrightness *= v);
            Add(Bodies, cfg, "Sky.Bodies", "StarsSize", "Star size", 0.25f, 4f, v => tod.Stars.Size *= v);
            Add(Bodies, cfg, "Sky.Bodies", "StarsBrightness", "Star brightness", 0f, 8f, v => tod.Stars.Brightness *= v);

            // --- colours (both day and night gradients)
            AddColor(cfg, "Light", "sunlight and moonlight", g => new[] { tod.Day.LightColor, tod.Day.RayColor, tod.Night.LightColor, tod.Night.RayColor });
            AddColor(cfg, "Sky", "sky", g => new[] { tod.Day.SkyColor, tod.Night.SkyColor });
            AddColor(cfg, "Ambient", "ambient light", g => new[] { tod.Day.AmbientColor, tod.Night.AmbientColor });
            AddColor(cfg, "Fog", "fog and haze", g => new[] { tod.Day.FogColor, tod.Night.FogColor });
            AddColor(cfg, "Clouds", "clouds", g => new[] { tod.Day.CloudColor, tod.Night.CloudColor });

            // --- rain, wind, leaves
            Add(Elements, cfg, "Sky.Elements", "Rain", "Rain", 0f, 3f, v =>
            {
                Rain.RainManager rm = Rain.RainManager.Instance;
                if (rm != null) rm.rainIntensity *= v;
            });
            Add(Elements, cfg, "Sky.Elements", "Wind", "Wind (cloud drift)", 0f, 5f, v =>
            {
                TOD_Animation a = tod.Components != null && tod.Components.Animation != null ? tod.Components.Animation.GetComponent<TOD_Animation>() : null;
                if (a != null) a.WindSpeed *= v;
            });
            Add(Elements, cfg, "Sky.Elements", "LeavesRate", "Falling leaves", 0f, 5f, v =>
            {
                LeafManager lm = Leaves;
                if (lm != null) lm.rate *= v;
            });
            Add(Elements, cfg, "Sky.Elements", "LeavesRadius", "Falling leaves radius", 0.5f, 4f, v =>
            {
                LeafManager lm = Leaves;
                if (lm != null) lm.radius *= v;
            });

            // --- place and date
            Latitude = cfg.Bind("Sky.Place", "Latitude", GameSentinel, new ConfigDescription(
                "Latitude of the town for the sun, moon and stars, degrees (north positive). -999 = the game's (52).",
                new AcceptableValueRange<float>(GameSentinel, 90f)));
            Longitude = cfg.Bind("Sky.Place", "Longitude", GameSentinel, new ConfigDescription(
                "Longitude, degrees (east positive). -999 = the game's (85).", new AcceptableValueRange<float>(GameSentinel, 180f)));
            Utc = cfg.Bind("Sky.Place", "UtcOffset", GameSentinel, new ConfigDescription(
                "Time zone of the clock, hours. -999 = the game's (+8). Longitude / 15 puts the solar noon at 12:00.",
                new AcceptableValueRange<float>(GameSentinel, 14f)));
            DateShift = cfg.Bind("Sky.Place", "DateShiftDays", 0f, new ConfigDescription(
                "Days added to the sky's date (the game starts on 15 August 1915). Changes the sun's path and the day length only; " +
                "the quests' calendar is not touched.", new AcceptableValueRange<float>(-183f, 183f)));

            // --- wetness
            Wetness = cfg.Bind("Sky.Wetness", "Mode", WetMode.Game,
                "Game: streets get wet in the rain and dry afterwards. Fixed: the ground and puddles keep the levels below.");
            GroundWetness = cfg.Bind("Sky.Wetness", "Ground", 1f, new ConfigDescription("Ground wetness in Fixed mode (0-1).",
                new AcceptableValueRange<float>(0f, 1f)));
            PuddleWetness = cfg.Bind("Sky.Wetness", "Puddles", 1f, new ConfigDescription("Puddle level in Fixed mode (0-1).",
                new AcceptableValueRange<float>(0f, 1f)));

            foreach (SkyParam p in all)
            {
                p.Entry.SettingChanged += OnChanged;
            }
            Latitude.SettingChanged += OnChanged;
            Longitude.SettingChanged += OnChanged;
            Utc.SettingChanged += OnChanged;
            DateShift.SettingChanged += (s, e) => dateApplied = false;
            dateApplied = DateShift.Value == 0f; // nothing to do until it is changed
        }

        private static void Add(List<SkyParam> group, ConfigFile cfg, string section, string key, string label, float min, float max, Action<float> apply)
        {
            ConfigEntry<float> e = cfg.Bind(section, key, 1f, new ConfigDescription(
                $"{label}: multiplier of the weather's value. 1 = as in the game.", new AcceptableValueRange<float>(min, max)));
            var p = new SkyParam(label, e, min, max, false, apply);
            group.Add(p);
            all.Add(p);
        }

        private static void AddColor(ConfigFile cfg, string key, string what, Func<int, Gradient[]> gradients)
        {
            ConfigEntry<float> warmth = cfg.Bind("Sky.Colors", key + "Warmth", 0f, new ConfigDescription(
                $"Colour of the {what}: negative = colder (blue), positive = warmer (orange). 0 = as in the game.",
                new AcceptableValueRange<float>(-1f, 1f)));
            ConfigEntry<float> saturation = cfg.Bind("Sky.Colors", key + "Saturation", 1f, new ConfigDescription(
                $"Colour saturation of the {what}. 1 = as in the game, 0 = grey.", new AcceptableValueRange<float>(0f, 2f)));
            string name = char.ToUpperInvariant(what[0]) + what.Substring(1);
            // Both sliders are applied together in one pass over the gradient keys (by the warmth entry).
            Action<float> apply = _ =>
            {
                foreach (Gradient g in gradients(0))
                {
                    Tint(g, warmth.Value, saturation.Value);
                }
            };
            var pw = new SkyParam(name + ": warmth", warmth, -1f, 1f, true, apply);
            var ps = new SkyParam(name + ": saturation", saturation, 0f, 2f, false, null);
            Colors.Add(pw);
            Colors.Add(ps);
            all.Add(pw);
            all.Add(ps);
            colorPairs.Add(new KeyValuePair<SkyParam, SkyParam>(pw, ps));
        }

        private static readonly List<KeyValuePair<SkyParam, SkyParam>> colorPairs = new List<KeyValuePair<SkyParam, SkyParam>>();

        private static void Tint(Gradient g, float warmth, float saturation)
        {
            if (g == null)
            {
                return;
            }
            GradientColorKey[] keys = g.colorKeys;
            for (int i = 0; i < keys.Length; i++)
            {
                Color c = keys[i].color;
                float l = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
                c.r = Mathf.Max(0f, (l + (c.r - l) * saturation) * (1f + 0.3f * warmth));
                c.g = Mathf.Max(0f, (l + (c.g - l) * saturation) * (1f + 0.05f * warmth));
                c.b = Mathf.Max(0f, (l + (c.b - l) * saturation) * (1f - 0.3f * warmth));
                keys[i].color = c;
            }
            g.colorKeys = keys;
        }

        private static LeafManager Leaves
        {
            get
            {
                try
                {
                    return ServiceLocator.GetService<EnvironmentService>()?.FallingLeaves;
                }
                catch
                {
                    return null;
                }
            }
        }

        internal static bool PlaceChanged => Latitude.Value > -900f || Longitude.Value > -900f || Utc.Value > -900f;

        private static bool AnyChanged
        {
            get
            {
                foreach (SkyParam p in all)
                {
                    if (!p.IsDefault)
                    {
                        return true;
                    }
                }
                return PlaceChanged;
            }
        }

        private static bool refreshPending = true;

        private static void OnChanged(object sender, EventArgs e)
        {
            refreshPending = true;
            Refresh();
        }

        /// <summary>Install or remove the postfix and make the game copy the weather into the engine again.</summary>
        internal static void Refresh()
        {
            WeatherController wc = WeatherTweaks.Controller;
            if (wc == null)
            {
                return; // retried from Tick
            }
            refreshPending = false;
            bool need = AnyChanged;
            try
            {
                if (need && !patched)
                {
                    harmony = harmony ?? new Harmony(Plugin.Guid + ".sky");
                    harmony.PatchAll(typeof(SkyPatches));
                    patched = true;
                    Plugin.Log.LogInfo("Sky: weather changes on");
                }
                else if (!need && patched)
                {
                    harmony.UnpatchSelf();
                    patched = false;
                    Plugin.Log.LogInfo("Sky: back to the game's weather");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("skypatch", "Sky patch: " + ex);
                return;
            }
            wc.IsEnabled = wc.IsEnabled; // the setter sets the controller's invalidate flag: the weather is copied again
        }

        /// <summary>Postfix of WeatherSnapshotUtility.CopyFrom: the game has just written the blended weather into the engine.</summary>
        internal static void AfterCopy(WeatherSnapshot snapshot)
        {
            tod = TOD_Sky.Instance;
            if (snapshot != null)
            {
                GameLatitude = snapshot.Location.Latitude;
                GameLongitude = snapshot.Location.Longitude;
                GameUtc = snapshot.Location.Utc;
            }
            if (tod == null)
            {
                return;
            }
            foreach (SkyParam p in all)
            {
                if (p.Apply == null)
                {
                    continue;
                }
                if (p.Offset)
                {
                    // colour pair: warmth or saturation changed
                    if (p.IsDefault && SaturationOf(p).IsDefault)
                    {
                        continue;
                    }
                }
                else if (p.IsDefault)
                {
                    continue;
                }
                p.Apply(p.Entry.Value);
            }
            if (Latitude.Value > -900f) tod.World.Latitude = Latitude.Value;
            if (Longitude.Value > -900f) tod.World.Longitude = Longitude.Value;
            if (Utc.Value > -900f) tod.World.UTC = Utc.Value;
        }

        private static SkyParam SaturationOf(SkyParam warmth)
        {
            foreach (KeyValuePair<SkyParam, SkyParam> pair in colorPairs)
            {
                if (pair.Key == warmth)
                {
                    return pair.Value;
                }
            }
            return warmth;
        }

        /// <summary>Twice a second.</summary>
        internal static void Tick()
        {
            if (Time.timeScale == 0f)
            {
                FrozenUpdates();
            }
            if (refreshPending)
            {
                Refresh();
            }
            if (!dateApplied)
            {
                ApplyDate();
            }
        }

        /// <summary>
        /// The game's smooth updaters (weather, environment, region distance...) accumulate Time.deltaTime, so at game speed 0
        /// they stop: weather changes and the time of day would not reach the sky. While the world is stopped, these two are
        /// run here twice a second (cheap: the weather is copied only when something changed).
        /// </summary>
        private static void FrozenUpdates()
        {
            try
            {
                ServiceLocator.GetService<EnvironmentService>()?.ComputeUpdate();
                WeatherTweaks.Controller?.ComputeUpdate();
            }
            catch (Exception e)
            {
                Plugin.LogOnce("frozenupdates", "Sky while stopped: " + e);
            }
        }

        // ------------------------------------------------------------ date

        internal static DateTime GameDate => gameOffset;

        private static void ApplyDate()
        {
            if (TOD_Sky.Instance == null)
            {
                return; // the settings asset is loaded with the game; wait for the sky
            }
            try
            {
                GameSettingsData data = ScriptableObjectInstance<GameSettingsData>.Instance;
                if (data == null)
                {
                    return;
                }
                if (gameOffset == DateTime.MinValue)
                {
                    gameOffset = data.OffsetTime.Value;
                    Plugin.Log.LogInfo($"Sky: the game's sky date starts on {gameOffset:yyyy-MM-dd}");
                }
                DateTime target = gameOffset.AddDays(Math.Round(DateShift.Value));
                if (data.OffsetTime.Value != target)
                {
                    data.OffsetTime = new DateTimeField { Value = target };
                }
                dateApplied = true;
            }
            catch (Exception e)
            {
                dateApplied = true;
                Plugin.LogOnce("skydate", "Sky date: " + e);
            }
        }

        // ------------------------------------------------------------ wetness

        /// <summary>Every frame while Fixed: RainManager.Update moves the wetness by rain and dry time; the mod sets it first.</summary>
        internal static void FrameTick()
        {
            if (Wetness.Value != WetMode.Fixed)
            {
                return;
            }
            Rain.RainManager rm = Rain.RainManager.Instance;
            if (rm != null)
            {
                rm.terrainWetness = GroundWetness.Value;
                rm.puddleWetness = PuddleWetness.Value;
            }
        }

        // ------------------------------------------------------------ weather lock

        internal static void SetLock(WeatherLock mode)
        {
            Lock = mode;
            try
            {
                lockHarmony = lockHarmony ?? new Harmony(Plugin.Guid + ".weatherlock");
                if (mode != WeatherLock.Off && !lockPatched)
                {
                    lockHarmony.PatchAll(typeof(WeatherLockPatches));
                    lockPatched = true;
                }
                else if (mode == WeatherLock.Off && lockPatched)
                {
                    lockHarmony.UnpatchSelf();
                    lockPatched = false;
                }
            }
            catch (Exception e)
            {
                Lock = WeatherLock.Off;
                Plugin.LogOnce("weatherlock", "Weather lock: " + e);
            }
        }

        internal static int Blocked;

        internal static bool AllowScript(WeatherLayer layer)
        {
            if (Lock == WeatherLock.AllLayers || (Lock == WeatherLock.BaseLayer && layer == WeatherLayer.BaseLayer))
            {
                Blocked++;
                return false;
            }
            return true;
        }

        internal static void ResetAll()
        {
            foreach (SkyParam p in all)
            {
                p.Entry.Value = p.Default;
            }
            Latitude.Value = Longitude.Value = Utc.Value = GameSentinel;
            DateShift.Value = 0f;
        }

        /// <summary>The sky's current date and sun times, for the window.</summary>
        /// <summary>TOD's SunriseTime/SunsetTime are civil twilight (-6 deg) in universal time: add the clock's UTC offset.</summary>
        internal static bool TryGetSky(out DateTime date, out float sunrise, out float sunset, out float utc)
        {
            TOD_Sky t = TOD_Sky.Instance;
            date = default;
            sunrise = sunset = utc = 0f;
            if (t == null)
            {
                return false;
            }
            date = t.Cycle.DateTime;
            sunrise = t.SunriseTime;
            sunset = t.SunsetTime;
            utc = t.World.UTC;
            return true;
        }
    }

    /// <summary>Installed while some sky value is not at "Game".</summary>
    [HarmonyPatch]
    internal static class SkyPatches
    {
        [HarmonyPatch(typeof(WeatherSnapshotUtility), nameof(WeatherSnapshotUtility.CopyFrom))]
        [HarmonyPostfix]
        private static void CopyFromPostfix(WeatherSnapshot snapshot)
        {
            try
            {
                SkyTweaks.AfterCopy(snapshot);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("skycopy", "Sky: " + e);
            }
        }
    }

    /// <summary>Installed while the weather is locked: the game's scripts (VM) cannot change the weather layers.</summary>
    [HarmonyPatch]
    internal static class WeatherLockPatches
    {
        [HarmonyPatch(typeof(PLVirtualMachine.Dynamic.Components.Weather), "SetWeatherSample")]
        [HarmonyPrefix]
        private static bool Sample(WeatherLayer weatherLayer) => SkyTweaks.AllowScript(weatherLayer);

        [HarmonyPatch(typeof(PLVirtualMachine.Dynamic.Components.Weather), "SetWeatherSampleGT")]
        [HarmonyPrefix]
        private static bool SampleGT(WeatherLayer weatherLayer) => SkyTweaks.AllowScript(weatherLayer);

        [HarmonyPatch(typeof(PLVirtualMachine.Dynamic.Components.Weather), "SetWeatherLayerWeight")]
        [HarmonyPrefix]
        private static bool Weight(WeatherLayer weatherLayer) => SkyTweaks.Lock != WeatherLock.AllLayers || SkyTweaks.AllowScript(weatherLayer);

        [HarmonyPatch(typeof(PLVirtualMachine.Dynamic.Components.Weather), "SetWeatherLayerWeightGT")]
        [HarmonyPrefix]
        private static bool WeightGT(WeatherLayer weatherLayer) => SkyTweaks.Lock != WeatherLock.AllLayers || SkyTweaks.AllowScript(weatherLayer);
    }
}
