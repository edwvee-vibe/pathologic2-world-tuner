using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    /// <summary>Tabs "Sky" (weather editor, place and date) and "Life & Lights" (crowds, lamps, lantern, birds, leaves), plus the
    /// weather lock and wetness rows of the Weather tab.</summary>
    internal static partial class Menu
    {
        private static readonly string[] LockModes = { "Off", "Base layer", "All layers" };
        private static readonly string[] WetModes = { "Game (rain wets, sun dries)", "Fixed" };
        private static readonly string[] ShadowModes = { "Game", "Off", "Hard", "Soft" };

        // ------------------------------------------------------------ Weather tab: lock and wetness

        private static void DrawWeatherExtras()
        {
            GUILayout.Space(8);
            GUILayout.Label("Weather lock", headerStyle);
            int mode = (int)SkyTweaks.Lock;
            int newMode = GUILayout.Toolbar(mode, LockModes);
            if (newMode != mode)
            {
                SkyTweaks.SetLock((WeatherLock)newMode);
            }
            GUILayout.Label("Keeps the game's scripts from changing the weather: Base layer blocks the everyday weather changes, " +
                            "All layers also blocks storms, district and cutscene weather and their weights. Weather picked above still " +
                            "works; loading a save still restores its weather. Not saved: off after a restart." +
                            (SkyTweaks.Blocked > 0 ? $"  Blocked so far: {SkyTweaks.Blocked}." : ""), hintStyle);

            GUILayout.Space(8);
            GUILayout.Label("Wet streets", headerStyle);
            int wet = (int)SkyTweaks.Wetness.Value;
            int newWet = GUILayout.Toolbar(wet, WetModes);
            if (newWet != wet)
            {
                SkyTweaks.Wetness.Value = (WetMode)newWet;
                Plugin.RequestSave();
            }
            bool oldEnabled = GUI.enabled;
            GUI.enabled = SkyTweaks.Wetness.Value == WetMode.Fixed;
            DefaultRow("Ground wetness", SkyTweaks.GroundWetness, 0f, 1f, 1f, v => $"{v * 100f:0}%", false);
            DefaultRow("Puddles", SkyTweaks.PuddleWetness, 0f, 1f, 1f, v => $"{v * 100f:0}%", false);
            GUI.enabled = oldEnabled;
            Rain.RainManager rm = Rain.RainManager.Instance;
            GUILayout.Label(rm != null
                ? $"Now: ground {rm.terrainWetness * 100f:0}%, puddles {rm.puddleWetness * 100f:0}%, rain {rm.actualRainIntensity * 100f:0}%. " +
                  "Fixed keeps wet ground and puddles without rain (or dry ones in the rain). Saved."
                : "Rain is not available yet.", hintStyle);
        }

        // ------------------------------------------------------------ Sky tab

        private static void DrawSky()
        {
            DrawPresets(Presets.Sky);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Changes apply on top of every weather (the game blends them as usual)", hintStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("All to game values", GUILayout.Width(170)))
            {
                pending.Clear();
                SkyTweaks.ResetAll();
                Plugin.RequestSave();
            }
            GUILayout.EndHorizontal();

            // --- place and date
            GUILayout.Space(4);
            GUILayout.Label("Where and when the town is (sun, moon and stars)", headerStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Game")) SetPlace(SkyTweaks.GameSentinel, SkyTweaks.GameSentinel, 0f);
            if (GUILayout.Button("Noon at 12:00"))
            {
                float lon = SkyTweaks.Longitude.Value > -900f ? SkyTweaks.Longitude.Value : SkyTweaks.GameLongitude;
                SkyTweaks.Utc.Value = Mathf.Round(lon / 15f * 4f) / 4f;
                Plugin.RequestSave();
            }
            if (GUILayout.Button("White nights")) SetPlace(64f, SkyTweaks.Longitude.Value, -55f);
            if (GUILayout.Button("Polar day")) SetPlace(75f, SkyTweaks.Longitude.Value, -55f);
            if (GUILayout.Button("Polar night")) SetPlace(75f, SkyTweaks.Longitude.Value, 128f);
            if (GUILayout.Button("Equator")) SetPlace(0f, SkyTweaks.Longitude.Value, 0f);
            GUILayout.EndHorizontal();
            PlaceRow("Latitude", SkyTweaks.Latitude, -89f, 89f, SkyTweaks.GameLatitude, v => v >= 0f ? $"{v:0.#} N" : $"{-v:0.#} S");
            PlaceRow("Longitude", SkyTweaks.Longitude, -180f, 180f, SkyTweaks.GameLongitude, v => v >= 0f ? $"{v:0.#} E" : $"{-v:0.#} W");
            PlaceRow("Clock time zone", SkyTweaks.Utc, -12f, 14f, SkyTweaks.GameUtc, v => $"UTC{v:+0.##;-0.##;+0}");
            DateTime baseDate = SkyTweaks.GameDate != DateTime.MinValue ? SkyTweaks.GameDate : new DateTime(1915, 8, 15);
            FloatRow("Date shift", SkyTweaks.DateShift, -183f, 183f, 1f, Mode.Plain, 0f,
                v => $"{v:+0;-0;0} d: from " + baseDate.AddDays(Math.Round(v)).ToString("d MMM", CultureInfo.InvariantCulture));
            if (SkyTweaks.TryGetSky(out DateTime date, out float dawn, out float dusk, out float utc))
            {
                bool polar = float.IsNaN(dawn) || float.IsNaN(dusk);
                GUILayout.Label("Sky now: " + date.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture) + ".  " +
                                (polar ? "No dawn or dusk today (polar day or night)."
                                       : $"Dawn {Clock(dawn + utc)}, dusk {Clock(dusk + utc)} (civil twilight, sun 6 deg below the horizon)."),
                    valueStyle);
            }
            GUILayout.Label("The game puts the town at 52 N, 85 E with clocks at UTC+8 and starts the sky on 15 August 1915. " +
                            "Only the sky moves: street lamps, crowds and quests follow the clock, not the sun.", hintStyle);

            SkyGroup("Clouds", SkyTweaks.Clouds, null);
            SkyGroup("Day", SkyTweaks.Day, null);
            SkyGroup("Night", SkyTweaks.Night, "Moonlight x6-x10 gives clearly lighter nights.");
            SkyGroup("Sun, moon and stars", SkyTweaks.Bodies, null);
            SkyGroup("Colours (day and night)", SkyTweaks.Colors, null);
            var weather = new List<SkyParam>();
            foreach (SkyParam p in SkyTweaks.Elements)
            {
                if (p.Label.IndexOf("leaves", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    weather.Add(p);
                }
            }
            SkyGroup("Rain and wind", weather, "Rain scales the rain of every rainy weather (Clear has none). Wind moves the clouds.");
            GUILayout.Label("With everything at Game the mod does not touch the weather. Changes are applied when the game updates the " +
                            "weather (a few times a second during a weather change, otherwise only on a change): no cost per frame.", hintStyle);
        }

        private static string Clock(float hours)
        {
            int m = ((Mathf.RoundToInt(hours * 60f) % 1440) + 1440) % 1440;
            return $"{m / 60}:{m % 60:00}";
        }

        private static void SetPlace(float lat, float lon, float shift)
        {
            SkyTweaks.Latitude.Value = lat;
            SkyTweaks.Longitude.Value = lon;
            if (lat <= -900f)
            {
                SkyTweaks.Utc.Value = SkyTweaks.GameSentinel;
            }
            SkyTweaks.DateShift.Value = shift;
            Plugin.RequestSave();
        }

        private static void SkyGroup(string title, List<SkyParam> items, string hint)
        {
            GUILayout.Space(8);
            GUILayout.Label(title, headerStyle);
            foreach (SkyParam p in items)
            {
                FloatRow(p.Label, p.Entry, p.Min, p.Max, p.Offset ? 0.05f : 0.05f, Mode.Plain, p.Default,
                    p.Offset ? (Func<float, string>)(v => $"{v:+0.00;-0.00;0}") : v => $"x{v:0.00}");
            }
            if (hint != null)
            {
                GUILayout.Label(hint, hintStyle);
            }
        }

        /// <summary>A value with a "Game" sentinel (-999) and a signed range.</summary>
        private static void PlaceRow(string label, ConfigEntry<float> entry, float min, float max, float game, Func<float, string> format)
        {
            bool isGame = entry.Value <= -900f;
            float value = isGame ? game : entry.Value;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            float newValue = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(SliderWidth));
            GUILayout.Label("  " + format(value) + (isGame ? " (game)" : ""), valueStyle, GUILayout.Width(ValueWidth));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Game", isGame ? selectedButton : GUI.skin.button, GUILayout.Width(ButtonWidth)) && !isGame)
            {
                entry.Value = SkyTweaks.GameSentinel;
                Plugin.RequestSave();
            }
            GUILayout.EndHorizontal();
            if (Math.Abs(newValue - value) > 0.001f)
            {
                entry.Value = Mathf.Clamp(Mathf.Round(newValue * 4f) / 4f, min, max);
                Plugin.RequestSave();
            }
        }

        // ------------------------------------------------------------ Life & Lights tab

        private static void DrawLife()
        {
            DrawPresets(Presets.Life);
            // --- crowds
            GUILayout.Label("Street crowds", headerStyle);
            if (!LifeTweaks.CrowdAvailable)
            {
                GUILayout.Label("Available in a running game (not in the main menu).", warnStyle);
            }
            else
            {
                FloatRow("Appear from", LifeTweaks.CrowdSpawnMin, 5f, 1000f, 5f, Mode.AbsoluteLog, LifeTweaks.GameSpawnMin, Meters);
                FloatRow("Appear up to", LifeTweaks.CrowdSpawnMax, 10f, 1000f, 5f, Mode.AbsoluteLog, LifeTweaks.GameSpawnMax, Meters);
                FloatRow("Removed beyond", LifeTweaks.CrowdDestroy, 20f, 1200f, 5f, Mode.AbsoluteLog, LifeTweaks.GameDestroy, Meters);
                FloatRow("Max passers-by", LifeTweaks.CrowdLimit, 0f, 3000f, 10f, Mode.Absolute, LifeTweaks.GameLimit, v => $"{v:0}");
                LifeTweaks.EffectiveCrowd(out float min, out float max, out float destroy);
                float draw = DistanceTweaks.Objects.Value > 0f ? DistanceTweaks.Objects.Value : DistanceTweaks.GameDefaultCull;
                GUILayout.Label($"Passers-by appear {min:0}-{max:0} m away and are removed beyond {destroy:0} m; people are drawn up to {draw:0} m.",
                    valueStyle);
                GUILayout.Label("Each crowd point gets a person when the player is inside the ring, so a farther ring makes people appear out " +
                                "of sight, but points closer than \"Appear from\" stay empty until you walk away and back. A wider ring and a " +
                                "farther removal mean more NPCs at once: more CPU.", max > 200f ? warnStyle : hintStyle);
            }

            // --- lamps
            GUILayout.Space(10);
            GUILayout.Label("Street lamps and lights", headerStyle);
            FloatRow("Shadows up to", LifeTweaks.LampShadows, 0.25f, 10f, 0.05f, Mode.Scale, 1f,
                v => $"x{v:0.00} = {LifeTweaks.TypicalShadowDistance * v:0} m", null, true);
            FloatRow("Light fade distance", LifeTweaks.LampRange, 0.5f, 8f, 0.05f, Mode.Scale, 1f, v => $"x{v:0.00}", null, true);
            if (LifeTweaks.ShadowLamps + LifeTweaks.FadeLamps > 0)
            {
                GUILayout.Label($"      {LifeTweaks.ShadowLamps} lights switch shadows off by distance, {LifeTweaks.FadeLamps} dim with distance (in the loaded scenes).", hintStyle);
            }
            GUILayout.Label("Lamps drop their shadows beyond 15-25 m and dim their light farther from the camera. Shadows of point lights are " +
                            "among the most expensive things to render: x2 and above can cost a lot of GPU time in lit streets. " +
                            "Applied when the slider is released.", LifeTweaks.LampShadows.Value > 2.01f ? warnStyle : hintStyle);

            // --- lantern
            GUILayout.Space(10);
            GUILayout.Label("Player's lantern", headerStyle);
            FloatRow("Range", LifeTweaks.LanternRange, 0.25f, 4f, 0.05f, Mode.Scale, 1f, v => $"x{v:0.00}");
            FloatRow("Brightness", LifeTweaks.LanternIntensity, 0f, 4f, 0.05f, Mode.Scale, 1f, v => $"x{v:0.00}");
            FloatRow("Colour warmth", LifeTweaks.LanternWarmth, -1f, 1f, 0.05f, Mode.Plain, 0f, v => $"{v:+0.00;-0.00;0}");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Shadows", GUILayout.Width(LabelWidth));
            int sm = (int)LifeTweaks.LanternShadowMode.Value;
            int newSm = GUILayout.Toolbar(sm, ShadowModes, GUILayout.Width(SliderWidth + ValueWidth));
            if (newSm != sm)
            {
                LifeTweaks.LanternShadowMode.Value = (LanternShadows)newSm;
                Plugin.RequestSave();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(LifeTweaks.LanternLights > 0 ? $"      {LifeTweaks.LanternInfo}" : "      Lantern lights are found when something here is changed.", hintStyle);

            // --- birds and leaves
            GUILayout.Space(10);
            GUILayout.Label("Birds and leaves", headerStyle);
            FloatRow("Birds per flock", LifeTweaks.Birds, 0f, 4f, 0.05f, Mode.Scale, 1f, v => $"x{v:0.00}", null, true);
            if (LifeTweaks.Flocks > 0)
            {
                GUILayout.Label($"      {LifeTweaks.Flocks} flock(s) found, {LifeTweaks.BirdsNow} birds wanted (they are added one at a time).", hintStyle);
            }
            foreach (SkyParam p in SkyTweaks.Elements)
            {
                if (p.Label.IndexOf("leaves", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    FloatRow(p.Label, p.Entry, p.Min, p.Max, 0.05f, Mode.Plain, 1f, v => $"x{v:0.00}");
                }
            }
            GUILayout.Label("Every bird is an object with its own flight code: x2 and more costs CPU where flocks are. Falling leaves come " +
                            "with the weather (rate per weather, radius around the player).", hintStyle);
        }
    }
}
