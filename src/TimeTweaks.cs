using System;
using Engine.Common.Services;
using Engine.Impl.Services;
using HarmonyLib;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// Time of day. The sky, sun, lamps and day/night street crowds follow TimeService.SolarTime (EnvironmentService copies it
    /// into TOD_Sky); quests use the separate game time, which is not touched. SolarTimeFactor is the speed (12 in the game).
    /// Both values are written to saves, so the mod puts the game's values back while a save is being written.
    /// Sleeping or a script may set the solar time again; then the mod's shift is forgotten.
    /// </summary>
    internal static class TimeTweaks
    {
        private static TimeSpan offset = TimeSpan.Zero;
        private static bool frozen;
        private static float savedFactor = 12f;

        internal static TimeService Service
        {
            get
            {
                try
                {
                    return ServiceLocator.GetService<TimeService>();
                }
                catch
                {
                    return null;
                }
            }
        }

        internal static bool Frozen => frozen;
        internal static TimeSpan Offset => offset;

        internal static void Tick()
        {
            if (!frozen)
            {
                return;
            }
            TimeService ts = Service;
            if (ts != null && ts.SolarTimeFactor != 0f)
            {
                frozen = false; // the game set its own speed (sleep, script): it takes over
            }
        }

        // ------------------------------------------------------------ world pause and game speed

        internal const float MaxSpeed = 10f;
        private static float speed = 1f;

        internal static float Speed => speed;

        /// <summary>Unity's Time.timeScale: 0 stops everything that runs on game time (physics, animation, particles, rain).</summary>
        internal static void SetSpeed(float value)
        {
            speed = Math.Max(0f, Math.Min(value, MaxSpeed));
            Time.timeScale = speed;
            speedHeld = speed != 1f;
            ImageTweaks.Refresh(); // motion blur breaks while the time stands
        }

        private static bool speedHeld;

        /// <summary>Every frame: keep the speed (nothing in the game sets Time.timeScale, but stay safe).</summary>
        internal static void FrameTick()
        {
            if (speedHeld && Math.Abs(Time.timeScale - speed) > 0.0001f)
            {
                Time.timeScale = speed;
            }
        }

        /// <summary>Set the hour of the current solar day (0-24), keeping the day.</summary>
        internal static void SetHour(float hours)
        {
            TimeService ts = Service;
            if (ts == null)
            {
                return;
            }
            TimeSpan current = ts.SolarTime;
            TimeSpan target = TimeSpan.FromDays(Math.Floor(current.TotalDays)) + TimeSpan.FromHours(Math.Max(0f, Math.Min(hours, 23.999f)));
            offset += target - current;
            ts.SolarTime = target;
        }

        internal static void SetFrozen(bool freeze)
        {
            TimeService ts = Service;
            if (ts == null || freeze == frozen)
            {
                return;
            }
            if (freeze)
            {
                savedFactor = ts.SolarTimeFactor > 0f ? ts.SolarTimeFactor : ts.DefaultTimeFactor;
                ts.SolarTimeFactor = 0f;
            }
            else if (ts.SolarTimeFactor == 0f)
            {
                ts.SolarTimeFactor = savedFactor;
            }
            frozen = freeze;
        }

        /// <summary>Undo the mod's shift of the solar time and let the clock run.</summary>
        internal static void BackToGameTime()
        {
            TimeService ts = Service;
            if (ts == null)
            {
                return;
            }
            SetFrozen(false);
            ts.SolarTime -= offset;
            offset = TimeSpan.Zero;
        }

        /// <summary>A save or a new game was loaded: the solar time comes from there.</summary>
        internal static void OnLoad()
        {
            offset = TimeSpan.Zero;
            frozen = false;
            // Loading must not run with frozen NPCs or at speed 0 (loading code may wait on game time).
            FreeCamera.OnLoad();
            NpcFreeze.OnLoad();
            if (speed != 1f)
            {
                SetSpeed(1f);
            }
        }

        internal struct SaveState
        {
            public bool Changed;
            public TimeSpan Offset;
            public float Factor;
        }

        internal static SaveState BeforeSave(TimeService ts)
        {
            var state = new SaveState { Offset = offset, Factor = ts.SolarTimeFactor };
            if (offset != TimeSpan.Zero)
            {
                ts.SolarTime -= offset;
                state.Changed = true;
            }
            if (frozen && ts.SolarTimeFactor == 0f)
            {
                ts.SolarTimeFactor = savedFactor;
                state.Changed = true;
            }
            return state;
        }

        internal static void AfterSave(TimeService ts, SaveState state)
        {
            if (!state.Changed)
            {
                return;
            }
            ts.SolarTime += state.Offset;
            ts.SolarTimeFactor = state.Factor;
        }
    }

    [HarmonyPatch]
    internal static class TimePatches
    {
        [HarmonyPatch(typeof(TimeService), nameof(TimeService.Save))]
        [HarmonyPrefix]
        private static void SavePrefix(TimeService __instance, out TimeTweaks.SaveState __state)
        {
            __state = default;
            try
            {
                __state = TimeTweaks.BeforeSave(__instance);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("timesave", "Time of day (save): " + e);
            }
        }

        [HarmonyPatch(typeof(TimeService), nameof(TimeService.Save))]
        [HarmonyPostfix]
        private static void SavePostfix(TimeService __instance, TimeTweaks.SaveState __state)
        {
            try
            {
                TimeTweaks.AfterSave(__instance, __state);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("timesave2", "Time of day (after save): " + e);
            }
        }

        // Both Load overloads (new game / saved game) return enumerators; resetting the mod's state on the call is enough.
        [HarmonyPatch(typeof(TimeService), nameof(TimeService.Load), new[] { typeof(Engine.Source.Services.Saves.IErrorLoadingHandler) })]
        [HarmonyPrefix]
        private static void LoadPrefix()
        {
            TimeTweaks.OnLoad();
        }

        [HarmonyPatch(typeof(TimeService), nameof(TimeService.Load),
            new[] { typeof(System.Xml.XmlElement), typeof(string), typeof(Engine.Source.Services.Saves.IErrorLoadingHandler) })]
        [HarmonyPrefix]
        private static void LoadXmlPrefix()
        {
            TimeTweaks.OnLoad();
        }
    }
}
