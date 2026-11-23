using UnityEngine;
using UnityEngine.Scripting;

namespace Pathologic2WorldTuner
{
    /// <summary>Tab "Memory & Loading": hitch monitor, memory, the game's cleanups, garbage collector, loading, texture streaming.</summary>
    internal static partial class Menu
    {
        private static readonly string[] GcModes = { "Game (off between cleanups)", "Incremental (always on)" };
        private static readonly string[] Priorities = { "Game (Low)", "Low", "Below normal", "Normal", "High" };

        private static void DrawMemory()
        {
            // --- frame times
            GUILayout.Label("Frame times and hitches", headerStyle);
            GUILayout.Label(HitchMonitor.HasData
                ? $"{HitchMonitor.Fps:0} fps, {HitchMonitor.AvgMs:0.0} ms on average, {HitchMonitor.MaxMs:0.0} ms the longest frame in the last second"
                : "Measuring...", valueStyle);
            DefaultRow("Hitch threshold", HitchMonitor.ThresholdMs, 10f, 1000f, 50f, v => $"{v:0} ms", true);
            GUILayout.BeginHorizontal();
            PlainToggle(HitchMonitor.Enabled, " Keep watching while the window is closed");
            PlainToggle(HitchMonitor.LogHitches, " Write hitches to LogOutput.log");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear", GUILayout.Width(ButtonWidth)))
            {
                HitchMonitor.Recent.Clear();
                HitchMonitor.HitchCount = 0;
            }
            GUILayout.EndHorizontal();
            if (HitchMonitor.Recent.Count == 0)
            {
                GUILayout.Label("No hitches recorded yet.", hintStyle);
            }
            else
            {
                GUILayout.Label($"{HitchMonitor.HitchCount} hitch(es), the latest first:", hintStyle);
                float now = Time.realtimeSinceStartup;
                foreach (HitchMonitor.Hitch h in HitchMonitor.Recent)
                {
                    GUILayout.Label($"   {h.Clock} ({Ago(now - h.RealTime)}): {h.Ms:0} ms - {h.Cause}", valueStyle);
                }
            }
            GUILayout.Label("The monitor runs while this tab is open, or always with the switch above (a few integer reads per frame). " +
                            "Scenes are houses and districts being loaded. Play with the window closed, then come back here or read the log.",
                hintStyle);

            // --- memory
            MemoryStats.Refresh();
            GUILayout.Space(10);
            GUILayout.Label("Memory", headerStyle);
            GUILayout.Label($"Scripts (Mono heap): {MemoryStats.Mb(MemoryStats.MonoUsed)} used of {MemoryStats.Mb(MemoryStats.MonoHeap)}.  " +
                            $"Unity: {MemoryStats.Mb(MemoryStats.Allocated)} allocated, {MemoryStats.Mb(MemoryStats.Reserved)} reserved.  " +
                            (MemoryStats.Gfx > 0 ? $"Graphics driver: {MemoryStats.Mb(MemoryStats.Gfx)}" : ""), valueStyle);
            GUILayout.Label($"Garbage collector: {GarbageCollector.GCMode}, incremental: {(GarbageCollector.isIncremental ? "yes" : "no")}, " +
                            $"slice {MemoryTweaks.GcSliceNowMs:0.#} ms, collections so far: {MemoryStats.Collections}.  " +
                            $"Game strategy: {MemoryTweaks.StrategyName}", valueStyle);

            // --- scheduled cleanup
            GUILayout.Space(10);
            GUILayout.Label("The game's memory cleanup", headerStyle);
            float left = MemoryTweaks.TimeLeft;
            bool running = MemoryTweaks.CleanupRunning;
            GUILayout.Label(running ? "Cleanup is running now."
                : left < 0f ? "The cleanup timer is not available."
                : left > 1e8f ? "Scheduled cleanups are off."
                : $"Next scheduled cleanup in {left:0} s of game time.", valueStyle);
            FloatRow("Scheduled every", MemoryTweaks.CleanupPeriod, MemoryTweaks.CleanupMin, MemoryTweaks.CleanupMax, 10f, Mode.AbsoluteLog,
                MemoryTweaks.GamePeriod, v => MemoryTweaks.CleanupPeriod.Value == 0f ? "never" : $"{v:0} s ({v / 60f:0.#} min)");
            GUILayout.BeginHorizontal();
            bool never = MemoryTweaks.CleanupPeriod.Value == 0f;
            if (GUILayout.Button("Never", never ? selectedButton : GUI.skin.button, GUILayout.Width(ButtonWidth + 20)) && !never)
            {
                pending.Remove(MemoryTweaks.CleanupPeriod);
                MemoryTweaks.CleanupPeriod.Value = 0f;
                Plugin.RequestSave();
            }
            if (GUILayout.Button("Clean up now", GUILayout.Width(130)))
            {
                MemoryTweaks.CleanupNow();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("The game keeps the garbage collector off. Instead, when a game starts, when you enter a house or change " +
                            "location, and on this timer (game time: faster at high game speed, stopped at 0) it pauses the game with a " +
                            "small loading icon, unloads unused assets and collects all garbage at once. Without the timer the cleanups " +
                            "still happen on entering houses, but memory grows more between them on long walks.", hintStyle);

            // --- garbage collector
            GUILayout.Space(10);
            GUILayout.Label("Garbage collector", headerStyle);
            int gc = (int)MemoryTweaks.Gc.Value;
            int newGc = GUILayout.Toolbar(gc, GcModes);
            if (newGc != gc)
            {
                MemoryTweaks.Gc.Value = (GcMode)newGc;
                Plugin.RequestSave();
            }
            FloatRow("Time slice per frame", MemoryTweaks.GcSliceMs, 0.5f, 20f, 0.5f, Mode.Absolute, MemoryTweaks.GameGcSliceMs, v => $"{v:0.0} ms");
            GUILayout.Label(GarbageCollector.isIncremental
                    ? "Experiment. Incremental: Unity collects garbage in small slices every frame instead of the game's big cleanups " +
                      "(those still run). It may smooth out memory growth or add small, steady cost per frame; compare with the monitor above."
                    : "This build has no incremental collector: switching it on would mean full stop-the-world collections. Not recommended.",
                GarbageCollector.isIncremental ? hintStyle : warnStyle);

            // --- loading
            GUILayout.Space(10);
            GUILayout.Label("Loading", headerStyle);
            int pr = (int)MemoryTweaks.Priority.Value;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Background loading", GUILayout.Width(LabelWidth));
            int newPr = GUILayout.Toolbar(pr, Priorities);
            GUILayout.EndHorizontal();
            if (newPr != pr)
            {
                MemoryTweaks.Priority.Value = (LoadPriority)newPr;
                Plugin.RequestSave();
            }
            GUILayout.Label($"      Now: {Application.backgroundLoadingPriority}. Higher priority loads houses and districts sooner, " +
                            "but the frames in which they are integrated may get longer.", hintStyle);
            FloatRow("GPU upload per frame", MemoryTweaks.UploadSliceMs, 1f, 33f, 1f, Mode.Absolute, MemoryTweaks.GameUploadSlice, v => $"{v:0} ms");
            FloatRow("GPU upload buffer", MemoryTweaks.UploadBufferMb, 4f, 512f, 1f, Mode.AbsoluteLog, MemoryTweaks.GameUploadBuffer,
                v => $"{v:0} MB", null, true);
            GUILayout.Label("Loaded textures and meshes are copied to the GPU in slices of this time per frame through a buffer of this " +
                            "size. More time: things appear sooner, single frames get longer. The buffer applies when the slider is " +
                            "released (it is reallocated once).", hintStyle);

            // --- texture streaming
            GUILayout.Space(10);
            GUILayout.Label("Texture streaming", headerStyle);
            GUILayout.Label($"Budget {QualitySettings.streamingMipmapsMemoryBudget:0} MB ({(QualitySettings.streamingMipmapsActive ? "streaming on" : "streaming off")}).  " +
                            $"Textures: {MemoryStats.Mb(MemoryStats.TexCurrent)} now, {MemoryStats.Mb(MemoryStats.TexDesired)} wanted at full detail, " +
                            $"{MemoryStats.Mb(MemoryStats.TexTarget)} target, {MemoryStats.Mb(MemoryStats.TexNonStreaming)} not streamed", valueStyle);
            if (QualitySettings.streamingMipmapsActive && MemoryStats.TexDesired > QualitySettings.streamingMipmapsMemoryBudget * 1048576.0)
            {
                GUILayout.Label("The wanted texture memory is above the budget: some textures are shown at lower detail.", warnStyle);
            }
            FloatRow("Budget", MemoryTweaks.StreamingBudgetMb, 128f, 16384f, 64f, Mode.AbsoluteLog, MemoryTweaks.GameBudget,
                v => $"{v:0} MB", null, true);
            FloatRow("Max detail reduction", MemoryTweaks.StreamingReduction, 0f, 7f, 1f, Mode.Absolute, MemoryTweaks.GameReduction,
                v => $"{v:0} mip levels");
            GUILayout.Label("The budget limits video memory for streamed textures; a larger draw distance needs more. Keep it below your " +
                            $"video memory ({SystemInfo.graphicsMemorySize} MB on this card) minus what the game needs for the rest.", hintStyle);
        }

        private static string Ago(float seconds)
        {
            return seconds < 60f ? $"{seconds:0} s ago" : seconds < 3600f ? $"{seconds / 60f:0} min ago" : $"{seconds / 3600f:0.0} h ago";
        }
    }
}
