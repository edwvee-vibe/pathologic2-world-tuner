using System;
using BepInEx.Configuration;
using Engine.Source.Commons;
using Engine.Source.Settings;
using UnityEngine;
using UnityEngine.PostProcessing;

namespace Pathologic2WorldTuner
{
    /// <summary>Tab "Free Camera & Image": free camera, depth of field, post-processing.</summary>
    internal static partial class Menu
    {
        private static readonly string[] DofModes = { "Off", "Free camera only", "Always" };
        private static readonly string[] Kernels = { "Small", "Medium", "Large", "Very large" };

        private static void DrawPhoto()
        {
            // --- free camera
            GUILayout.Label("Free camera", headerStyle);
            GUILayout.BeginHorizontal();
            bool on = FreeCamera.Active;
            if (GUILayout.Button(on ? "Free camera: ON" : "Free camera: off", on ? selectedButton : GUI.skin.button, GUILayout.Width(170)))
            {
                FreeCamera.Set(!on);
            }
            GUILayout.Label("  Key", GUILayout.Width(40));
            bool waiting = waitingFor == FreeCamera.Key;
            if (GUILayout.Button(waiting ? "press a key... (Esc: cancel)" : FreeCamera.Key.Value.ToString(),
                    waiting ? selectedButton : GUI.skin.button, GUILayout.Width(200)))
            {
                waitingFor = waiting ? null : FreeCamera.Key;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            PlainToggle(FreeCamera.FreezeWorld, " Stop the world");
            PlainToggle(FreeCamera.HideUi, " Hide the interface");
            PlainToggle(FreeCamera.HidePlayer, " Hide the player's hands");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            PlainToggle(FreeCamera.LoadAroundCamera, " Load houses around the camera");
            PlainToggle(FrozenShaderTime.Enabled, " Keep dithering moving while stopped");
            GUILayout.EndHorizontal();
            if (FreeCamera.LoadAroundCamera.Value)
            {
                GUILayout.Label("Houses and districts load where the camera is. Flying fast loads and unloads many houses: hitches and " +
                                "more memory; after leaving, the player's area loads again.", warnStyle);
            }

            DefaultRow("Speed", FreeCamera.MoveSpeed, 0.2f, 50f, 4f, v => $"{v:0.0} m/s" + (on ? $" (now {FreeCamera.SpeedNow:0.0})" : ""), true);
            float gameFov = 0f;
            try
            {
                gameFov = InstanceByRequest<GraphicsGameSettings>.Instance.FieldOfView.Value;
            }
            catch
            {
                // main menu
            }
            FloatRow("Field of view", FreeCamera.Fov, FreeCamera.MinFov, FreeCamera.MaxFov, 1f, Mode.Absolute, gameFov, v => $"{v:0} deg");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Roll", GUILayout.Width(LabelWidth));
            float roll = GUILayout.HorizontalSlider(FreeCamera.Roll, -90f, 90f, GUILayout.Width(SliderWidth));
            if (Math.Abs(roll - FreeCamera.Roll) > 0.01f)
            {
                FreeCamera.Roll = Mathf.Round(roll);
            }
            GUILayout.Label($"  {FreeCamera.Roll:0} deg", valueStyle, GUILayout.Width(ValueWidth));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Level", GUILayout.Width(ButtonWidth)))
            {
                FreeCamera.Roll = 0f;
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("WASD: move, Q/E: up/down, mouse: look, Shift: x4, Ctrl: x0.25, wheel: speed, +/-: zoom, Z/C: roll, " +
                            "R: level, H: interface, Esc: leave. Close this window to fly. Only from normal play (not in dialogs or cutscenes); " +
                            "a cutscene or a dialog switches it off. Houses load around the player unless the switch above is on. " +
                            "Screenshots: Steam's F12.", hintStyle);

            // --- depth of field
            GUILayout.Space(10);
            GUILayout.Label("Depth of field", headerStyle);
            int dof = (int)ImageTweaks.Dof.Value;
            int newDof = GUILayout.Toolbar(dof, DofModes);
            if (newDof != dof)
            {
                ImageTweaks.Dof.Value = (DofMode)newDof;
                Plugin.RequestSave();
            }
            bool oldEnabled = GUI.enabled;
            GUI.enabled = ImageTweaks.Dof.Value != DofMode.Off;
            PlainToggle(ImageTweaks.DofAutoFocus, ImageTweaks.DofAutoFocus.Value && ImageTweaks.DofActive
                ? $" Auto focus on the centre of the screen (now {ImageTweaks.FocusNow:0.0} m)"
                : " Auto focus on the centre of the screen");
            GUI.enabled = ImageTweaks.Dof.Value != DofMode.Off && !ImageTweaks.DofAutoFocus.Value;
            DefaultRow("Focus distance", ImageTweaks.DofDistance, 0.1f, 1000f, 10f, v => $"{v:0.0} m", true);
            GUI.enabled = ImageTweaks.Dof.Value != DofMode.Off;
            DefaultRow("Aperture", ImageTweaks.DofAperture, 0.5f, 32f, 2.8f, v => $"f/{v:0.0}", true);
            DefaultRow("Focal length", ImageTweaks.DofFocalLength, 10f, 300f, 50f, v => $"{v:0} mm", true);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Blur size", GUILayout.Width(LabelWidth));
            int kernel = (int)ImageTweaks.DofKernel.Value;
            int newKernel = GUILayout.Toolbar(kernel, Kernels, GUILayout.Width(SliderWidth + ValueWidth));
            if (newKernel != kernel)
            {
                ImageTweaks.DofKernel.Value = (DepthOfFieldModel.KernelSize)newKernel;
                Plugin.RequestSave();
            }
            GUILayout.EndHorizontal();
            GUI.enabled = oldEnabled;
            GUILayout.Label("Lower aperture and longer focal length blur more. Auto focus measures the distance with one ray per frame " +
                            "(only while depth of field is on); things without colliders, like smoke or leaves, are looked through.", hintStyle);

            // --- post-processing
            GUILayout.Space(10);
            DrawPresets(Presets.Image);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Image (post-processing)", headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("All to game values", GUILayout.Width(170)))
            {
                pending.Clear();
                ImageTweaks.ResetAll();
                Plugin.RequestSave();
            }
            GUILayout.EndHorizontal();

            ImageTweaks.ReadGameValuesForWindow();
            ImageTweaks.GameValues g = ImageTweaks.Game;
            if (!g.Valid)
            {
                GUILayout.Label("The game camera's post-processing is not found yet (main menu or loading).", warnStyle);
                return;
            }

            GUI.enabled = g.ColorGrading;
            FloatRow("Exposure", ImageTweaks.Exposure, -3f, 3f, 0.05f, Mode.Plain, 0f, v => $"{v:+0.00;-0.00;0} EV");
            FloatRow("Contrast", ImageTweaks.Contrast, 0.3f, 2f, 0.01f, Mode.Scale, 1f, v => $"x{v:0.00}");
            FloatRow("Saturation", ImageTweaks.Saturation, 0f, 2f, 0.01f, Mode.Scale, 1f, v => $"x{v:0.00}");
            FloatRow("Temperature", ImageTweaks.Temperature, -60f, 60f, 1f, Mode.Plain, 0f, v => $"{v:+0;-0;0}");
            FloatRow("Tint", ImageTweaks.Tint, -60f, 60f, 1f, Mode.Plain, 0f, v => $"{v:+0;-0;0}");
            GUI.enabled = g.EffectsMixer;
            FloatRow("Screen effects", ImageTweaks.EffectsStrength, 0f, 2f, 0.05f, Mode.Scale, 1f, v => $"x{v:0.00}");
            GUI.enabled = oldEnabled;
            GUILayout.Label($"      Game now: exposure {g.Exposure:0.00}, contrast {g.Contrast:0.00}, saturation {g.Saturation:0.00}, " +
                            $"temperature {g.Temperature:0.#}, tint {g.Tint:0.#}" +
                            (g.ColorGrading ? "" : " (colour grading is off in the game)"), hintStyle);

            ScaleRow("Bloom", ImageTweaks.Bloom, 4f, g.Bloom, g.BloomIntensity);
            ScaleRow("Vignette", ImageTweaks.Vignette, 2f, g.Vignette, g.VignetteIntensity);
            ScaleRow("Chromatic aberration", ImageTweaks.Chromatic, 4f, g.Chromatic, g.ChromaticIntensity);
            ScaleRow("Film grain", ImageTweaks.Grain, 4f, g.Grain, g.GrainIntensity);
            GUI.enabled = g.EyeAdaptation || ImageTweaks.NoAutoExposure.Value;
            PlainToggle(ImageTweaks.NoAutoExposure, g.EyeAdaptation || ImageTweaks.NoAutoExposure.Value
                ? " Switch off auto exposure (eye adaptation)"
                : " Switch off auto exposure (the game does not use it now)");
            GUI.enabled = oldEnabled;

            GUILayout.Label("Applied after the game's own effects. Screen effects scales how much the player's state (tiredness, " +
                            "infection, pain, focus) shifts colour and chromatic aberration; 0 keeps the picture neutral. Vignette also " +
                            "scales the red vignette of damage. With every slider at Game the mod does not touch post-processing at all.",
                hintStyle);
        }

        private static void ScaleRow(string label, ConfigEntry<float> entry, float max, bool inGame, float gameValue)
        {
            bool oldEnabled = GUI.enabled;
            GUI.enabled = inGame;
            FloatRow(label, entry, 0f, max, 0.05f, Mode.Scale, 1f,
                v => inGame ? $"x{v:0.00} = {gameValue * v:0.00}" : "off in the game");
            GUI.enabled = oldEnabled;
        }

        private static void PlainToggle(ConfigEntry<bool> entry, string label)
        {
            bool v = GUILayout.Toggle(entry.Value, label);
            if (v != entry.Value)
            {
                entry.Value = v;
                Plugin.RequestSave();
            }
        }

        /// <summary>A slider with a "Default" button, for the mod's own settings that have no game value.</summary>
        private static void DefaultRow(string label, ConfigEntry<float> entry, float min, float max, float def, Func<float, string> format, bool log)
        {
            float value = Mathf.Clamp(entry.Value, min, max);
            bool isDefault = Math.Abs(entry.Value - def) < 0.0001f;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(LabelWidth));
            float t = log ? Mathf.InverseLerp(Mathf.Log(min), Mathf.Log(max), Mathf.Log(value)) : Mathf.InverseLerp(min, max, value);
            float newT = GUILayout.HorizontalSlider(t, 0f, 1f, GUILayout.Width(SliderWidth));
            GUILayout.Label("  " + format(value), valueStyle, GUILayout.Width(ValueWidth));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Default", isDefault ? selectedButton : GUI.skin.button, GUILayout.Width(ButtonWidth)) && !isDefault)
            {
                entry.Value = def;
                Plugin.RequestSave();
            }
            GUILayout.EndHorizontal();
            if (Math.Abs(newT - t) > 0.0001f)
            {
                float v = log ? Mathf.Exp(Mathf.Lerp(Mathf.Log(min), Mathf.Log(max), newT)) : Mathf.Lerp(min, max, newT);
                float step = v < 1f ? 0.05f : v < 10f ? 0.1f : 1f;
                entry.Value = Mathf.Clamp(Mathf.Round(v / step) * step, min, max);
                Plugin.RequestSave();
            }
        }
    }
}
