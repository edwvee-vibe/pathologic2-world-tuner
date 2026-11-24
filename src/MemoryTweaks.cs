using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Engine.Common.Services;
using Engine.Source.Otimizations;
using Engine.Source.Services;
using Engine.Source.Settings.External;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace Pathologic2WorldTuner
{
    internal enum GcMode
    {
        Game,
        Incremental
    }

    internal enum LoadPriority
    {
        Game,
        Low,
        BelowNormal,
        Normal,
        High
    }

    /// <summary>
    /// Memory and loading. The game keeps the garbage collector off (NoGCMemoryStrategy) and instead, on starting a game,
    /// entering a house, changing location and every MemoryStrategyTimeEventPeriod seconds of game time (1000), pauses the game
    /// with a small loading icon, unloads unused assets and runs a full blocking collection (MemoryStrategyService).
    /// Background loading runs at low priority (EngineApplication). Everything here is written once when a setting changes;
    /// a few cheap checks run twice a second only for settings that are not at "Game" (the game may set them back).
    /// </summary>
    internal static class MemoryTweaks
    {
        internal static ConfigEntry<float> CleanupPeriod, GcSliceMs, UploadSliceMs, UploadBufferMb, StreamingBudgetMb, StreamingReduction;
        internal static ConfigEntry<GcMode> Gc;
        internal static ConfigEntry<LoadPriority> Priority;

        internal const float CleanupMin = 60f, CleanupMax = 7200f;

        private static int gamePeriod = -1;      // MemoryStrategyTimeEventPeriod before the mod changed it
        private static ulong gameSliceNs;        // GarbageCollector.incrementalTimeSliceNanoseconds before the mod changed it
        private static bool sliceTouched;
        private static int gameUploadSlice = -1, gameUploadBuffer = -1;
        private static float gameBudget = -1f;
        private static int gameReduction = -1;
        private static bool priorityTouched, gcTouched, periodPending;

        private static readonly FieldInfo timeLeftField = typeof(MemoryStrategyService).GetField("timeLeft", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo enumeratorField = typeof(MemoryStrategyService).GetField("enumerator", BindingFlags.NonPublic | BindingFlags.Instance);
        private static MemoryStrategyService service;
        private static float nextServiceLookup;

        internal static int ScenesLoaded, ScenesUnloaded;

        internal static void BindConfig(ConfigFile cfg)
        {
            CleanupPeriod = cfg.Bind("Memory", "CleanupPeriod", -1f, new ConfigDescription(
                "Seconds of game time between the game's scheduled memory cleanups (a short pause with a loading icon). -1 = the game's (1000), 0 = no scheduled cleanups (houses and location changes still clean up).",
                new AcceptableValueRange<float>(-1f, CleanupMax)));
            Gc = cfg.Bind("Memory", "GarbageCollector", GcMode.Game,
                "Game: the collector is off between the game's cleanups. Incremental: Unity's incremental collector runs all the time in small slices (experiment).");
            GcSliceMs = cfg.Bind("Memory", "GcTimeSliceMs", -1f, new ConfigDescription(
                "Time per frame for the incremental collector, ms. -1 = the build's (3 ms).", new AcceptableValueRange<float>(-1f, 50f)));
            Priority = cfg.Bind("Loading", "BackgroundPriority", LoadPriority.Game,
                "Priority of background loading (time per frame Unity spends finishing loaded scenes). Game = Low. Hardly matters in " +
                "this game: scenes are small; HouseCheckInterval and ParallelSceneLoading are what limit how fast houses appear.");
            UploadSliceMs = cfg.Bind("Loading", "UploadTimeSliceMs", -1f, new ConfigDescription(
                "Time per frame for uploading loaded textures and meshes to the GPU, ms. -1 = the game's.", new AcceptableValueRange<float>(-1f, 33f)));
            UploadBufferMb = cfg.Bind("Loading", "UploadBufferMb", -1f, new ConfigDescription(
                "Size of the GPU upload ring buffer, MB. -1 = the game's.", new AcceptableValueRange<float>(-1f, 512f)));
            StreamingBudgetMb = cfg.Bind("Loading", "TextureStreamingBudgetMb", -1f, new ConfigDescription(
                "Memory budget for streamed textures, MB. -1 = the game's (set by the texture quality).", new AcceptableValueRange<float>(-1f, 16384f)));
            StreamingReduction = cfg.Bind("Loading", "TextureStreamingMaxReduction", -1f, new ConfigDescription(
                "How many mip levels textures may be lowered to fit the budget. -1 = the game's.", new AcceptableValueRange<float>(-1f, 7f)));

            CleanupPeriod.SettingChanged += (o, e) => { periodPending = true; ApplyPeriod(); };
            Gc.SettingChanged += (o, e) => ApplyGc();
            GcSliceMs.SettingChanged += (o, e) => ApplySlice();
            Priority.SettingChanged += (o, e) => ApplyPriority();
            UploadSliceMs.SettingChanged += (o, e) => ApplyUpload();
            UploadBufferMb.SettingChanged += (o, e) => ApplyUpload();
            StreamingBudgetMb.SettingChanged += (o, e) => ApplyStreaming();
            StreamingReduction.SettingChanged += (o, e) => ApplyStreaming();

            periodPending = CleanupPeriod.Value >= 0f;
            ApplySlice();
            ApplyUpload();
        }

        /// <summary>Twice a second. Only settings that are not at "Game" do anything here.</summary>
        internal static void Tick()
        {
            if (periodPending)
            {
                ApplyPeriod();
            }
            if (Priority.Value != LoadPriority.Game)
            {
                ApplyPriority(); // the game sets Low when the engine starts
            }
            if (Gc.Value == GcMode.Incremental && GarbageCollector.GCMode != GarbageCollector.Mode.Enabled && !CleanupRunning)
            {
                ApplyGc(); // the game's cleanup switches the collector off again when it ends
            }
        }

        // ------------------------------------------------------------ scheduled cleanup

        internal static MemoryStrategyService Service
        {
            get
            {
                // Looked up again every second: the service is created anew for every game session.
                if (Time.unscaledTime >= nextServiceLookup)
                {
                    nextServiceLookup = Time.unscaledTime + 1f;
                    try
                    {
                        service = ServiceLocator.GetService<MemoryStrategyService>();
                    }
                    catch
                    {
                        service = null;
                    }
                }
                return service;
            }
        }

        internal static bool CleanupRunning
        {
            get
            {
                MemoryStrategyService s = Service;
                return s != null && enumeratorField != null && enumeratorField.GetValue(s) != null;
            }
        }

        internal static float TimeLeft => timeLeftField != null ? (float)timeLeftField.GetValue(null) : -1f;

        internal static int GamePeriod
        {
            get
            {
                if (gamePeriod >= 0)
                {
                    return gamePeriod;
                }
                if (Service == null)
                {
                    return 1000; // not in a game: do not make the game load its settings early
                }
                ExternalOptimizationSettings s = ExternalSettingsInstance<ExternalOptimizationSettings>.Instance;
                return s != null ? s.MemoryStrategyTimeEventPeriod : 1000;
            }
        }

        internal static string StrategyName
        {
            get
            {
                if (Service == null)
                {
                    return "?";
                }
                try
                {
                    // The game's factory creates generated proxies ("NoGCMemoryStrategy_Generated").
                    return MemoryStrategy.Instance?.GetType().Name.Replace("_Generated", "") ?? "?";
                }
                catch
                {
                    return "?";
                }
            }
        }

        private static void ApplyPeriod()
        {
            if (Service == null)
            {
                return; // applied once a game is running (periodPending stays set)
            }
            ExternalOptimizationSettings s;
            try
            {
                s = ExternalSettingsInstance<ExternalOptimizationSettings>.Instance;
            }
            catch
            {
                return;
            }
            if (s == null)
            {
                return;
            }
            periodPending = false;
            float v = CleanupPeriod.Value;
            if (v < 0f)
            {
                if (gamePeriod >= 0)
                {
                    s.MemoryStrategyTimeEventPeriod = gamePeriod;
                    if (TimeLeft > gamePeriod)
                    {
                        SetTimeLeft(gamePeriod);
                    }
                    gamePeriod = -1;
                    Plugin.Log.LogInfo($"Memory: scheduled cleanup back to the game's {s.MemoryStrategyTimeEventPeriod} s");
                }
                return;
            }
            if (gamePeriod < 0)
            {
                gamePeriod = s.MemoryStrategyTimeEventPeriod;
            }
            int period = v <= 0f ? int.MaxValue / 2 : (int)Mathf.Clamp(v, CleanupMin, CleanupMax);
            s.MemoryStrategyTimeEventPeriod = period;
            float left = TimeLeft;
            if (left < 0f || left > period || v <= 0f)
            {
                SetTimeLeft(period);
            }
            Plugin.Log.LogInfo(v <= 0f ? "Memory: scheduled cleanups off" : $"Memory: scheduled cleanup every {period} s");
        }

        private static void SetTimeLeft(float seconds)
        {
            timeLeftField?.SetValue(null, seconds);
        }

        /// <summary>The game runs its cleanup on the next frame of normal play (not in menus, dialogs or the free camera).</summary>
        internal static void CleanupNow()
        {
            SetTimeLeft(0f);
            Plugin.ShowMessage("Memory cleanup: runs as soon as you are back in normal play");
        }

        // ------------------------------------------------------------ garbage collector

        private static void ApplyGc()
        {
            if (Gc.Value == GcMode.Incremental)
            {
                if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled)
                {
                    GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                }
                if (!gcTouched)
                {
                    gcTouched = true;
                    Plugin.Log.LogInfo($"Memory: garbage collector on (incremental: {GarbageCollector.isIncremental})");
                }
            }
            else if (gcTouched)
            {
                gcTouched = false;
                // Back to the game's state: its strategy keeps the collector off (other strategies leave it on).
                if (Service != null && MemoryStrategy.Instance is NoGCMemoryStrategy && !CleanupRunning)
                {
                    GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                }
                Plugin.Log.LogInfo("Memory: garbage collector back to the game's mode");
            }
        }

        private static void ApplySlice()
        {
            float ms = GcSliceMs.Value;
            if (ms < 0f)
            {
                if (sliceTouched)
                {
                    GarbageCollector.incrementalTimeSliceNanoseconds = gameSliceNs;
                    sliceTouched = false;
                }
                return;
            }
            if (!sliceTouched)
            {
                gameSliceNs = GarbageCollector.incrementalTimeSliceNanoseconds;
                sliceTouched = true;
            }
            GarbageCollector.incrementalTimeSliceNanoseconds = (ulong)(Mathf.Clamp(ms, 0.1f, 50f) * 1_000_000f);
        }

        internal static float GcSliceNowMs => GarbageCollector.incrementalTimeSliceNanoseconds / 1_000_000f;
        internal static float GameGcSliceMs => (sliceTouched ? gameSliceNs : GarbageCollector.incrementalTimeSliceNanoseconds) / 1_000_000f;

        // ------------------------------------------------------------ loading

        private static void ApplyPriority()
        {
            if (Priority.Value == LoadPriority.Game)
            {
                if (priorityTouched)
                {
                    priorityTouched = false;
                    Application.backgroundLoadingPriority = ThreadPriority.Low;
                }
                return;
            }
            ThreadPriority p = Priority.Value == LoadPriority.Low ? ThreadPriority.Low
                : Priority.Value == LoadPriority.BelowNormal ? ThreadPriority.BelowNormal
                : Priority.Value == LoadPriority.Normal ? ThreadPriority.Normal : ThreadPriority.High;
            if (Application.backgroundLoadingPriority != p)
            {
                Application.backgroundLoadingPriority = p;
            }
            priorityTouched = true;
        }

        private static void ApplyUpload()
        {
            if (UploadSliceMs.Value >= 0f)
            {
                if (gameUploadSlice < 0)
                {
                    gameUploadSlice = QualitySettings.asyncUploadTimeSlice;
                }
                QualitySettings.asyncUploadTimeSlice = Mathf.Clamp(Mathf.RoundToInt(UploadSliceMs.Value), 1, 33);
            }
            else if (gameUploadSlice >= 0)
            {
                QualitySettings.asyncUploadTimeSlice = gameUploadSlice;
                gameUploadSlice = -1;
            }
            if (UploadBufferMb.Value >= 0f)
            {
                if (gameUploadBuffer < 0)
                {
                    gameUploadBuffer = QualitySettings.asyncUploadBufferSize;
                }
                int mb = Mathf.Clamp(Mathf.RoundToInt(UploadBufferMb.Value), 2, 512);
                if (QualitySettings.asyncUploadBufferSize != mb)
                {
                    QualitySettings.asyncUploadBufferSize = mb; // reallocates the buffer once
                }
            }
            else if (gameUploadBuffer >= 0)
            {
                QualitySettings.asyncUploadBufferSize = gameUploadBuffer;
                gameUploadBuffer = -1;
            }
        }

        internal static int GameUploadSlice => gameUploadSlice >= 0 ? gameUploadSlice : QualitySettings.asyncUploadTimeSlice;
        internal static int GameUploadBuffer => gameUploadBuffer >= 0 ? gameUploadBuffer : QualitySettings.asyncUploadBufferSize;
        internal static float GameBudget => gameBudget >= 0f ? gameBudget : QualitySettings.streamingMipmapsMemoryBudget;
        internal static int GameReduction => gameReduction >= 0 ? gameReduction : QualitySettings.streamingMipmapsMaxLevelReduction;

        private static void ApplyStreaming()
        {
            if (StreamingBudgetMb.Value >= 0f)
            {
                if (gameBudget < 0f)
                {
                    gameBudget = QualitySettings.streamingMipmapsMemoryBudget;
                }
                QualitySettings.streamingMipmapsMemoryBudget = Mathf.Max(32f, StreamingBudgetMb.Value);
            }
            else if (gameBudget >= 0f)
            {
                QualitySettings.streamingMipmapsMemoryBudget = gameBudget;
                gameBudget = -1f;
            }
            if (StreamingReduction.Value >= 0f)
            {
                if (gameReduction < 0)
                {
                    gameReduction = QualitySettings.streamingMipmapsMaxLevelReduction;
                }
                QualitySettings.streamingMipmapsMaxLevelReduction = Mathf.Clamp(Mathf.RoundToInt(StreamingReduction.Value), 0, 7);
            }
            else if (gameReduction >= 0)
            {
                QualitySettings.streamingMipmapsMaxLevelReduction = gameReduction;
                gameReduction = -1;
            }
        }

        /// <summary>After the game applied its graphics settings (TextureSettings.Apply sets the streaming budget).</summary>
        internal static void OnGameQualityApplied()
        {
            if (gameBudget >= 0f)
            {
                gameBudget = QualitySettings.streamingMipmapsMemoryBudget;
            }
            if (gameReduction >= 0)
            {
                gameReduction = QualitySettings.streamingMipmapsMaxLevelReduction;
            }
            ApplyStreaming();
            ApplyUpload();
        }
    }

    /// <summary>
    /// Frame time and hitches: enabled only while the Memory tab is open or the monitor is switched on. Per frame: a few integer
    /// reads; a hitch is a frame longer than the threshold, recorded with what happened in that frame.
    /// </summary>
    internal sealed class HitchMonitor : MonoBehaviour
    {
        internal static ConfigEntry<bool> Enabled, LogHitches;
        internal static ConfigEntry<float> ThresholdMs;

        internal struct Hitch
        {
            public float RealTime;
            public string Clock; // local wall-clock time, as in the game's Player.log and in MSI Afterburner
            public float Ms;
            public string Cause;
        }

        internal static readonly List<Hitch> Recent = new List<Hitch>();
        internal static float AvgMs, MaxMs, Fps;
        internal static int HitchCount;
        internal static bool HasData;

        private int lastGc, lastLoaded, lastUnloaded;
        private bool lastCleanup;
        private int frames;
        private double sum;
        private float max, windowStart;

        internal static void BindConfig(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Monitor", "Enabled", false, "Watch frame times while the window is closed too (a few integer reads per frame).");
            LogHitches = cfg.Bind("Monitor", "LogHitches", true, "Write every hitch to BepInEx\\LogOutput.log while the monitor runs.");
            ThresholdMs = cfg.Bind("Monitor", "ThresholdMs", 50f, new ConfigDescription("A frame longer than this counts as a hitch, ms.",
                new AcceptableValueRange<float>(10f, 1000f)));
        }

        private void OnEnable()
        {
            lastGc = GC.CollectionCount(0);
            lastLoaded = MemoryTweaks.ScenesLoaded;
            lastUnloaded = MemoryTweaks.ScenesUnloaded;
            lastCleanup = false;
            frames = 0;
            sum = 0;
            max = 0f;
            windowStart = Time.unscaledTime;
        }

        private void Update()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            frames++;
            sum += ms;
            if (ms > max)
            {
                max = ms;
            }

            int gc = GC.CollectionCount(0);
            int loaded = MemoryTweaks.ScenesLoaded, unloaded = MemoryTweaks.ScenesUnloaded;
            bool cleanup = MemoryTweaks.CleanupRunning;
            if (ms >= ThresholdMs.Value && frames > 1)
            {
                string cause = "";
                if (gc != lastGc) cause += $"garbage collection x{gc - lastGc}; ";
                if (cleanup || lastCleanup) cause += "game memory cleanup; ";
                if (loaded != lastLoaded) cause += $"{loaded - lastLoaded} scene(s) loaded; ";
                if (unloaded != lastUnloaded) cause += $"{unloaded - lastUnloaded} scene(s) unloaded; ";
                if (cause.Length == 0) cause = "nothing the monitor tracks; ";
                cause = cause.Substring(0, cause.Length - 2);
                HitchCount++;
                string clock = DateTime.Now.ToString("HH:mm:ss.fff");
                Recent.Insert(0, new Hitch { RealTime = Time.realtimeSinceStartup, Clock = clock, Ms = ms, Cause = cause });
                if (Recent.Count > 12)
                {
                    Recent.RemoveAt(Recent.Count - 1);
                }
                if (LogHitches.Value && !Menu.IsOpen)
                {
                    Plugin.Log.LogInfo($"Hitch at {clock}: {ms:0} ms ({cause})");
                }
            }
            lastGc = gc;
            lastLoaded = loaded;
            lastUnloaded = unloaded;
            lastCleanup = cleanup;

            if (Time.unscaledTime - windowStart >= 1f && frames > 0)
            {
                AvgMs = (float)(sum / frames);
                MaxMs = max;
                Fps = frames / (Time.unscaledTime - windowStart);
                HasData = true;
                frames = 0;
                sum = 0;
                max = 0f;
                windowStart = Time.unscaledTime;
            }
        }
    }

    /// <summary>Memory numbers for the window, refreshed twice a second while it shows them.</summary>
    internal static class MemoryStats
    {
        private static float next;
        internal static long MonoUsed, MonoHeap, Allocated, Reserved, Gfx;
        internal static ulong TexCurrent, TexDesired, TexTarget, TexNonStreaming;
        internal static int Collections;

        internal static void Refresh()
        {
            if (Time.unscaledTime < next)
            {
                return;
            }
            next = Time.unscaledTime + 0.5f;
            MonoUsed = Profiler.GetMonoUsedSizeLong();
            MonoHeap = Profiler.GetMonoHeapSizeLong();
            Allocated = Profiler.GetTotalAllocatedMemoryLong();
            Reserved = Profiler.GetTotalReservedMemoryLong();
            Gfx = Profiler.GetAllocatedMemoryForGraphicsDriver();
            TexCurrent = Texture.currentTextureMemory;
            TexDesired = Texture.desiredTextureMemory;
            TexTarget = Texture.targetTextureMemory;
            TexNonStreaming = Texture.nonStreamingTextureMemory;
            Collections = GC.CollectionCount(0);
        }

        internal static string Mb(long bytes) => $"{bytes / 1048576.0:0} MB";
        internal static string Mb(ulong bytes) => $"{bytes / 1048576.0:0} MB";
    }
}
