using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// In-game window (F6) to switch and edit the weather and sky, tune the fog (globally or per weather), the draw distance,
    /// crowds, lamps and the lantern, the time of day, the free camera, the image and memory. Everything is changed in memory at runtime; no game files are touched.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [DefaultExecutionOrder(-32000)] // before the game's scripts, to block input while the window is open
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "pathologic2.worldtuner";
        public const string Name = "Pathologic 2 World Tuner";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigFile Cfg;
        internal static ConfigEntry<KeyboardShortcut> MenuKey;
        internal static ConfigEntry<bool> BlockInput;
        internal static ConfigEntry<bool> MenuOpenOnStart;

        private static Plugin instance;
        private static bool saveRequested;
        private float nextTick;
        private GuiHost gui;
        private static Harmony cursorHarmony;

        // Cached delegates: a method group passed as Action allocates a new delegate on every call.
        private static readonly Action FogTick = FogTweaks.Tick, DistanceTick = DistanceTweaks.Tick, TimeTick = TimeTweaks.Tick,
            SpeedTick = PlayerSpeed.Tick, DistanceFrame = DistanceTweaks.FrameTick, TimeFrame = TimeTweaks.FrameTick,
            FreezeFrame = NpcFreeze.Tick, FreeCameraTick = FreeCamera.Tick, MemoryTick = MemoryTweaks.Tick,
            SkyTick = SkyTweaks.Tick, SkyFrame = SkyTweaks.FrameTick, LifeTick = LifeTweaks.Tick, ShaderTimeFrame = FrozenShaderTime.FrameTick,
            FreeCameraRestore = FreeCamera.WatchRestore;
        private PhysicsMeter meter;
        private HitchMonitor monitor;
        private static string osdText;
        private static float osdUntil;
        private static GUIStyle osdStyle;

        private void Awake()
        {
            instance = this;
            Log = Logger;
            Cfg = Config;
            MenuKey = Config.Bind("General", "MenuKey", new KeyboardShortcut(KeyCode.F6),
                "Key for the World Tuner window. Esc also closes it.");
            BlockInput = Config.Bind("General", "BlockGameInput", true,
                "While the window is open, the game receives no mouse or keyboard input (clicks do not reach the game, the camera does not turn).");
            MenuOpenOnStart = Config.Bind("Debug", "MenuOpenOnStart", false, "Open the window right at startup (for testing).");

            WeatherTweaks.BindConfig(Config);
            FogTweaks.BindConfig(Config);
            DistanceTweaks.BindConfig(Config);
            PlayerSpeed.BindConfig(Config);
            PhysicsRate.BindConfig(Config);
            FreeCamera.BindConfig(Config);
            ImageTweaks.BindConfig(Config);
            MemoryTweaks.BindConfig(Config);
            HitchMonitor.BindConfig(Config);
            SkyTweaks.BindConfig(Config);
            FrozenShaderTime.BindConfig(Config);
            LifeTweaks.BindConfig(Config);

            // Sliders change values often, so the file is written on mouse release, not on every change.
            Config.SaveOnConfigSet = false;
            SaveConfig();

            // Permanent patches only on rare events (density change, settings apply, save/load). Per-frame hooks (window cursor,
            // NPC freeze) are installed only while that feature is in use.
            var harmony = new Harmony(Guid);
            TryPatch(harmony, typeof(FogPatches), "fog / volumetric light");
            TryPatch(harmony, typeof(DistancePatches), "quality settings");
            TryPatch(harmony, typeof(TimePatches), "time of day (save guard)");

            SceneManager.sceneLoaded += (scene, mode) => { MemoryTweaks.ScenesLoaded++; DistanceTweaks.OnSceneLoaded(scene); };
            SceneManager.sceneUnloaded += scene => MemoryTweaks.ScenesUnloaded++;

            // OnGUI costs every frame while it exists, so it lives on a component that is enabled only when something is shown.
            gui = gameObject.AddComponent<GuiHost>();
            gui.enabled = false;
            meter = gameObject.AddComponent<PhysicsMeter>();
            meter.enabled = false;
            monitor = gameObject.AddComponent<HitchMonitor>();
            monitor.enabled = false;

            Log.LogInfo($"{Name} {Version}: MenuKey={MenuKey.Value}, Fog.Mode={FogTweaks.Mode.Value}");
            if (MenuOpenOnStart.Value)
            {
                Menu.SetOpen(true);
            }
        }

        private static void TryPatch(Harmony harmony, Type type, string what)
        {
            try
            {
                harmony.PatchAll(type);
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to apply {what} patches, that part will not work: {e}");
            }
        }

        private void Update()
        {
            bool menuWasOpen = Menu.IsOpen;
            if (IsPressed(MenuKey.Value))
            {
                Menu.Toggle();
            }
            else if (Menu.IsOpen && Input.GetKeyDown(KeyCode.Escape))
            {
                Menu.SetOpen(false);
            }
            if (IsPressed(FreeCamera.Key.Value))
            {
                FreeCamera.Toggle();
            }
            if (menuWasOpen || Menu.IsOpen)
            {
                BlockGameInput(); // also in the frame where the window was closed by a key: Esc/F6 must not reach the game
            }
            // The free camera reads the keys and the mouse here, before the game's scripts. Its Esc must not reach the game;
            // the Esc that has just closed this window must not switch the camera off.
            if (FreeCamera.CaptureInput(Menu.IsOpen || menuWasOpen))
            {
                Input.ResetInputAxes();
            }

            if (saveRequested && GUIUtility.hotControl == 0)
            {
                saveRequested = false;
                SaveConfig();
            }

            float now = Time.unscaledTime;
            if (now >= nextTick)
            {
                nextTick = now + 0.5f;
                SafeTick("fog", FogTick);
                SafeTick("distance", DistanceTick);
                SafeTick("time", TimeTick);
                SafeTick("player speed", SpeedTick);
                SafeTick("memory", MemoryTick);
                SafeTick("sky", SkyTick);
                SafeTick("life", LifeTick);
                if (FreeCamera.Active)
                {
                    SafeTick("free camera", FreeCameraTick);
                }
            }
            SafeTick("distance (frame)", DistanceFrame);
            SafeTick("time (frame)", TimeFrame);
            if (SkyTweaks.Wetness.Value == WetMode.Fixed)
            {
                SafeTick("wetness", SkyFrame);
            }
            if (Time.timeScale == 0f || FrozenShaderTime.Attached)
            {
                SafeTick("shader time", ShaderTimeFrame);
            }
            if (FreeCamera.RestorePending)
            {
                SafeTick("free camera restore", FreeCameraRestore);
            }
            if (NpcFreeze.Active)
            {
                SafeTick("npc freeze", FreezeFrame);
            }

            bool showGui = Menu.IsOpen || (osdText != null && now <= osdUntil);
            if (gui.enabled != showGui)
            {
                gui.enabled = showGui;
            }
            bool showMeter = Menu.IsOpen && Menu.CurrentTab == Menu.TimeTab;
            if (meter.enabled != showMeter)
            {
                meter.enabled = showMeter;
            }
            bool showMonitor = HitchMonitor.Enabled.Value || (Menu.IsOpen && Menu.CurrentTab == Menu.MemoryTab);
            if (monitor.enabled != showMonitor)
            {
                monitor.enabled = showMonitor;
            }
        }

        /// <summary>The window's cursor hook is installed while the window is open.</summary>
        internal static void SetCursorPatch(bool on)
        {
            try
            {
                cursorHarmony = cursorHarmony ?? new Harmony(Guid + ".cursor");
                if (on)
                {
                    cursorHarmony.PatchAll(typeof(CursorPatches));
                }
                else
                {
                    cursorHarmony.UnpatchSelf();
                }
            }
            catch (Exception e)
            {
                LogOnce("cursorpatch", "Window cursor patch: " + e);
            }
        }

        private static void SafeTick(string what, Action tick)
        {
            try
            {
                tick();
            }
            catch (Exception e)
            {
                LogOnce(what, $"Error in {what} update: {e}");
            }
        }

        private static readonly System.Collections.Generic.HashSet<string> logged = new System.Collections.Generic.HashSet<string>();

        /// <summary>Log an error only once per key, so a broken part does not flood the log every frame.</summary>
        internal static void LogOnce(string key, string message)
        {
            if (logged.Add(key))
            {
                Log.LogError(message);
            }
        }

        private void LateUpdate()
        {
            if (Menu.IsOpen)
            {
                BlockGameInput();
            }
        }

        internal void DrawGui()
        {
            GUI.depth = -1000;
            if (Menu.IsOpen)
            {
                BlockGameInput();
                try
                {
                    Menu.Draw();
                }
                catch (Exception e)
                {
                    LogOnce("menu", "Error while drawing the window: " + e);
                }
            }
            DrawMessage();
        }

        /// <summary>
        /// Own check instead of KeyboardShortcut.IsDown(): that one only counts a press when nothing else is held,
        /// so the key did not fire while moving (WASD, Shift). Here unrelated keys are ignored.
        /// </summary>
        internal static bool IsPressed(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
            {
                return false;
            }
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!Input.GetKey(modifier))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Same as IsPressed, but for an IMGUI event (while the window is open, Input may be cleared by ResetInputAxes).</summary>
        internal static bool Matches(KeyboardShortcut shortcut, Event e)
        {
            if (shortcut.MainKey == KeyCode.None || e.keyCode != shortcut.MainKey)
            {
                return false;
            }
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                bool held;
                switch (modifier)
                {
                    case KeyCode.LeftControl: case KeyCode.RightControl: held = e.control; break;
                    case KeyCode.LeftShift: case KeyCode.RightShift: held = e.shift; break;
                    case KeyCode.LeftAlt: case KeyCode.RightAlt: case KeyCode.AltGr: held = e.alt; break;
                    default: held = Input.GetKey(modifier); break;
                }
                if (!held)
                {
                    return false;
                }
            }
            return true;
        }

        private static void BlockGameInput()
        {
            if (BlockInput.Value)
            {
                Input.ResetInputAxes();
            }
            if (Menu.IsOpen)
            {
                Menu.ForceCursor();
            }
        }

        /// <summary>Write the config later (in Update, when no slider is being dragged).</summary>
        internal static void RequestSave()
        {
            saveRequested = true;
        }

        private void SaveConfig()
        {
            try
            {
                Config.Save();
            }
            catch (Exception e)
            {
                Log.LogWarning("Failed to write config: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (saveRequested && instance == this)
            {
                SaveConfig();
            }
        }

        internal static void ShowMessage(string text)
        {
            osdText = text;
            osdUntil = Time.unscaledTime + 2.5f;
            Log.LogInfo(text);
        }

        private void DrawMessage()
        {
            if (osdText == null || Time.unscaledTime > osdUntil)
            {
                return;
            }
            if (osdStyle == null)
            {
                osdStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            }
            var rect = new Rect(24, 104, 900, 40);
            var shadow = new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height);
            osdStyle.normal.textColor = Color.black;
            GUI.Label(shadow, osdText, osdStyle);
            osdStyle.normal.textColor = Color.white;
            GUI.Label(rect, osdText, osdStyle);
        }
    }

    /// <summary>Hosts OnGUI; enabled only while the window or an on-screen message is shown.</summary>
    internal sealed class GuiHost : MonoBehaviour
    {
        private Plugin plugin;

        private void Awake()
        {
            plugin = GetComponent<Plugin>();
        }

        private void OnGUI()
        {
            plugin.DrawGui();
        }
    }

    /// <summary>While the window is open the cursor is visible and unlocked; the game's own fields stay untouched.</summary>
    [HarmonyPatch]
    internal static class CursorPatches
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            // 2.1.31563: UpdateCursor sets Cursor.*; 2.1.31648: UpdateCursor -> ApplyCursor, and focus regain calls ApplyCursor directly.
            foreach (string name in new[] { "UpdateCursor", "ApplyCursor" })
            {
                var m = AccessTools.Method(typeof(InputServices.WindowsCursorController), name);
                if (m != null)
                {
                    yield return m;
                }
            }
        }

        private static void Postfix()
        {
            if (Menu.IsOpen)
            {
                Menu.ForceCursor();
            }
        }
    }
}
