using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// At game speed 0 Unity's shader time (_Time = time since level load, scaled) stands still, so dithered hair (Interleaved
    /// Gradient Noise offset by _Time) freezes into a fixed checkerboard that TAA/DLSS cannot average. While the world is
    /// stopped, the game camera gets a command buffer that sets _Time to the frozen time plus a tiny offset that changes every
    /// frame: frac(n * 0.618) / 52.98 s, under 19 ms. The hair noise is frac(52.98 * frac(... + t)), so its threshold moves by
    /// frac(n * 0.618) per frame (a golden-ratio sequence), while everything else animated by _Time moves by under 19 ms of
    /// time, which is not visible. Nothing runs while the game speed is not 0.
    /// </summary>
    internal static class FrozenShaderTime
    {
        internal static ConfigEntry<bool> Enabled;

        private static readonly int TimeId = Shader.PropertyToID("_Time");
        private static readonly CameraEvent[] Events = { CameraEvent.BeforeGBuffer, CameraEvent.BeforeForwardOpaque, CameraEvent.BeforeForwardAlpha };
        private static CommandBuffer cmd;
        private static Camera attached;
        private static int frame;

        internal static bool Attached => attached != null;

        internal static void BindConfig(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Photo", "MoveDitherWhenStopped", true,
                "While the world is stopped (game speed 0), keep dithered hair and other dithering moving so TAA/DLSS can smooth it. " +
                "Other shader animations shift by less than 0.02 s, which is not visible.");
        }

        /// <summary>Every frame while the game speed is 0 or the buffer is attached.</summary>
        internal static void FrameTick()
        {
            Camera cam = null;
            if (Enabled.Value && Time.timeScale == 0f && GameCamera.Instance != null)
            {
                cam = GameCamera.Instance.Camera;
            }
            if (cam != attached)
            {
                Detach();
                if (cam != null)
                {
                    Attach(cam);
                }
            }
            if (attached == null)
            {
                return;
            }
            frame++;
            float offset = (frame * 0.618034f) % 1f / 52.982918f;
            float t = Time.timeSinceLevelLoad + offset;
            var v = new Vector4(t / 20f, t, t * 2f, t * 3f);
            cmd.Clear();
            cmd.SetGlobalVector(TimeId, v);
        }

        private static void Attach(Camera cam)
        {
            cmd = cmd ?? new CommandBuffer { name = "World Tuner: shader time while stopped" };
            foreach (CameraEvent e in Events)
            {
                cam.AddCommandBuffer(e, cmd);
            }
            attached = cam;
            Plugin.Log.LogInfo("Shader time: moving dither while the world is stopped");
        }

        private static void Detach()
        {
            if (attached == null)
            {
                return;
            }
            try
            {
                foreach (CameraEvent e in Events)
                {
                    attached.RemoveCommandBuffer(e, cmd);
                }
            }
            catch (Exception)
            {
                // the camera was destroyed
            }
            attached = null;
        }
    }
}
