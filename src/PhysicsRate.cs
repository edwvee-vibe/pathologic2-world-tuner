using System.Diagnostics;
using BepInEx.Configuration;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// Physics rate: Unity's Time.fixedDeltaTime, one global value (the game runs at 30 Hz, 0.0333 s). The game never sets it
    /// in code, so it is written once when the setting changes and left alone otherwise: with "as in the game" (-1) the mod
    /// does not touch it at all. Everything in FixedUpdate runs once per step: PhysX, the player's movement, blocking, the
    /// combat camera kicks, the animators of walking NPCs (AnimatePhysics), ragdolls. Game code uses Time.fixedDeltaTime, so
    /// speeds and timings stay the same; only the cost per second grows with the rate.
    /// </summary>
    internal static class PhysicsRate
    {
        internal const float MinHz = 5f, MaxHz = 1000f, SliderMinHz = 10f, SliderMaxHz = 240f;

        internal static ConfigEntry<float> Rate;

        private static float gameStep = -1f; // fixedDeltaTime before the mod changed it
        private static bool touched;

        internal static void BindConfig(ConfigFile cfg)
        {
            Rate = cfg.Bind("Physics", "Rate", -1f, new ConfigDescription(
                "Physics steps per second (Unity's fixed timestep). -1 = as in the game (30). Higher values make movement, " +
                "blocking and walking NPCs step more finely and cost CPU time in proportion; values below 5 are raised to 5.",
                new AcceptableValueRange<float>(-1f, MaxHz)));
            Rate.SettingChanged += (o, e) => Apply();
            Apply();
        }

        internal static bool Active => touched;

        /// <summary>The game's step, also before anything was changed.</summary>
        internal static float GameHz => 1f / (gameStep > 0f ? gameStep : Time.fixedDeltaTime);

        internal static float CurrentHz => 1f / Time.fixedDeltaTime;

        private static void Apply()
        {
            float hz = Rate.Value;
            if (hz < 0f)
            {
                if (touched)
                {
                    Time.fixedDeltaTime = gameStep;
                    touched = false;
                    Plugin.Log.LogInfo($"Physics rate: back to the game's {1f / gameStep:0.#} Hz");
                }
                return;
            }
            if (!touched)
            {
                gameStep = Time.fixedDeltaTime;
            }
            hz = Mathf.Clamp(hz, MinHz, MaxHz);
            Time.fixedDeltaTime = 1f / hz;
            touched = true;
            Plugin.Log.LogInfo($"Physics rate: {hz:0.#} Hz (fixed step {Time.fixedDeltaTime:0.#####} s, game {1f / gameStep:0.#} Hz)");
        }
    }

    /// <summary>
    /// Measures the physics part of each frame: from the first FixedUpdate of the frame to the first Update (Unity runs all
    /// fixed steps, with the scripts, PhysX and AnimatePhysics animators, between them). Enabled only while the window shows
    /// the Time tab. Approximate: scripts ordered before this one in a step are not counted.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    internal sealed class PhysicsMeter : MonoBehaviour
    {
        private static readonly Stopwatch clock = Stopwatch.StartNew();

        internal static bool HasData;
        internal static float AvgSteps, AvgMs, MaxMs;

        private long fixedStart;
        private bool inFixed;
        private int steps, frames, stepSum;
        private double msSum, msMax;
        private float windowStart;

        private void OnEnable()
        {
            HasData = false;
            inFixed = false;
            frames = stepSum = 0;
            msSum = msMax = 0;
            windowStart = Time.unscaledTime;
        }

        private void FixedUpdate()
        {
            if (!inFixed)
            {
                inFixed = true;
                steps = 0;
                fixedStart = clock.ElapsedTicks;
            }
            steps++;
        }

        private void Update()
        {
            double ms = 0;
            int s = 0;
            if (inFixed)
            {
                ms = (clock.ElapsedTicks - fixedStart) * 1000.0 / Stopwatch.Frequency;
                s = steps;
                inFixed = false;
            }
            frames++;
            stepSum += s;
            msSum += ms;
            if (ms > msMax)
            {
                msMax = ms;
            }
            if (Time.unscaledTime - windowStart >= 0.5f && frames > 0)
            {
                AvgSteps = (float)stepSum / frames;
                AvgMs = (float)(msSum / frames);
                MaxMs = (float)msMax;
                HasData = true;
                frames = stepSum = 0;
                msSum = msMax = 0;
                windowStart = Time.unscaledTime;
            }
        }
    }
}
