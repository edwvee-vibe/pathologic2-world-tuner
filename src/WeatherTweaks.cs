using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Engine.Common.Blenders;
using Engine.Common.Services;
using Engine.Common.Weather;
using Engine.Impl.Services;
using Engine.Source.Blenders;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// Weather switching, the same way as the console command change_weather: the snapshot is blended in on the base layer.
    /// The game's scripts (VM) keep driving the weather, so the next scripted change replaces it.
    /// Layers are blended in order Base, PlannedEvents, District, CutScene; each one over the previous by its opacity.
    /// </summary>
    internal static class WeatherTweaks
    {
        internal static ConfigEntry<float> BlendSeconds;

        private static readonly List<IWeatherSnapshot> snapshots = new List<IWeatherSnapshot>();

        internal static readonly WeatherLayer[] Layers =
        {
            WeatherLayer.BaseLayer, WeatherLayer.PlannedEventsLayer, WeatherLayer.DistrictLayer, WeatherLayer.CutSceneLayer
        };

        internal static void BindConfig(ConfigFile cfg)
        {
            BlendSeconds = cfg.Bind("Weather", "BlendSeconds", 5f, new ConfigDescription(
                "How long the weather picked in the window takes to blend in, in real seconds (0 = instantly).",
                new AcceptableValueRange<float>(0f, 120f)));
        }

        internal static WeatherController Controller
        {
            get
            {
                try
                {
                    return ServiceLocator.GetService<WeatherController>();
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>All weather snapshot templates (Clear, CloudyPartial, RainSun, Cloudy, RainLight, RainHeavy, Menu).</summary>
        internal static List<IWeatherSnapshot> Snapshots
        {
            get
            {
                // Templates are loaded by background threads at startup; the game applies its first weather (main menu)
                // only after that, so a weather on the base layer means the template dictionary is complete.
                if (snapshots.Count == 0 && TemplatesReady())
                {
                    try
                    {
                        ITemplateService templates = ServiceLocator.GetService<ITemplateService>();
                        if (templates != null)
                        {
                            foreach (IWeatherSnapshot s in templates.GetTemplates<IWeatherSnapshot>())
                            {
                                if (s != null)
                                {
                                    snapshots.Add(s);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.LogOnce("templates", "Failed to read weather templates: " + e);
                        snapshots.Clear();
                    }
                }
                return snapshots;
            }
        }

        private static bool TemplatesReady()
        {
            SmoothBlender<IWeatherSnapshot> blender = LayerBlender(Controller, WeatherLayer.BaseLayer);
            return blender != null && blender.SnapshotTemplateId != Guid.Empty;
        }

        internal static IWeatherSnapshot FindSnapshot(Guid id)
        {
            foreach (IWeatherSnapshot s in Snapshots)
            {
                if (s.Id == id)
                {
                    return s;
                }
            }
            return null;
        }

        internal static SmoothBlender<IWeatherSnapshot> LayerBlender(WeatherController wc, WeatherLayer layer)
        {
            IWeatherLayerBlenderItem item = wc?.GetItem(layer);
            return item?.Blender as SmoothBlender<IWeatherSnapshot>;
        }

        /// <summary>Snapshot shown on the layer: the blend target while blending, otherwise the last one blended to.</summary>
        internal static IWeatherSnapshot LayerSnapshot(WeatherController wc, WeatherLayer layer)
        {
            SmoothBlender<IWeatherSnapshot> blender = LayerBlender(wc, layer);
            return blender == null ? null : FindSnapshot(blender.SnapshotTemplateId);
        }

        internal static void Apply(IWeatherSnapshot snapshot)
        {
            WeatherController wc = Controller;
            ISmoothBlender<IWeatherSnapshot> blender = wc?.GetItem(WeatherLayer.BaseLayer)?.Blender;
            if (blender == null)
            {
                Plugin.ShowMessage("Weather: not available right now");
                return;
            }
            // Same as change_weather: the interval is in game time (AbsoluteGameTime runs GameTimeFactor times faster).
            float factor = 1f;
            try
            {
                factor = ServiceLocator.GetService<ITimeService>().GameTimeFactor;
            }
            catch
            {
                // keep 1
            }
            float seconds = Mathf.Max(0f, BlendSeconds.Value);
            blender.BlendTo(snapshot, TimeSpan.FromSeconds(seconds * factor));
            Plugin.ShowMessage($"Weather: {snapshot.Name}" + (seconds > 0f ? $" (blending {seconds:0} s)" : ""));
        }

        internal static string LayerName(WeatherLayer layer)
        {
            switch (layer)
            {
                case WeatherLayer.BaseLayer: return "Base";
                case WeatherLayer.PlannedEventsLayer: return "Planned events";
                case WeatherLayer.DistrictLayer: return "District";
                case WeatherLayer.CutSceneLayer: return "Cutscene";
                default: return layer.ToString();
            }
        }
    }
}
