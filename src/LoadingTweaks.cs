using System;
using System.Reflection;
using BepInEx.Configuration;
using Engine.Source.Commons;
using Engine.Source.Settings.External;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// What actually limits how fast houses appear [code]:
    /// - SceneController.CanLoad lets only one scene load at a time unless ExternalOptimizationSettings.MultipleSceneLoader
    ///   (false in the game) is set; the setting is read on every request.
    /// - The houses' loaders (LoadByDistanceFromSegment, the only users of UpdateService.RegionDistanceUpdater) check the
    ///   distance to the player about once per RegionDistanceDelay (1 s). The delay is copied into the updater
    ///   (SmoothUpdater -> ReduceUpdateProxy.delay, a struct field) when the service is created, so the mod writes it there.
    /// Scene loads themselves are short (0.02-0.03 s on average in Player.log), which is why the background loading priority
    /// hardly matters in this game. Everything is applied only in a running game and only after a change.
    /// </summary>
    internal static class LoadingTweaks
    {
        internal static ConfigEntry<bool> ParallelScenes;
        internal static ConfigEntry<float> HouseCheckInterval;

        private static bool dirty;
        private static bool gameParallelKnown;
        private static bool gameParallel;
        private static float gameInterval = -1f;
        private static object updaterSeen;

        private static readonly FieldInfo smoothUpdaterField = typeof(SmoothUpdater).GetField("updater", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo delayField = smoothUpdaterField?.FieldType.GetField("delay", BindingFlags.NonPublic | BindingFlags.Instance);

        internal static float GameInterval => gameInterval > 0f ? gameInterval : 1f;
        internal static float IntervalNow = -1f;

        internal static void BindConfig(ConfigFile cfg)
        {
            ParallelScenes = cfg.Bind("Loading", "ParallelSceneLoading", false,
                "Experiment. Let several house and district scenes load at the same time (the game loads them one by one: " +
                "ExternalOptimizationSettings.MultipleSceneLoader). The developers turned it off; may cause hitches or errors.");
            HouseCheckInterval = cfg.Bind("Loading", "HouseCheckInterval", -1f, new ConfigDescription(
                "How often each house checks its distance to the player to start or stop loading, seconds. -1 = the game's (1 s). " +
                "Lower = houses start loading sooner; the checks are cheap.", new AcceptableValueRange<float>(-1f, 2f)));
            ParallelScenes.SettingChanged += (s, e) => dirty = true;
            HouseCheckInterval.SettingChanged += (s, e) => dirty = true;
            dirty = ParallelScenes.Value || HouseCheckInterval.Value > 0f;
        }

        /// <summary>Twice a second.</summary>
        internal static void Tick()
        {
            // ExternalSettingsInstance loads the XML on first access: only in a running game (see MemoryTweaks).
            if (MemoryTweaks.Service == null)
            {
                return;
            }
            UpdateService us = InstanceByRequest<UpdateService>.Instance;
            object updater = us?.RegionDistanceUpdater;
            if (updater != null && updater != updaterSeen)
            {
                updaterSeen = updater; // a new game session: apply again
                dirty = dirty || HouseCheckInterval.Value > 0f;
                ReadInterval(updater);
            }
            if (!dirty)
            {
                return;
            }
            dirty = false;
            try
            {
                ExternalOptimizationSettings s = ExternalSettingsInstance<ExternalOptimizationSettings>.Instance;
                if (!gameParallelKnown)
                {
                    gameParallelKnown = true;
                    gameParallel = s.MultipleSceneLoader;
                }
                bool parallel = ParallelScenes.Value || gameParallel;
                if (s.MultipleSceneLoader != parallel)
                {
                    s.MultipleSceneLoader = parallel;
                    Plugin.Log.LogInfo($"Loading: scenes load {(parallel ? "in parallel" : "one at a time (game)")}");
                }
                if (updater != null)
                {
                    SetInterval(updater, HouseCheckInterval.Value > 0f ? HouseCheckInterval.Value : GameInterval);
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("loadingtweaks", "Loading: " + e);
            }
        }

        private static void ReadInterval(object updater)
        {
            if (!(updater is SmoothUpdater) || smoothUpdaterField == null || delayField == null)
            {
                return;
            }
            object proxy = smoothUpdaterField.GetValue(updater); // boxed copy of the struct
            float d = (float)delayField.GetValue(proxy);
            IntervalNow = d;
            if (gameInterval < 0f)
            {
                gameInterval = d;
                Plugin.Log.LogInfo($"Loading: houses check their distance every {d:0.##} s (game)");
            }
        }

        private static void SetInterval(object updater, float seconds)
        {
            if (!(updater is SmoothUpdater) || smoothUpdaterField == null || delayField == null)
            {
                Plugin.LogOnce("loadinginterval", "Loading: the house check interval cannot be set (game updated?)");
                return;
            }
            object proxy = smoothUpdaterField.GetValue(updater);
            if (Math.Abs((float)delayField.GetValue(proxy) - seconds) < 0.0001f)
            {
                return;
            }
            delayField.SetValue(proxy, seconds); // the boxed copy...
            smoothUpdaterField.SetValue(updater, proxy); // ...written back into the updater
            IntervalNow = seconds;
            Plugin.Log.LogInfo($"Loading: houses check their distance every {seconds:0.##} s");
        }
    }
}
