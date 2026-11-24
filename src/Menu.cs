using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Engine.Common.Weather;
using Engine.Impl.Services;
using Engine.Source.Blenders;
using InputServices;
using UnityEngine;
using UnityStandardAssets.ImageEffects;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// The window (IMGUI): tabs Fog, Distance, Weather, Sky, Life & Lights, Time & Player, Free Camera & Image, Memory & Loading
    /// (Sky and Life in MenuSky.cs, Photo in MenuPhoto.cs, Memory in MenuMemory.cs). Values come from the same ConfigEntry objects as the config file
    /// and are saved back to it on mouse release (Plugin.RequestSave). Weather and time are one-off actions and are not saved.
    /// </summary>
    internal static partial class Menu
    {
        private const int WindowId = 0x0D1AB;
        private const float BaseWidth = 780f;
        // Width inside the scroll view: window padding (14 + 14) and room for the vertical scrollbar.
        private const float ContentWidth = BaseWidth - 28f - 24f;
        private const float LabelWidth = 200f, SliderWidth = 270f, ValueWidth = 160f, ButtonWidth = 62f;

        private static readonly string[] Tabs = { "Fog", "Distance", "Weather", "Sky", "Life & Lights", "Time & Player", "Free Camera & Image", "Memory & Loading" };
        private static readonly string[] FogModes = { "Off (game)", "Global", "Per weather" };

        public static bool IsOpen { get; private set; }

        private static Rect window = new Rect(60, 60, BaseWidth, 10);
        private static bool positioned;
        private static int lastChangeFrame = -1;
        private static int tab;

        internal const int FogTab = 0, DistanceTab = 1, WeatherTab = 2, SkyTab = 3, LifeTab = 4, TimeTab = 5, PhotoTab = 6, MemoryTab = 7;
        internal static int CurrentTab => tab;

        private static string physicsText;
        private static Vector2 scroll;
        private static readonly float[] contentHeight = new float[8];
        private static string fogWeather;
        private static ConfigEntry<KeyboardShortcut> waitingFor;
        private static readonly Dictionary<ConfigEntryBase, float> pending = new Dictionary<ConfigEntryBase, float>();
        private static GUIStyle windowStyle, headerStyle, statusStyle, selectedButton, hintStyle, warnStyle, valueStyle, footerStyle;
        private static Texture2D windowBackground, selectedBackground;

        public static void Toggle()
        {
            SetOpen(!IsOpen);
        }

        public static void SetOpen(bool open)
        {
            if (IsOpen == open)
            {
                return;
            }
            IsOpen = open;
            CloseSelect();
            Plugin.SetCursorPatch(open);
            lastChangeFrame = Time.frameCount;
            waitingFor = null;
            if (open)
            {
                ForceCursor();
                OnTabShown();
            }
            else
            {
                CommitPending();
                Plugin.RequestSave();
                RestoreGameCursor();
            }
        }

        /// <summary>While the window is open the cursor must be visible and unlocked, whatever the game decides.</summary>
        public static void ForceCursor()
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private static void RestoreGameCursor()
        {
            try
            {
                // The game keeps its state in WindowsCursorController; setting it again re-applies it.
                ICursorController c = CursorService.Instance;
                c.Visible = c.Visible;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Failed to restore the game cursor: " + e.Message);
            }
        }

        private static void OnTabShown()
        {
            try
            {
                if (tab == DistanceTab)
                {
                    DistanceTweaks.ScanForWindow();
                }
                if (tab == FogTab)
                {
                    FogPresets.Reload(); // files may have been added or edited outside the game
                }
                if (tab == FogTab && fogWeather == null)
                {
                    fogWeather = WeatherTweaks.LayerSnapshot(WeatherTweaks.Controller, WeatherLayer.BaseLayer)?.Name ?? FogTweaks.KnownWeathers[0];
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("tabshown", "Window: " + e);
            }
        }

        // ------------------------------------------------------------ drawing

        public static void Draw()
        {
            if (!IsOpen)
            {
                return;
            }
            EnsureStyles();
            HandleKeyCapture();
            if (!IsOpen)
            {
                return;
            }

            float scale = Mathf.Max(1f, Screen.height / 1080f);
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

            window.width = BaseWidth;
            window.height = 10f; // shrink to the content of the current tab
            if (!positioned)
            {
                positioned = true;
                window.x = (Screen.width / scale - BaseWidth) / 2f;
                window.y = Screen.height / scale * 0.06f;
            }
            window = GUILayout.Window(WindowId, window, DrawWindow, $"{Plugin.Name} {Plugin.Version}", windowStyle,
                GUILayout.Width(BaseWidth));
            float maxX = Screen.width / scale - window.width;
            float maxY = Screen.height / scale - window.height;
            window.x = Mathf.Clamp(window.x, 0f, Mathf.Max(0f, maxX));
            window.y = Mathf.Clamp(window.y, 0f, Mathf.Max(0f, maxY));

            GUI.matrix = oldMatrix;

            if (GUIUtility.hotControl == 0)
            {
                CommitPending();
            }
        }

        private static void DrawWindow(int id)
        {
            SelectInput(); // an open drop-down list takes the clicks before the controls under it
            int newTab = GUILayout.SelectionGrid(tab, Tabs, 4, GUILayout.Height(52));
            if (newTab != tab)
            {
                CommitPending();
                CloseSelect();
                tab = newTab;
                scroll = Vector2.zero;
                OnTabShown();
            }
            GUILayout.Space(6);

            // A layout scroll view does not grow with its content, so it gets the content height measured in the previous
            // repaint (per tab), up to most of the screen; beyond that it scrolls.
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            float maxHeight = Screen.height / scale * 0.85f - 130f;
            GUIStyle area = GUI.skin.scrollView;
            float height = contentHeight[tab] > 0f
                ? Mathf.Min(contentHeight[tab] + area.padding.vertical + area.border.vertical + 12f, maxHeight)
                : maxHeight;
            // No horizontal scrollbar: rows are sized to ContentWidth.
            scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, area,
                GUILayout.Height(height));
            GUILayout.BeginVertical(GUILayout.Width(ContentWidth));
            try
            {
                switch (tab)
                {
                    case WeatherTab: DrawWeather(); break;
                    case SkyTab: DrawSky(); break;
                    case FogTab: DrawFog(); break;
                    case DistanceTab: DrawDistance(); break;
                    case LifeTab: DrawLife(); break;
                    case TimeTab: DrawTime(); break;
                    case PhotoTab: DrawPhoto(); break;
                    case MemoryTab: DrawMemory(); break;
                }
            }
            catch (Exception e)
            {
                GUILayout.Label("Error: " + e.Message, warnStyle);
                Plugin.LogOnce("tab" + tab, $"Window tab {Tabs[tab]}: {e}");
            }
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
            {
                contentHeight[tab] = GUILayoutUtility.GetLastRect().height;
            }
            GUILayout.EndScrollView();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Window key", GUILayout.Width(90));
            bool waiting = waitingFor != null;
            if (GUILayout.Button(waiting ? "press a key... (Esc: cancel)" : Plugin.MenuKey.Value.ToString(),
                    waiting ? selectedButton : GUI.skin.button, GUILayout.Width(200)))
            {
                waitingFor = waiting ? null : Plugin.MenuKey;
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label("config: pathologic2.worldtuner.cfg", footerStyle);
            if (GUILayout.Button("Close", GUILayout.Width(90)))
            {
                SetOpen(false);
            }
            GUILayout.EndHorizontal();

            SelectDraw();
            GUI.DragWindow(new Rect(0, 0, 10000, 30));
        }

        // ------------------------------------------------------------ Weather

        private static void DrawWeather()
        {
            WeatherController wc = WeatherTweaks.Controller;
            List<IWeatherSnapshot> snaps = WeatherTweaks.Snapshots;
            if (wc == null || snaps.Count == 0)
            {
                GUILayout.Label("Weather is not available yet (loading).", warnStyle);
                return;
            }

            IWeatherSnapshot baseSnap = WeatherTweaks.LayerSnapshot(wc, WeatherLayer.BaseLayer);
            GUILayout.Label("Switch the weather", headerStyle);
            ButtonGrid(snaps, 4, s => baseSnap != null && s.Id == baseSnap.Id, WeatherTweaks.Apply);

            GUILayout.Space(6);
            FloatRow("Blend time", WeatherTweaks.BlendSeconds, 0f, 120f, 1f, Mode.Plain, 5f, v => v < 0.5f ? "instant" : $"{v:0} s");
            GUILayout.Label("Works like the console command change_weather: the weather goes to the base layer. " +
                            "The game's scripts change the weather on their own schedule and will replace it later.", hintStyle);

            GUILayout.Space(8);
            GUILayout.Label("Weather layers (blended in this order)", headerStyle);
            foreach (WeatherLayer layer in WeatherTweaks.Layers)
            {
                IWeatherLayerBlenderItem item = wc.GetItem(layer);
                SmoothBlender<IWeatherSnapshot> sb = WeatherTweaks.LayerBlender(wc, layer);
                IWeatherSnapshot snap = sb == null ? null : WeatherTweaks.FindSnapshot(sb.SnapshotTemplateId);
                string text = $"{WeatherTweaks.LayerName(layer)}:  weight {(item?.Opacity ?? 0f) * 100f:0}%";
                text += snap != null ? $",  {snap.Name}" : ",  -";
                if (sb != null && sb.Target != null)
                {
                    text += $"  (blending {sb.Progress * 100f:0}%)";
                }
                GUILayout.Label(text, item != null && item.Opacity > 0.001f ? valueStyle : hintStyle);
                if (layer != WeatherLayer.BaseLayer && item != null && item.Opacity > 0.5f)
                {
                    GUILayout.Label($"   The {WeatherTreeName(layer)} layer covers the base layer now: a weather picked here may be hidden by it.", warnStyle);
                }
            }
            DrawWeatherExtras();
        }

        private static string WeatherTreeName(WeatherLayer layer) => WeatherTweaks.LayerName(layer).ToLowerInvariant();

        private static float actualSpeed = 1f;

        /// <summary>Set speed and, when Unity's per-frame cap (Time.maximumDeltaTime) holds it back, the measured one.</summary>
        private static string SpeedText(float speed)
        {
            if (Event.current.type == EventType.Repaint && Time.unscaledDeltaTime > 0f)
            {
                actualSpeed = Mathf.Lerp(actualSpeed, Time.deltaTime / Time.unscaledDeltaTime, 0.05f);
            }
            return speed > 1f && actualSpeed < speed * 0.95f ? $"  x{speed:0.##} (x{actualSpeed:0.0})" : $"  x{speed:0.##}";
        }

        // ------------------------------------------------------------ Fog

        private static string presetName = "My fog";
        private static string presetDeleteArmed;

        private static string[] presetNames;
        private static int presetNamesVersion = -1;
        private static readonly Action<int> PickPreset = i =>
        {
            if (i >= FogPresets.All.Count)
            {
                return;
            }
            FogPresets.Preset p = FogPresets.All[i];
            CommitPending();
            FogPresets.Apply(p);
            FogPresets.Matching(Time.unscaledTime, true);
            if (!p.BuiltIn)
            {
                presetName = p.Name;
            }
            presetDeleteArmed = null;
        };

        private static void DrawFogPresets()
        {
            FogPresets.EnsureLoaded();
            string current = FogPresets.Matching(Time.unscaledTime, false);
            GUILayout.Label("Presets", headerStyle);

            // A drop-down like an HTML select: it shows the preset the settings equal now.
            GUILayout.BeginHorizontal();
            List<FogPresets.Preset> all = FogPresets.All;
            if (presetNames == null || presetNamesVersion != FogPresets.Version)
            {
                presetNamesVersion = FogPresets.Version;
                presetNames = new string[all.Count];
                for (int i = 0; i < all.Count; i++)
                {
                    presetNames[i] = all[i].BuiltIn ? all[i].Name : all[i].Name + "  (own)";
                }
            }
            int currentIndex = -1;
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Name, current, StringComparison.OrdinalIgnoreCase))
                {
                    currentIndex = i;
                }
            }
            Select("fogpreset", current ?? "(own settings)", presetNames, currentIndex, PickPreset, 220);
            GUILayout.Space(12);
            GUILayout.Label("Name", GUILayout.Width(40));
            presetName = GUILayout.TextField(presetName ?? "", 40, GUILayout.Width(220));
            FogPresets.Preset existing = FogPresets.Find(presetName ?? "");
            bool valid = FogPresets.IsValidName(presetName);
            bool oldEnabled = GUI.enabled;
            GUI.enabled = valid;
            if (GUILayout.Button(existing != null && !existing.BuiltIn ? "Overwrite" : "Save current", GUILayout.Width(130)))
            {
                CommitPending();
                FogPresets.Save(presetName);
                FogPresets.Matching(Time.unscaledTime, true);
                presetDeleteArmed = null;
            }
            GUI.enabled = existing != null && !existing.BuiltIn;
            bool armed = existing != null && string.Equals(presetDeleteArmed, existing.Name, StringComparison.OrdinalIgnoreCase);
            if (GUILayout.Button(armed ? "Sure? Delete" : "Delete", armed ? selectedButton : GUI.skin.button, GUILayout.Width(110)))
            {
                if (armed)
                {
                    FogPresets.Delete(existing);
                    presetDeleteArmed = null;
                    FogPresets.Matching(Time.unscaledTime, true);
                }
                else
                {
                    presetDeleteArmed = existing.Name;
                }
            }
            GUI.enabled = oldEnabled;
            GUILayout.EndHorizontal();
            GUILayout.Label("A preset holds every fog setting on this tab (mode, global and per-weather values, volumetric light, " +
                            "fog shader). The drop-down shows the preset that equals the current settings. Built-in presets cannot be " +
                            "overwritten. Own presets are files in BepInEx/config/pathologic2-world-tuner-fog-presets/ and can be shared.",
                valid || string.IsNullOrWhiteSpace(presetName) ? hintStyle : warnStyle);
            GUILayout.Space(8);
        }

        private static void DrawFog()
        {
            DrawFogPresets();
            GUILayout.Label("Fog mode", headerStyle);
            int mode = (int)FogTweaks.Mode.Value;
            int newMode = GUILayout.Toolbar(mode, FogModes);
            if (newMode != mode)
            {
                FogTweaks.Mode.Value = (FogMode)newMode;
                Plugin.RequestSave();
            }
            GUILayout.Label(FogTweaks.Mode.Value == FogMode.Off
                ? "The fog is as in the game. Choose Global (one setting for every weather) or Per weather (own setting for each)."
                : FogTweaks.Mode.Value == FogMode.Global
                    ? "One setting applied on top of every weather's own fog."
                    : "Each weather has its own setting. The weather on the base layer now is highlighted.", hintStyle);
            GUILayout.Space(6);

            FogProfile profile = FogTweaks.Global;
            IWeatherSnapshot selected = null;
            if (FogTweaks.Mode.Value == FogMode.PerWeather)
            {
                List<IWeatherSnapshot> snaps = WeatherTweaks.Snapshots;
                if (fogWeather == null)
                {
                    fogWeather = FogTweaks.KnownWeathers[0];
                }
                IWeatherSnapshot baseSnap = WeatherTweaks.LayerSnapshot(WeatherTweaks.Controller, WeatherLayer.BaseLayer);
                if (snaps.Count > 0)
                {
                    GUILayout.Label("Weather to edit", headerStyle);
                    ButtonGrid(snaps, 4, s => string.Equals(s.Name, fogWeather, StringComparison.OrdinalIgnoreCase),
                        s => { CommitPending(); fogWeather = s.Name; },
                        s => (baseSnap != null && s.Id == baseSnap.Id ? "> " : "") + s.Name +
                             (FogTweaks.ProfileOf(s.Name).IsDefault ? "" : " *"));
                    foreach (IWeatherSnapshot s in snaps)
                    {
                        if (string.Equals(s.Name, fogWeather, StringComparison.OrdinalIgnoreCase))
                        {
                            selected = s;
                        }
                    }
                    GUILayout.Label("> = current weather, * = changed", hintStyle);
                }
                profile = FogTweaks.ProfileOf(fogWeather);
                GUILayout.Space(4);
            }

            bool off = FogTweaks.Mode.Value == FogMode.Off;
            bool oldEnabled = GUI.enabled;
            GUI.enabled = !off;
            float gameDensity = 0.025f, gameStart = 0f, gameHeight = 60f;
            if (selected != null)
            {
                FogTweaks.TryGetOriginal(selected, out gameDensity, out gameStart, out gameHeight);
            }
            GUILayout.Label(FogTweaks.Mode.Value == FogMode.PerWeather ? $"Fog for {profile.Name}" : "Fog for all weathers", headerStyle);
            FloatRow("Density", profile.DensityScale, 0f, 3f, 0.05f, Mode.Scale, 1f,
                v => selected != null ? $"x{v:0.00} = {gameDensity * v:0.0000}" : $"x{v:0.00}");
            FloatRow("Start distance", profile.StartOffset, 0f, 200f, 1f, Mode.Plain, 0f, v => $"+{v:0} m");
            FloatRow("Height fog level", profile.Height, 0f, 300f, 1f, Mode.Absolute, gameHeight, v => $"{v:0} m");
            FloatRow("Sky haze (fogginess)", profile.FogginessScale, 0f, 3f, 0.05f, Mode.Scale, 1f, v => $"x{v:0.00}");
            GUILayout.Label("Density scales the whole fog. Start distance: no fog closer than this, the far view stays foggy. " +
                            "Height: the level up to which the height fog lies. Sky haze: the hazy horizon and sky colour.", hintStyle);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset this", GUILayout.Width(130)))
            {
                pending.Clear();
                profile.Reset();
                Plugin.RequestSave();
            }
            if (FogTweaks.Mode.Value == FogMode.PerWeather)
            {
                if (GUILayout.Button("Copy to all weathers", GUILayout.Width(170)))
                {
                    CommitPending();
                    foreach (string name in AllWeatherNames())
                    {
                        FogProfile p = FogTweaks.ProfileOf(name);
                        if (p != profile)
                        {
                            p.CopyFrom(profile);
                        }
                    }
                    Plugin.RequestSave();
                }
                if (GUILayout.Button("Take from Global", GUILayout.Width(150)))
                {
                    pending.Clear();
                    profile.CopyFrom(FogTweaks.Global);
                    Plugin.RequestSave();
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            bool keep = GUILayout.Toggle(FogTweaks.KeepVolumetricLight.Value,
                " Keep volumetric light as in the game (the game scales light shafts with the fog density)");
            if (keep != FogTweaks.KeepVolumetricLight.Value)
            {
                FogTweaks.KeepVolumetricLight.Value = keep;
                Plugin.RequestSave();
            }
            GUI.enabled = oldEnabled;

            GUILayout.Space(8);
            GUILayout.Label("Fog shader (camera, for every weather)", headerStyle);
            FloatRow("Distance fog multiplier", FogTweaks.DistanceDensity, 0f, 5f, 0.05f, Mode.Absolute, FogTweaks.GameDistanceDensity,
                v => $"{v:0.00}", FogTweaks.ApplyCameraFogNow);
            FloatRow("Height fog falloff", FogTweaks.HeightDensity, 0f, 10f, 0.05f, Mode.Absolute, FogTweaks.GameHeightDensity,
                v => $"{v:0.00}", FogTweaks.ApplyCameraFogNow);
            GUILayout.BeginHorizontal();
            BoolToggle(FogTweaks.DisableDistanceFog, " No distance fog");
            BoolToggle(FogTweaks.DisableHeightFog, " No height fog");
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GlobalFog fog = FogTweaks.CameraFog;
            GUILayout.Label(fog != null
                ? $"Now in the engine: density {RenderSettings.fogDensity:0.0000}, start {fog.startDistance:0.#} m, height {fog.height:0.#} m"
                : "The game camera's fog is not found (main menu or loading).", valueStyle);
        }

        private static IEnumerable<string> AllWeatherNames()
        {
            var names = new HashSet<string>(FogTweaks.KnownWeathers, StringComparer.OrdinalIgnoreCase);
            foreach (IWeatherSnapshot s in WeatherTweaks.Snapshots)
            {
                names.Add(s.Name);
            }
            return names;
        }

        // ------------------------------------------------------------ Distance

        private static void DrawDistance()
        {
            DistanceTweaks.CameraInfo(); // the game camera values for the "Game" labels (only while the window shows them)
            GUILayout.BeginHorizontal();
            GUILayout.Label("Presets", headerStyle, GUILayout.Width(70));
            if (GUILayout.Button("Game")) { pending.Clear(); DistanceTweaks.ApplyPreset(0); Plugin.RequestSave(); }
            if (GUILayout.Button("Far")) { pending.Clear(); DistanceTweaks.ApplyPreset(1); Plugin.RequestSave(); }
            if (GUILayout.Button("Very far")) { pending.Clear(); DistanceTweaks.ApplyPreset(2); Plugin.RequestSave(); }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            // --- rendering
            GUILayout.Label("Rendering: how far things are drawn", headerStyle);
            float far = DistanceTweaks.FarClip.Value > 0f ? DistanceTweaks.FarClip.Value : DistanceTweaks.GameFar;
            FloatRow("Objects, trees, people", DistanceTweaks.Objects, 50f, 6000f, 10f, Mode.AbsoluteLog, DistanceTweaks.GameDefaultCull, Meters);
            bool same = DistanceTweaks.DecalsSameAsObjects.Value;
            bool oldEnabled = GUI.enabled;
            GUI.enabled = !same;
            FloatRow("Infection decals", DistanceTweaks.Decals, 50f, 6000f, 10f, Mode.AbsoluteLog,
                DistanceTweaks.GameLayerCull(DistanceTweaks.LayerUnimportant), Meters);
            GUI.enabled = oldEnabled;
            BoolToggle(DistanceTweaks.DecalsSameAsObjects, " Infection decals: same as objects");
            FloatRow("Interiors", DistanceTweaks.Interiors, 10f, 1000f, 5f, Mode.AbsoluteLog,
                DistanceTweaks.GameLayerCull(DistanceTweaks.LayerIndoor), Meters);
            FloatRow("Key buildings, proxies", DistanceTweaks.KeyBuildings, 500f, 6000f, 50f, Mode.AbsoluteLog,
                DistanceTweaks.GameLayerCull(DistanceTweaks.LayerKeyBuilding), Meters);
            FloatRow("Terrain", DistanceTweaks.TerrainLayer, 500f, 6000f, 50f, Mode.AbsoluteLog,
                DistanceTweaks.GameLayerCull(DistanceTweaks.LayerTerrain), Meters);
            FloatRow("Camera far clip", DistanceTweaks.FarClip, 500f, 6000f, 50f, Mode.AbsoluteLog, DistanceTweaks.GameFar, Meters);
            GUILayout.Label($"Nothing is drawn beyond the far clip ({far:0} m). Distant houses are low-poly proxies until the real " +
                            "house is loaded (next section); fog hides the far edge.", hintStyle);
            GUILayout.Space(8);

            // --- streaming
            GUILayout.Label("World streaming: where real houses are loaded", headerStyle);
            StreamingRow("Houses", DistanceTweaks.Houses, 6f, LoaderKind.Houses);
            StreamingRow("Landmark buildings", DistanceTweaks.Landmarks, 6f, LoaderKind.Landmarks);
            StreamingRow("Districts", DistanceTweaks.Districts, 4f, LoaderKind.Districts);
            FloatRow("Load while indoors", DistanceTweaks.IndoorLoad, 0.5f, 3f, 0.05f, Mode.Scale, 1f, v => $"x{v:0.00}", null, true);
            float maxScale = Mathf.Max(DistanceTweaks.Houses.Value, DistanceTweaks.Landmarks.Value);
            GUILayout.Label("Applied when the slider is released. Higher values load more houses at once: more CPU and memory, " +
                            "hitches while walking. Houses x2-3 is a reasonable start.", maxScale > 3.01f ? warnStyle : hintStyle);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Rescan scene", GUILayout.Width(130)))
            {
                DistanceTweaks.Rescan();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            // --- detail
            GUILayout.Label("Detail", headerStyle);
            FloatRow("LOD bias", DistanceTweaks.LodBias, 0.25f, 8f, 0.05f, Mode.Absolute, DistanceTweaks.GameLodBias, v => $"{v:0.00}");
            FloatRow("Shadow distance", DistanceTweaks.ShadowDistance, 5f, 500f, 1f, Mode.AbsoluteLog, DistanceTweaks.GameShadowDistance, Meters);
            if (DistanceTweaks.NgssOverride)
            {
                GUILayout.Label("NGSS global shadow override is on: the shadow distance is set through NGSS.", hintStyle);
            }
            float grass = DistanceTweaks.GameGrassDistance;
            FloatRow("Grass", DistanceTweaks.GrassScale, 0.5f, 6f, 0.1f, Mode.Scale, 1f,
                v => grass > 0f ? $"x{v:0.0} = {grass * v:0} m" : $"x{v:0.0}");
            FloatRow("Sharp ground texture", DistanceTweaks.GroundTexture, 10f, 2000f, 5f, Mode.AbsoluteLog, DistanceTweaks.GameGroundTexture, Meters);
            FloatRow("Terrain mesh error", DistanceTweaks.TerrainError, 1f, 50f, 1f, Mode.Absolute, DistanceTweaks.GameTerrainError,
                v => $"{v:0} px");
            FloatRow("Reflection distance", DistanceTweaks.ReflectionCulling, 0.1f, 1f, 0.05f, Mode.Absolute, DistanceTweaks.GameReflectionCulling,
                v => $"x{v:0.00}");
            GUILayout.Label("LOD bias: detailed models farther away. Terrain mesh error: lower is more detailed. " +
                            "Reflection distance: fraction of the draw distances used by puddle/water reflections.", hintStyle);
        }

        private static void StreamingRow(string label, ConfigEntry<float> entry, float max, LoaderKind kind)
        {
            DistanceTweaks.LoaderStats s = DistanceTweaks.Stats(kind);
            FloatRow(label, entry, 0.5f, max, 0.05f, Mode.Scale, 1f, v => $"x{v:0.00}", null, true);
            string range = s.Total > 0 ? $", game {s.MinLoad:0}-{s.MaxLoad:0} m -> {s.MinLoad * entry.Value:0}-{s.MaxLoad * entry.Value:0} m" : "";
            GUILayout.Label($"      {s.Total} in the scene, {s.Loaded} loaded now{range}", hintStyle);
        }

        private static string Meters(float v) => $"{v:0} m";

        // ------------------------------------------------------------ Time

        private static bool draggingHour;

        private static void DrawTime()
        {
            TimeService ts = TimeTweaks.Service;
            if (ts == null)
            {
                GUILayout.Label("Time service is not available yet.", warnStyle);
                return;
            }
            TimeSpan solar = ts.SolarTime;
            GUILayout.Label("Time of day (sky, sun, lamps, day/night street crowds)", headerStyle);
            GUILayout.Label($"Solar time: day {(int)solar.TotalDays}, {solar.Hours:00}:{solar.Minutes:00}    " +
                            $"speed x{ts.SolarTimeFactor:0.#}    game time: day {(int)ts.GameTime.TotalDays}, {ts.GameTime.Hours:00}:{ts.GameTime.Minutes:00}",
                valueStyle);

            float hour = (float)(solar.TotalHours - Math.Floor(solar.TotalDays) * 24.0);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Hour", GUILayout.Width(LabelWidth));
            float newHour = GUILayout.HorizontalSlider(hour, 0f, 23.99f, GUILayout.Width(SliderWidth + ValueWidth));
            GUILayout.EndHorizontal();
            if (Math.Abs(newHour - hour) > 0.01f)
            {
                draggingHour = true;
                TimeTweaks.SetHour(Mathf.Round(newHour * 12f) / 12f); // 5-minute steps
            }
            if (draggingHour && GUIUtility.hotControl == 0)
            {
                draggingHour = false;
            }

            GUILayout.BeginHorizontal();
            foreach (int h in new[] { 0, 3, 6, 9, 12, 15, 18, 21 })
            {
                if (GUILayout.Button($"{h:00}:00"))
                {
                    TimeTweaks.SetHour(h);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            bool frozen = GUILayout.Toggle(TimeTweaks.Frozen, " Stop the clock (solar time only; quests keep running)");
            if (frozen != TimeTweaks.Frozen)
            {
                TimeTweaks.SetFrozen(frozen);
            }

            TimeSpan offset = TimeTweaks.Offset;
            GUILayout.BeginHorizontal();
            bool oldEnabled = GUI.enabled;
            GUI.enabled = offset != TimeSpan.Zero || TimeTweaks.Frozen;
            if (GUILayout.Button("Back to game time", GUILayout.Width(170)))
            {
                TimeTweaks.BackToGameTime();
            }
            GUI.enabled = oldEnabled;
            if (offset != TimeSpan.Zero)
            {
                GUILayout.Label($"  shifted by {(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.TotalHours):0.0} h", valueStyle);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("The shift is not saved: saves keep the game's solar time and speed. Sleeping or a script may reset " +
                            "the time on their own. Street crowds switch between day and night sets when the time crosses dusk or dawn.", hintStyle);

            // --- world pause and game speed
            GUILayout.Space(10);
            GUILayout.Label("Stop the world", headerStyle);
            GUILayout.BeginHorizontal();
            bool frozenNpcs = NpcFreeze.Active;
            if (GUILayout.Button(frozenNpcs ? "Resume NPCs" : "Freeze NPCs", frozenNpcs ? selectedButton : GUI.skin.button, GUILayout.Width(170)))
            {
                NpcFreeze.Set(!frozenNpcs);
            }
            GUILayout.Label(frozenNpcs
                ? $"  {NpcFreeze.Count} NPCs frozen. NPCs that appear later are not frozen (press again to include them)."
                : "  Stops NPC AI, walking and animations; the player keeps moving. Clocks keep running.", hintStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            float speed = TimeTweaks.Speed;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Game speed", GUILayout.Width(LabelWidth));
            // Two-part slider: the left half is x0..x1 (steps of 0.05), the right half x1..x10 (steps of 0.25).
            float pos = speed <= 1f ? speed : 1f + (speed - 1f) / (TimeTweaks.MaxSpeed - 1f);
            float newPos = GUILayout.HorizontalSlider(pos, 0f, 2f, GUILayout.Width(SliderWidth));
            GUILayout.Label(speed <= 0f ? "  0: everything stopped" : SpeedText(speed), valueStyle, GUILayout.Width(ValueWidth));
            GUILayout.EndHorizontal();
            if (Math.Abs(newPos - pos) > 0.001f)
            {
                float s = newPos <= 1f
                    ? Mathf.Round(newPos * 20f) / 20f
                    : Mathf.Round((1f + (newPos - 1f) * (TimeTweaks.MaxSpeed - 1f)) * 4f) / 4f;
                TimeTweaks.SetSpeed(s);
            }
            GUILayout.BeginHorizontal();
            foreach (float s in new[] { 0f, 0.1f, 0.25f, 0.5f, 1f, 2f, 4f, 10f })
            {
                bool on = Math.Abs(speed - s) < 0.001f;
                string label = s == 0f ? "Freeze all" : s == 1f ? "Normal" : $"x{s:0.##}";
                if (GUILayout.Button(label, on ? selectedButton : GUI.skin.button))
                {
                    TimeTweaks.SetSpeed(s);
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Game speed scales everything that runs on game time: NPCs, the player, physics, particles, rain. " +
                            "At 0 the whole picture stops (the camera may still turn). Above x2 physics runs several steps per " +
                            "frame; at a low frame rate Unity caps the game time per frame, so the actual speed (shown in " +
                            "brackets) stays lower. Both reset when a save is loaded; the speed is not saved.", hintStyle);

            DrawPhysicsRate();

            // --- player movement
            GUILayout.Space(10);
            GUILayout.Label("Player movement speed", headerStyle);
            GUILayout.BeginHorizontal();
            foreach (float k in new[] { 1.5f, 2f, 3f })
            {
                if (GUILayout.Button($"All x{k:0.#}"))
                {
                    PlayerSpeed.ScaleAll(k);
                    Plugin.RequestSave();
                }
            }
            if (GUILayout.Button("All game"))
            {
                pending.Clear();
                PlayerSpeed.ResetAll();
                Plugin.RequestSave();
            }
            GUILayout.EndHorizontal();
            FloatRow("Walk", PlayerSpeed.Walk, 0.2f, 30f, 0.05f, Mode.AbsoluteLog, PlayerSpeed.Game(0, 2f), MetersPerSecond);
            FloatRow("Walk backwards", PlayerSpeed.WalkBack, 0.2f, 30f, 0.05f, Mode.AbsoluteLog, PlayerSpeed.Game(1, 1.5f), MetersPerSecond);
            FloatRow("Run", PlayerSpeed.Run, 0.2f, 30f, 0.05f, Mode.AbsoluteLog, PlayerSpeed.Game(2, 5f), MetersPerSecond);
            FloatRow("Run backwards", PlayerSpeed.RunBack, 0.2f, 30f, 0.05f, Mode.AbsoluteLog, PlayerSpeed.Game(3, 3f), MetersPerSecond);
            FloatRow("Sneak", PlayerSpeed.Sneak, 0.2f, 30f, 0.05f, Mode.AbsoluteLog, PlayerSpeed.Game(4, 1f), MetersPerSecond);
            FloatRow("With a weapon out", PlayerSpeed.Fight, 0.25f, 4f, 0.05f, Mode.Scale, 1f, v => $"x{v:0.00}");
            FloatRow("Jump", PlayerSpeed.Jump, 0.5f, 30f, 0.05f, Mode.AbsoluteLog, PlayerSpeed.Game(5, 5f), MetersPerSecond);
            FloatRow("Gravity", PlayerSpeed.Gravity, 0.1f, 10f, 0.05f, Mode.AbsoluteLog, PlayerSpeed.Game(6, 1f), v => $"x{v:0.00}");
            FloatRow("Run stop smoothing", RunStopSmoothing.Seconds, 0f, 1f, 0.05f, Mode.Plain, 0f, v => v <= 0f ? "off" : $"{v:0.00} s");
            FloatRow("Landing camera push", LandingPush.Scale, 0f, 2f, 0.05f, Mode.Scale, 1f, v => v <= 0f ? "none" : $"x{v:0.00}");
            GUILayout.Label("Saved and applied on every start. Running is also scaled by the game's own run modifier (tiredness and the like). " +
                            "\"With a weapon out\" scales all nine combat speeds (forward, backward, strafe; normal, sneaking, running). " +
                            "Run stop smoothing: when running stops (Shift released or stamina run out) the game drops to walking speed " +
                            "at once and the view jerks; this slows down over the set time instead. Landing camera push: the short " +
                            "field-of-view kick the game plays on every landing after a jump (the landing sound stays).", hintStyle);
        }

        private static string MetersPerSecond(float v) => $"{v:0.00} m/s";

        private static void DrawPhysicsRate()
        {
            GUILayout.Space(10);
            GUILayout.Label("Physics rate", headerStyle);
            FloatRow("Physics steps per second", PhysicsRate.Rate, PhysicsRate.SliderMinHz, PhysicsRate.SliderMaxHz, 1f, Mode.AbsoluteLog,
                PhysicsRate.GameHz, v => $"{v:0.#} Hz ({1000f / v:0.0} ms)");

            GUILayout.BeginHorizontal();
            foreach (float hz in new[] { 20f, 45f, 60f, 90f, 120f, 144f, 240f })
            {
                bool on = PhysicsRate.Active && Math.Abs(PhysicsRate.Rate.Value - hz) < 0.01f;
                if (GUILayout.Button($"{hz:0}", on ? selectedButton : GUI.skin.button))
                {
                    PhysicsRate.Rate.Value = hz;
                    Plugin.RequestSave();
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label("Exact:");
            if (physicsText == null)
            {
                physicsText = PhysicsRate.Active ? PhysicsRate.Rate.Value.ToString("0.#") : "";
            }
            physicsText = GUILayout.TextField(physicsText, 6, GUILayout.Width(60));
            if (GUILayout.Button("Set", GUILayout.Width(ButtonWidth)) &&
                float.TryParse(physicsText.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float typed))
            {
                PhysicsRate.Rate.Value = Mathf.Clamp(typed, PhysicsRate.MinHz, PhysicsRate.MaxHz);
                physicsText = PhysicsRate.Rate.Value.ToString("0.#");
                Plugin.RequestSave();
            }
            GUILayout.EndHorizontal();

            if (PhysicsMeter.HasData)
            {
                GUILayout.Label($"Now: {PhysicsRate.CurrentHz:0.#} Hz, {PhysicsMeter.AvgSteps:0.0} physics steps per frame, the physics part " +
                                $"of the frame {PhysicsMeter.AvgMs:0.0} ms on average, {PhysicsMeter.MaxMs:0.0} ms at most (approximate).", hintStyle);
            }
            GUILayout.Label("Unity's fixed timestep (30 Hz in the game). Higher rates make the player's movement, blocking, combat camera " +
                            "kicks and walking NPCs step more finely; speeds and timings stay the same. The cost per second grows with the " +
                            "rate, and game speed multiplies the steps per frame. Ragdolls and combat were tuned for 30 Hz. Saved and applied " +
                            "on every start; with Game selected the mod does not touch the value at all.", hintStyle);
        }

        // ------------------------------------------------------------ controls

        private enum Mode
        {
            Plain,       // the value itself, the default is shown as "game"
            Scale,       // multiplier, 1 = game
            Absolute,    // -1 = game value, linear slider
            AbsoluteLog  // -1 = game value, logarithmic slider (distances)
        }

        /// <summary>
        /// Label, slider, value, "Game" button. For Absolute modes -1 in the config means "as in the game" and the slider shows
        /// <paramref name="game"/>. With <paramref name="deferred"/> the value is applied on mouse release only.
        /// </summary>
        private static void FloatRow(string label, ConfigEntry<float> entry, float min, float max, float step, Mode mode, float game,
            Func<float, string> format, Action applied = null, bool deferred = false)
        {
            bool absolute = mode == Mode.Absolute || mode == Mode.AbsoluteLog;
            bool log = mode == Mode.AbsoluteLog && min > 0f;
            bool isGame = absolute ? entry.Value < 0f : Math.Abs(entry.Value - game) < 0.0001f;
            float value = pending.TryGetValue(entry, out float p) ? p : (absolute && entry.Value < 0f ? game : entry.Value);
            if (pending.ContainsKey(entry))
            {
                isGame = false;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            float t = log ? Mathf.InverseLerp(Mathf.Log(min), Mathf.Log(max), Mathf.Log(Mathf.Clamp(value, min, max)))
                          : Mathf.InverseLerp(min, max, value);
            float newT = GUILayout.HorizontalSlider(t, 0f, 1f, GUILayout.Width(SliderWidth));
            GUILayout.Label("  " + format(value) + (isGame ? " (game)" : ""), valueStyle, GUILayout.Width(ValueWidth));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Game", isGame ? selectedButton : GUI.skin.button, GUILayout.Width(ButtonWidth)) && !isGame)
            {
                pending.Remove(entry);
                entry.Value = absolute ? -1f : game;
                applied?.Invoke();
                Plugin.RequestSave();
            }
            GUILayout.EndHorizontal();

            if (Math.Abs(newT - t) > 0.0001f)
            {
                float v = log ? Mathf.Exp(Mathf.Lerp(Mathf.Log(min), Mathf.Log(max), newT)) : Mathf.Lerp(min, max, newT);
                v = Mathf.Clamp(Mathf.Round(v / step) * step, min, max);
                if (log && v >= 100f)
                {
                    v = Mathf.Round(v / 10f) * 10f; // round distances to 10 m
                }
                if (deferred)
                {
                    pending[entry] = v;
                }
                else if (Math.Abs(entry.Value - v) > 0.00001f)
                {
                    entry.Value = v;
                    applied?.Invoke();
                    Plugin.RequestSave();
                }
            }
        }

        private static void CommitPending()
        {
            if (pending.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<ConfigEntryBase, float> pair in pending)
            {
                if (pair.Key is ConfigEntry<float> e && Math.Abs(e.Value - pair.Value) > 0.00001f)
                {
                    e.Value = pair.Value;
                }
            }
            pending.Clear();
            Plugin.RequestSave();
        }

        private static void BoolToggle(ConfigEntry<bool> entry, string label)
        {
            bool v = GUILayout.Toggle(entry.Value, label);
            if (v != entry.Value)
            {
                entry.Value = v;
                FogTweaks.ApplyCameraFogNow();
                Plugin.RequestSave();
            }
        }

        private static void ButtonGrid(List<IWeatherSnapshot> items, int perRow, Func<IWeatherSnapshot, bool> selected,
            Action<IWeatherSnapshot> click, Func<IWeatherSnapshot, string> text = null)
        {
            for (int i = 0; i < items.Count; i += perRow)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Math.Min(items.Count, i + perRow); j++)
                {
                    IWeatherSnapshot s = items[j];
                    if (GUILayout.Button(text != null ? text(s) : s.Name, selected(s) ? selectedButton : GUI.skin.button,
                            GUILayout.Width((ContentWidth - perRow * 8f) / perRow)))
                    {
                        click(s);
                    }
                }
                GUILayout.EndHorizontal();
            }
        }

        private static void HandleKeyCapture()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown || e.keyCode == KeyCode.None)
            {
                return;
            }
            if (waitingFor == null)
            {
                // While the window is open, Input.ResetInputAxes() may swallow GetKeyDown in Update, but not IMGUI events.
                // Skip the frame in which Update has just opened/closed the window.
                if (e.keyCode == KeyCode.Escape && (SelectOpen || selectClosedFrame == Time.frameCount))
                {
                    CloseSelect(); // Esc closes an open drop-down list first, not the window
                    e.Use();
                    return;
                }
                if (e.keyCode == KeyCode.Escape || (Plugin.Matches(Plugin.MenuKey.Value, e) && lastChangeFrame != Time.frameCount))
                {
                    e.Use();
                    SetOpen(false);
                }
                return;
            }
            if (e.keyCode == KeyCode.Escape)
            {
                waitingFor = null;
                e.Use();
                return;
            }
            if (IsModifier(e.keyCode))
            {
                return; // wait for the main key
            }
            var modifiers = new List<KeyCode>();
            if (e.control) modifiers.Add(KeyCode.LeftControl);
            if (e.shift) modifiers.Add(KeyCode.LeftShift);
            if (e.alt) modifiers.Add(KeyCode.LeftAlt);
            waitingFor.Value = new KeyboardShortcut(e.keyCode, modifiers.ToArray());
            Plugin.Log.LogInfo($"{waitingFor.Definition.Key} = {waitingFor.Value}");
            waitingFor = null;
            lastChangeFrame = Time.frameCount; // the same key must not close the window right away
            e.Use();
            Plugin.RequestSave();
        }

        private static bool IsModifier(KeyCode k)
        {
            return k == KeyCode.LeftControl || k == KeyCode.RightControl || k == KeyCode.LeftShift || k == KeyCode.RightShift ||
                   k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.LeftCommand || k == KeyCode.RightCommand ||
                   k == KeyCode.AltGr;
        }

        // ------------------------------------------------------------ styles

        private static void EnsureStyles()
        {
            if (windowStyle != null)
            {
                return;
            }
            windowBackground = MakeTexture(new Color(0.08f, 0.08f, 0.09f, 0.96f));
            selectedBackground = MakeTexture(new Color(0.55f, 0.12f, 0.10f, 1f));

            windowStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(14, 14, 30, 12) };
            windowStyle.normal.background = windowBackground;
            windowStyle.onNormal.background = windowBackground;
            windowStyle.normal.textColor = Color.white;
            windowStyle.onNormal.textColor = Color.white;

            headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            headerStyle.normal.textColor = new Color(0.95f, 0.85f, 0.70f);

            statusStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            statusStyle.normal.textColor = new Color(0.70f, 0.90f, 0.70f);

            valueStyle = new GUIStyle(GUI.skin.label);
            valueStyle.normal.textColor = new Color(0.80f, 0.92f, 0.80f);

            hintStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = Mathf.Max(10, GUI.skin.label.fontSize - 1) };
            hintStyle.normal.textColor = new Color(0.65f, 0.65f, 0.65f);

            footerStyle = new GUIStyle(hintStyle) { wordWrap = false };

            warnStyle = new GUIStyle(hintStyle);
            warnStyle.normal.textColor = new Color(1f, 0.72f, 0.45f);

            selectedButton = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
            selectedButton.normal.background = selectedBackground;
            selectedButton.hover.background = selectedBackground;
            selectedButton.active.background = selectedBackground;
            selectedButton.normal.textColor = Color.white;
            selectedButton.hover.textColor = Color.white;
        }

        private static Texture2D MakeTexture(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
