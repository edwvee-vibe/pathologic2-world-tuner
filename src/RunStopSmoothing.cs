using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using Engine.Behaviours.Components;
using HarmonyLib;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// The game sets the player's horizontal speed anew on every physics step (PlayerMoveController.UpdateCharacterPhysics),
    /// with no acceleration. When running stops (Shift released, or stamina reaching exactly 0, which turns running off in
    /// ControllerComponent), the speed drops from run to walk (6 to 3.5 m/s) within one physics step and the camera, which
    /// follows the player with only 0.03 s of smoothing, jerks. This replaces the step's CharacterController.Move call:
    /// while the player keeps moving in about the same direction and the wanted speed is lower than the last one, the speed
    /// goes down over the set time instead. Stopping, turning, speeding up and the vertical part (jumps, gravity) are as
    /// in the game. The patch is installed only while the setting is above 0.
    /// </summary>
    internal static class RunStopSmoothing
    {
        internal static ConfigEntry<float> Seconds;

        private const float MinCos = 0.9f;   // ~25 degrees: a sharper turn is not smoothed
        private static Harmony harmony;
        private static bool patched;
        private static Vector3 lastVelocity; // horizontal velocity applied on the last physics step
        private static float lastTime = -1f;

        internal static void BindConfig(ConfigFile cfg)
        {
            Seconds = cfg.Bind("Player", "RunStopSmoothing", 0.25f, new ConfigDescription(
                "When running stops (Shift released or stamina run out) the game drops the speed from run to walk at once, which " +
                "jerks the camera. Seconds over which the speed goes down instead (from run to walk). 0 = as in the game.",
                new AcceptableValueRange<float>(0f, 1f)));
            Seconds.SettingChanged += (s, e) => Refresh();
        }

        internal static void Refresh()
        {
            bool need = Seconds.Value > 0f;
            try
            {
                harmony = harmony ?? new Harmony(Plugin.Guid + ".runstop");
                if (need && !patched)
                {
                    harmony.PatchAll(typeof(RunStopPatches));
                    patched = true;
                }
                else if (!need && patched)
                {
                    harmony.UnpatchSelf();
                    patched = false;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("runstop", "Run stop smoothing: " + e);
            }
        }

        /// <summary>Called instead of characterController.Move(moveDir * deltaTime) in the player's physics step.</summary>
        internal static CollisionFlags Move(CharacterController cc, Vector3 motion)
        {
            float dt = Time.fixedDeltaTime;
            if (dt > 0f)
            {
                try
                {
                    motion = Smooth(motion, dt);
                }
                catch (Exception e)
                {
                    Plugin.LogOnce("runstop-move", "Run stop smoothing: " + e);
                }
            }
            return cc.Move(motion);
        }

        private static Vector3 Smooth(Vector3 motion, float dt)
        {
            var wanted = new Vector3(motion.x / dt, 0f, motion.z / dt);
            float now = Time.fixedTime;
            bool continuous = lastTime >= 0f && now - lastTime <= dt * 1.5f; // otherwise the player was not controlled meanwhile
            lastTime = now;

            float wantedSpeed = wanted.magnitude;
            float lastSpeed = lastVelocity.magnitude;
            Vector3 result = wanted;
            if (continuous && wantedSpeed > 0.05f && wantedSpeed < lastSpeed &&
                Vector3.Dot(wanted / wantedSpeed, lastVelocity / lastSpeed) >= MinCos)
            {
                // From run (~6 m/s) to walk (~3.5 m/s) in the set time: about 2.5 m/s over Seconds.
                float rate = 2.5f / Mathf.Max(0.01f, Seconds.Value);
                float speed = Mathf.Max(wantedSpeed, lastSpeed - rate * dt);
                result = wanted / wantedSpeed * speed;
            }
            lastVelocity = result;
            return new Vector3(result.x * dt, motion.y, result.z * dt);
        }
    }

    /// <summary>Installed while RunStopSmoothing is above 0.</summary>
    [HarmonyPatch]
    internal static class RunStopPatches
    {
        [HarmonyPatch(typeof(PlayerMoveController), "UpdateCharacterPhysics")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo move = AccessTools.Method(typeof(CharacterController), nameof(CharacterController.Move), new[] { typeof(Vector3) });
            MethodInfo replacement = AccessTools.Method(typeof(RunStopSmoothing), nameof(RunStopSmoothing.Move));
            int replaced = 0;
            foreach (CodeInstruction ci in code)
            {
                if (ci.Calls(move))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                    replaced++;
                }
                yield return ci;
            }
            if (replaced != 1)
            {
                Plugin.LogOnce("runstop-il", $"Run stop smoothing: expected one CharacterController.Move call, found {replaced} (game updated?)");
            }
        }
    }
}
