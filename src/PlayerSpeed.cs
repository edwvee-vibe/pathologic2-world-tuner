using System;
using BepInEx.Configuration;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// Player movement speeds. They live in the InputSettingsData ScriptableObject (the game even has debug console commands
    /// for them: walk_speed, run_speed, ...); only PlayerMoveController reads them, so NPCs are not affected.
    /// Run speed is additionally multiplied by the game's RunModifier (tiredness and other states), which is left alone.
    /// The asset is edited in memory from the stored originals; -1 in the config = as in the game.
    /// </summary>
    internal static class PlayerSpeed
    {
        internal static ConfigEntry<float> Walk, WalkBack, Run, RunBack, Sneak, Jump, Gravity, Fight;

        private static InputSettingsData data;
        private static float[] original;
        private static bool dirty = true;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string s = "Player";
            Walk = Bind(cfg, s, "WalkSpeed", -1f, 30f, "Walking speed, m/s. -1 = as in the game.");
            WalkBack = Bind(cfg, s, "WalkBackSpeed", -1f, 30f, "Walking backwards, m/s. -1 = as in the game.");
            Run = Bind(cfg, s, "RunSpeed", -1f, 30f, "Running speed, m/s (the game multiplies it by its own run modifier, e.g. when tired). -1 = as in the game.");
            RunBack = Bind(cfg, s, "RunBackSpeed", -1f, 30f, "Running backwards, m/s. -1 = as in the game.");
            Sneak = Bind(cfg, s, "SneakSpeed", -1f, 30f, "Sneaking speed, m/s. -1 = as in the game.");
            Jump = Bind(cfg, s, "JumpSpeed", -1f, 30f, "Initial upward speed of a jump, m/s (higher = higher jump). -1 = as in the game.");
            Gravity = Bind(cfg, s, "GravityMultiplier", -1f, 10f, "Gravity multiplier for the player (lower = floatier jumps and falls). -1 = as in the game.");
            Fight = cfg.Bind(s, "FightSpeedScale", 1f, new ConfigDescription(
                "Multiplier of all movement speeds with a weapon or fists out (forward, backward, strafe; normal, sneaking, running). 1 = as in the game.",
                new AcceptableValueRange<float>(0.25f, 4f)));
            Fight.SettingChanged += (o, e) => { dirty = true; Apply(); };
        }

        private static ConfigEntry<float> Bind(ConfigFile cfg, string section, string key, float def, float max, string description)
        {
            ConfigEntry<float> e = cfg.Bind(section, key, def, new ConfigDescription(description, new AcceptableValueRange<float>(-1f, max)));
            e.SettingChanged += (o, args) => { dirty = true; Apply(); };
            return e;
        }

        /// <summary>The asset is loaded on first access through the game's asset service, so only touch it once the game runs.</summary>
        private static bool Ready()
        {
            if (data != null)
            {
                return true;
            }
            if (GameCamera.Instance == null)
            {
                return false;
            }
            data = ScriptableObjectInstance<InputSettingsData>.Instance;
            if (data == null)
            {
                return false;
            }
            original = Read(data);
            Plugin.Log.LogInfo($"Player speeds: walk {data.WalkSpeed}, back {data.WalkBackSpeed}, run {data.RunSpeed}, run back {data.RunBackSpeed}, " +
                               $"sneak {data.SneakSpeed}, jump {data.JumpSpeed}, gravity x{data.GravityMultiplier}; fight {data.FightForwardSpeed}/" +
                               $"{data.FightBackwardSpeed}/{data.FightStrafeSpeed}, sneak {data.FightForwardSneakSpeed}/{data.FightBackwardSneakSpeed}/" +
                               $"{data.FightStrafeSneakSpeed}, run {data.FightForwardRunSpeed}/{data.FightBackwardRunSpeed}/{data.FightStrafeRunSpeed}");
            return true;
        }

        // Order: walk, walk back, run, run back, sneak, jump, gravity, then the 9 fight speeds.
        private static float[] Read(InputSettingsData d)
        {
            return new[]
            {
                d.WalkSpeed, d.WalkBackSpeed, d.RunSpeed, d.RunBackSpeed, d.SneakSpeed, d.JumpSpeed, d.GravityMultiplier,
                d.FightForwardSpeed, d.FightBackwardSpeed, d.FightStrafeSpeed,
                d.FightForwardSneakSpeed, d.FightBackwardSneakSpeed, d.FightStrafeSneakSpeed,
                d.FightForwardRunSpeed, d.FightBackwardRunSpeed, d.FightStrafeRunSpeed
            };
        }

        private static bool touched;

        private static bool AllDefault =>
            Walk.Value < 0f && WalkBack.Value < 0f && Run.Value < 0f && RunBack.Value < 0f && Sneak.Value < 0f &&
            Jump.Value < 0f && Gravity.Value < 0f && Math.Abs(Fight.Value - 1f) < 0.0001f;

        internal static void Tick()
        {
            if (dirty)
            {
                Apply();
            }
        }

        private static void Apply()
        {
            if (AllDefault && !touched)
            {
                dirty = false; // as in the game and never changed: do not even load the asset
                return;
            }
            if (!Ready())
            {
                return;
            }
            dirty = false;
            touched = !AllDefault;
            data.WalkSpeed = Value(Walk, 0);
            data.WalkBackSpeed = Value(WalkBack, 1);
            data.RunSpeed = Value(Run, 2);
            data.RunBackSpeed = Value(RunBack, 3);
            data.SneakSpeed = Value(Sneak, 4);
            data.JumpSpeed = Value(Jump, 5);
            data.GravityMultiplier = Value(Gravity, 6);
            float k = Fight.Value;
            data.FightForwardSpeed = original[7] * k;
            data.FightBackwardSpeed = original[8] * k;
            data.FightStrafeSpeed = original[9] * k;
            data.FightForwardSneakSpeed = original[10] * k;
            data.FightBackwardSneakSpeed = original[11] * k;
            data.FightStrafeSneakSpeed = original[12] * k;
            data.FightForwardRunSpeed = original[13] * k;
            data.FightBackwardRunSpeed = original[14] * k;
            data.FightStrafeRunSpeed = original[15] * k;
        }

        private static float Value(ConfigEntry<float> e, int index)
        {
            return e.Value >= 0f ? e.Value : original[index];
        }

        /// <summary>Game value for the window (a typical default before the asset is read).</summary>
        internal static float Game(int index, float fallback)
        {
            Ready();
            return original != null ? original[index] : fallback;
        }

        internal static void ResetAll()
        {
            Walk.Value = -1f; WalkBack.Value = -1f; Run.Value = -1f; RunBack.Value = -1f; Sneak.Value = -1f;
            Jump.Value = -1f; Gravity.Value = -1f; Fight.Value = 1f;
        }

        internal static void ScaleAll(float k)
        {
            if (!Ready())
            {
                return;
            }
            Walk.Value = Round(original[0] * k);
            WalkBack.Value = Round(original[1] * k);
            Run.Value = Round(original[2] * k);
            RunBack.Value = Round(original[3] * k);
            Sneak.Value = Round(original[4] * k);
            Fight.Value = k;
        }

        private static float Round(float v) => (float)Math.Round(v, 2);
    }
}
