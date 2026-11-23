using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Engine.Common.Weather;
using Engine.Impl.Services;
using Engine.Impl.Weather;
using Engine.Source.Blenders;
using HarmonyLib;
using UnityEngine;
using UnityStandardAssets.ImageEffects;

namespace Pathologic2WorldTuner
{
    internal enum FogMode
    {
        Off,
        Global,
        PerWeather
    }

    /// <summary>Fog settings applied to one weather snapshot (or to all of them in Global mode).</summary>
    internal sealed class FogProfile
    {
        public readonly string Name;
        public readonly ConfigEntry<float> DensityScale;
        public readonly ConfigEntry<float> StartOffset;
        public readonly ConfigEntry<float> Height;
        public readonly ConfigEntry<float> FogginessScale;

        public FogProfile(ConfigFile cfg, string name, string who)
        {
            Name = name;
            string section = "Fog." + name;
            DensityScale = cfg.Bind(section, "DensityScale", 1f, new ConfigDescription(
                $"Fog density multiplier ({who}). 1 = as in the game, 0 = no distance fog.",
                new AcceptableValueRange<float>(0f, 3f)));
            StartOffset = cfg.Bind(section, "StartOffset", 0f, new ConfigDescription(
                $"Meters added to the fog start distance ({who}): no fog closer than this. The game uses 0.",
                new AcceptableValueRange<float>(0f, 200f)));
            Height = cfg.Bind(section, "Height", -1f, new ConfigDescription(
                $"Height fog level in meters ({who}). -1 = as in the game (60).",
                new AcceptableValueRange<float>(-1f, 300f)));
            FogginessScale = cfg.Bind(section, "FogginessScale", 1f, new ConfigDescription(
                $"Multiplier of the sky haze (Day/Night Fogginess of the weather, {who}). 1 = as in the game. The result is limited to 0-1.",
                new AcceptableValueRange<float>(0f, 3f)));
            DensityScale.SettingChanged += FogTweaks.OnSettingChanged;
            StartOffset.SettingChanged += FogTweaks.OnSettingChanged;
            Height.SettingChanged += FogTweaks.OnSettingChanged;
            FogginessScale.SettingChanged += FogTweaks.OnSettingChanged;
        }

        public void Reset()
        {
            DensityScale.Value = 1f;
            StartOffset.Value = 0f;
            Height.Value = -1f;
            FogginessScale.Value = 1f;
        }

        public void CopyFrom(FogProfile other)
        {
            DensityScale.Value = other.DensityScale.Value;
            StartOffset.Value = other.StartOffset.Value;
            Height.Value = other.Height.Value;
            FogginessScale.Value = other.FogginessScale.Value;
        }

        public bool IsDefault => Math.Abs(DensityScale.Value - 1f) < 0.0001f && Math.Abs(StartOffset.Value) < 0.0001f &&
                                 Height.Value < 0f && Math.Abs(FogginessScale.Value - 1f) < 0.0001f;
    }

    /// <summary>
    /// The fog lives in every weather snapshot (Fog.Density / StartDistance / Height, Day/Night.Fogginess), and the engine gets
    /// a blend of the weather layers and transitions. So the mod edits the snapshot templates themselves in memory
    /// (what tools/patch-fog.ps1 does on disk), keeping the original values. All changes are linear, so blending stays correct.
    /// Saves only store template ids, so they are not affected.
    /// </summary>
    internal static class FogTweaks
    {
        internal static readonly string[] KnownWeathers = { "Clear", "CloudyPartial", "RainSun", "Cloudy", "RainLight", "RainHeavy", "Menu" };

        internal static ConfigEntry<FogMode> Mode;
        internal static ConfigEntry<bool> KeepVolumetricLight;
        internal static ConfigEntry<float> DistanceDensity;
        internal static ConfigEntry<float> HeightDensity;
        internal static ConfigEntry<bool> DisableDistanceFog;
        internal static ConfigEntry<bool> DisableHeightFog;
        internal static FogProfile Global;

        private static ConfigFile cfg;
        private static readonly Dictionary<string, FogProfile> perWeather = new Dictionary<string, FogProfile>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Guid, Original> originals = new Dictionary<Guid, Original>();
        private static bool dirty = true;
        private static bool applying;

        private static readonly AccessTools.FieldRef<object, bool> layerBlenderInvalidate =
            AccessTools.FieldRefAccess<bool>(typeof(LayerBlender<IWeatherSnapshot>), "invalidate");

        // Camera fog component values as the game set them (weather never writes these fields).
        private static int fogInstanceId;
        private static float gameDistanceDensity = 1f, gameHeightDensity = 2f;
        private static bool gameDistanceFog = true, gameHeightFog = true;

        private sealed class Original
        {
            public float Density, Start, Height, DayFogginess, NightFogginess;
        }

        internal static void BindConfig(ConfigFile config)
        {
            cfg = config;
            Mode = cfg.Bind("Fog", "Mode", FogMode.Off,
                "Off: fog as in the game. Global: one set of fog values for every weather (section Fog.Global). " +
                "PerWeather: own values for each weather (sections Fog.Clear, Fog.RainHeavy, ...).");
            KeepVolumetricLight = cfg.Bind("Fog", "KeepVolumetricLight", true,
                "The game scales volumetric light (light shafts of the sun, moon and lamps) with the fog density. " +
                "true: the light shafts stay as in the game whatever fog density is set here. false: they follow the new density.");
            DistanceDensity = cfg.Bind("Fog.Advanced", "DistanceDensity", -1f, new ConfigDescription(
                "GlobalFog.distanceDensity of the camera (shader multiplier of the distance fog). -1 = as in the game (1).",
                new AcceptableValueRange<float>(-1f, 10f)));
            HeightDensity = cfg.Bind("Fog.Advanced", "HeightDensity", -1f, new ConfigDescription(
                "GlobalFog.heightDensity of the camera (how fast the height fog thins out upwards). -1 = as in the game (2).",
                new AcceptableValueRange<float>(-1f, 10f)));
            DisableDistanceFog = cfg.Bind("Fog.Advanced", "DisableDistanceFog", false, "Turn off the distance part of the fog shader.");
            DisableHeightFog = cfg.Bind("Fog.Advanced", "DisableHeightFog", false, "Turn off the height part of the fog shader.");

            Global = new FogProfile(cfg, "Global", "all weathers");
            foreach (string name in KnownWeathers)
            {
                ProfileOf(name);
            }
            Mode.SettingChanged += OnSettingChanged;
            KeepVolumetricLight.SettingChanged += OnSettingChanged;
        }

        /// <summary>Per-weather profile; bound on first use for weathers not in the known list.</summary>
        internal static FogProfile ProfileOf(string weather)
        {
            if (string.IsNullOrEmpty(weather))
            {
                return null;
            }
            if (!perWeather.TryGetValue(weather, out FogProfile p))
            {
                p = new FogProfile(cfg, weather, "weather " + weather);
                perWeather[weather] = p;
            }
            return p;
        }

        /// <summary>The profile in effect for a weather in the current mode; null = as in the game.</summary>
        private static FogProfile ActiveProfile(string weather)
        {
            switch (Mode.Value)
            {
                case FogMode.Global: return Global;
                case FogMode.PerWeather: return ProfileOf(weather);
                default: return null;
            }
        }

        internal static void OnSettingChanged(object sender, EventArgs e)
        {
            if (applying)
            {
                return;
            }
            dirty = true;
            Apply();
        }

        internal static void MarkDirty()
        {
            dirty = true;
        }

        private static bool templatesTouched, cameraFogTouched;

        private static bool CameraFogActive =>
            DistanceDensity.Value >= 0f || HeightDensity.Value >= 0f || DisableDistanceFog.Value || DisableHeightFog.Value;

        internal static void Tick()
        {
            if (dirty)
            {
                // Nothing to do while the fog is as in the game and the templates were never changed.
                if (Mode.Value == FogMode.Off && !templatesTouched)
                {
                    dirty = false;
                }
                else
                {
                    Apply();
                }
            }
            if (CameraFogActive || cameraFogTouched)
            {
                ApplyCameraFog();
                cameraFogTouched = CameraFogActive;
            }
        }

        // ------------------------------------------------------------ snapshot templates

        private static void Apply()
        {
            List<IWeatherSnapshot> list = WeatherTweaks.Snapshots;
            if (list.Count == 0)
            {
                return; // templates are not loaded yet; retried in Tick
            }
            applying = true;
            try
            {
                foreach (IWeatherSnapshot s in list)
                {
                    if (s is WeatherSnapshot ws)
                    {
                        Write(ws, OriginalOf(ws), ActiveProfile(s.Name));
                    }
                }
                RefreshBlenders();
                dirty = false;
                templatesTouched = Mode.Value != FogMode.Off;
            }
            finally
            {
                applying = false;
            }
        }

        private static Original OriginalOf(WeatherSnapshot ws)
        {
            if (!originals.TryGetValue(ws.Id, out Original o))
            {
                o = new Original
                {
                    Density = ws.Fog.Density,
                    Start = ws.Fog.StartDistance,
                    Height = ws.Fog.Height,
                    DayFogginess = ws.Day.Fogginess,
                    NightFogginess = ws.Night.Fogginess
                };
                originals[ws.Id] = o;
                Plugin.Log.LogInfo($"Weather {ws.Name}: fog density {o.Density}, start {o.Start}, height {o.Height}, fogginess {o.DayFogginess}/{o.NightFogginess}");
            }
            return o;
        }

        private static void Write(WeatherSnapshot ws, Original o, FogProfile p)
        {
            if (p == null)
            {
                ws.Fog.Density = o.Density;
                ws.Fog.StartDistance = o.Start;
                ws.Fog.Height = o.Height;
                ws.Day.Fogginess = o.DayFogginess;
                ws.Night.Fogginess = o.NightFogginess;
                return;
            }
            ws.Fog.Density = o.Density * p.DensityScale.Value;
            ws.Fog.StartDistance = o.Start + p.StartOffset.Value;
            ws.Fog.Height = p.Height.Value >= 0f ? p.Height.Value : o.Height;
            float fs = p.FogginessScale.Value;
            ws.Day.Fogginess = Math.Abs(fs - 1f) < 0.0001f ? o.DayFogginess : Mathf.Clamp01(o.DayFogginess * fs);
            ws.Night.Fogginess = Math.Abs(fs - 1f) < 0.0001f ? o.NightFogginess : Mathf.Clamp01(o.NightFogginess * fs);
        }

        /// <summary>
        /// Make the change visible right away. A blend in progress targets the template object itself and picks the change up;
        /// a finished blend holds a copy (SmoothBlender.Current), so it is updated from the template here. Then the layer blend
        /// and the weather controller are invalidated, which copies the result into the engine (FogController.CopyFrom).
        /// </summary>
        private static void RefreshBlenders()
        {
            WeatherController wc = WeatherTweaks.Controller;
            if (wc == null)
            {
                return;
            }
            foreach (WeatherLayer layer in WeatherTweaks.Layers)
            {
                SmoothBlender<IWeatherSnapshot> sb = WeatherTweaks.LayerBlender(wc, layer);
                if (sb == null || sb.Target != null)
                {
                    continue;
                }
                if (WeatherTweaks.FindSnapshot(sb.SnapshotTemplateId) is WeatherSnapshot tpl && sb.Current is WeatherSnapshot cur)
                {
                    cur.Fog.Density = tpl.Fog.Density;
                    cur.Fog.StartDistance = tpl.Fog.StartDistance;
                    cur.Fog.Height = tpl.Fog.Height;
                    cur.Day.Fogginess = tpl.Day.Fogginess;
                    cur.Night.Fogginess = tpl.Night.Fogginess;
                }
            }
            if (wc.Blender != null)
            {
                layerBlenderInvalidate(wc.Blender) = true;
            }
            // FogController raises DensityChangedEvent (volumetric light) only when the density differs from RenderSettings.
            RenderSettings.fogDensity += 0.00001f;
            wc.IsEnabled = wc.IsEnabled; // the setter sets the controller's invalidate flag
        }

        // ------------------------------------------------------------ volumetric light

        private static int volFrame = -1;
        private static float volIn, volOut;

        /// <summary>
        /// Fog density as the game would have it without the mod, for VolumetricLightDensity.ApplyDensity.
        /// Exact for finished blends; during a transition between weathers with different multipliers it is an approximation.
        /// </summary>
        internal static float GameDensity(float density)
        {
            if (Mode.Value == FogMode.Off || !KeepVolumetricLight.Value || originals.Count == 0)
            {
                return density;
            }
            if (volFrame == Time.frameCount && Math.Abs(volIn - density) < 1e-7f)
            {
                return volOut;
            }
            float result = density;
            WeatherController wc = WeatherTweaks.Controller;
            if (wc != null)
            {
                float value = 0f;
                bool ok = true;
                foreach (WeatherLayer layer in WeatherTweaks.Layers)
                {
                    IWeatherLayerBlenderItem item = wc.GetItem(layer);
                    SmoothBlender<IWeatherSnapshot> sb = item?.Blender as SmoothBlender<IWeatherSnapshot>;
                    if (sb == null)
                    {
                        continue;
                    }
                    float opacity = item.Opacity;
                    if (opacity <= 0f)
                    {
                        continue;
                    }
                    IWeatherSnapshot snap = WeatherTweaks.FindSnapshot(sb.SnapshotTemplateId);
                    if (snap == null || !originals.TryGetValue(snap.Id, out Original o))
                    {
                        ok = false;
                        break;
                    }
                    float layerDensity = o.Density;
                    if (sb.Target != null && sb.Current is WeatherSnapshot cur)
                    {
                        FogProfile p = ActiveProfile(snap.Name);
                        float k = p == null ? 1f : p.DensityScale.Value;
                        layerDensity = k > 0.001f ? cur.Fog.Density / k : o.Density;
                    }
                    value = value * (1f - opacity) + layerDensity * opacity;
                }
                if (ok)
                {
                    result = value;
                }
            }
            volFrame = Time.frameCount;
            volIn = density;
            volOut = result;
            return result;
        }

        // ------------------------------------------------------------ camera fog component

        internal static GlobalFog CameraFog
        {
            get
            {
                Camera cam = GameCamera.Instance != null ? GameCamera.Instance.Camera : null;
                return cam != null ? cam.GetComponent<GlobalFog>() : null;
            }
        }

        private static void ApplyCameraFog()
        {
            GlobalFog fog = CameraFog;
            if (fog == null)
            {
                return;
            }
            if (fog.GetInstanceID() != fogInstanceId)
            {
                fogInstanceId = fog.GetInstanceID();
                gameDistanceDensity = fog.distanceDensity;
                gameHeightDensity = fog.heightDensity;
                gameDistanceFog = fog.distanceFog;
                gameHeightFog = fog.heightFog;
            }
            float dd = DistanceDensity.Value >= 0f ? Mathf.Max(0.001f, DistanceDensity.Value) : gameDistanceDensity;
            float hd = HeightDensity.Value >= 0f ? Mathf.Max(0.001f, HeightDensity.Value) : gameHeightDensity;
            bool df = gameDistanceFog && !DisableDistanceFog.Value;
            bool hf = gameHeightFog && !DisableHeightFog.Value;
            if (fog.distanceDensity != dd) fog.distanceDensity = dd;
            if (fog.heightDensity != hd) fog.heightDensity = hd;
            if (fog.distanceFog != df) fog.distanceFog = df;
            if (fog.heightFog != hf) fog.heightFog = hf;
        }

        internal static float GameDistanceDensity => gameDistanceDensity;
        internal static float GameHeightDensity => gameHeightDensity;

        /// <summary>Original (game) fog values of a weather, for display; false if not known yet.</summary>
        internal static bool TryGetOriginal(IWeatherSnapshot s, out float density, out float start, out float height)
        {
            density = start = height = 0f;
            if (s == null || !originals.TryGetValue(s.Id, out Original o))
            {
                return false;
            }
            density = o.Density;
            start = o.Start;
            height = o.Height;
            return true;
        }

        /// <summary>Call after changing the camera fog settings from the window, so they apply without waiting for the tick.</summary>
        internal static void ApplyCameraFogNow()
        {
            ApplyCameraFog();
            cameraFogTouched = CameraFogActive;
        }
    }

    [HarmonyPatch]
    internal static class FogPatches
    {
        [HarmonyPatch(typeof(VolumetricLightDensity), "ApplyDensity")]
        [HarmonyPrefix]
        private static void ApplyDensityPrefix(ref float density)
        {
            try
            {
                density = FogTweaks.GameDensity(density);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("voldensity", "Volumetric light density: " + e);
            }
        }
    }
}
