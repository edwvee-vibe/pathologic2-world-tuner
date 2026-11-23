using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.PostProcessing;

namespace Pathologic2WorldTuner
{
    internal enum DofMode
    {
        Off,
        FreeCameraOnly,
        Always
    }

    /// <summary>
    /// Post-processing (Post Processing Stack v1) of the game camera. Every LateUpdate PostProcessingOverrideController rebuilds
    /// its "_Override" profile: the base profile's models, then the overrides (graphics settings and the blueprint effects mixer,
    /// which writes the colour grading: saturation 1 + effects, contrast 0.9 + effects, post exposure 1 + effects, temperature,
    /// tint; chromatic aberration max(effects, 0.04)). A postfix on that LateUpdate swaps in the mod's own model instances with
    /// adjusted settings, so nothing accumulates and the game's values stay untouched. The postfix is installed only while some
    /// setting differs from the game's; with everything at "Game" nothing runs.
    /// </summary>
    internal static class ImageTweaks
    {
        // Neutral values of the effects mixer graph (sharedassets1.assets): what the colour grading is without any effect.
        internal const float NeutralSaturation = 1f, NeutralContrast = 0.9f, NeutralExposure = 1f, NeutralChromatic = 0.04f;

        internal static ConfigEntry<float> TaaSharpness, Exposure, Contrast, Saturation, Temperature, Tint, EffectsStrength,
            Bloom, Vignette, Chromatic, Grain;
        internal static ConfigEntry<bool> NoAutoExposure;
        internal static ConfigEntry<DofMode> Dof;
        internal static ConfigEntry<bool> DofAutoFocus;
        internal static ConfigEntry<float> DofDistance, DofAperture, DofFocalLength;
        internal static ConfigEntry<DepthOfFieldModel.KernelSize> DofKernel;

        private static Harmony harmony;
        private static bool patched;

        private static readonly AntialiasingModel aa = new AntialiasingModel();
        private static readonly ColorGradingModel cg = new ColorGradingModel();
        private static readonly BloomModel bloom = new BloomModel();
        private static readonly VignetteModel vignette = new VignetteModel();
        private static readonly ChromaticAberrationModel chromatic = new ChromaticAberrationModel();
        private static readonly GrainModel grain = new GrainModel();
        private static readonly EyeAdaptationModel eye = new EyeAdaptationModel();
        private static readonly DepthOfFieldModel dof = new DepthOfFieldModel();
        private static readonly MotionBlurModel motionBlur = new MotionBlurModel();
        private static MotionTrail motionTrail;

        private static PostProcessingOverrideController gameController;
        private static float focus = 10f;
        private static int lastFrame = -1;

        /// <summary>The game's values in the last frame (before the mod's changes), for the window.</summary>
        internal struct GameValues
        {
            public bool Valid, Taa, ColorGrading, EffectsMixer, Bloom, Vignette, Chromatic, Grain, EyeAdaptation, Dof;
            public float Sharpen, Saturation, Contrast, Exposure, Temperature, Tint, BloomIntensity, VignetteIntensity,
                ChromaticIntensity, GrainIntensity;
        }

        internal static GameValues Game;
        internal static float FocusNow => focus;
        internal static bool Patched => patched;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string s = "Image";
            TaaSharpness = Bind(cfg, s, "TaaSharpness", -1f, -1f, 3f, "Sharpening of the temporal anti-aliasing (TAA). -1 = the game's.");
            Exposure = Bind(cfg, s, "Exposure", 0f, -3f, 3f, "Added to the post exposure, EV. 0 = as in the game.");
            Contrast = Bind(cfg, s, "Contrast", 1f, 0.3f, 2f, "Contrast multiplier. 1 = as in the game.");
            Saturation = Bind(cfg, s, "Saturation", 1f, 0f, 2f, "Saturation multiplier. 1 = as in the game.");
            Temperature = Bind(cfg, s, "Temperature", 0f, -60f, 60f, "Added to the white balance temperature. 0 = as in the game.");
            Tint = Bind(cfg, s, "Tint", 0f, -60f, 60f, "Added to the white balance tint. 0 = as in the game.");
            EffectsStrength = Bind(cfg, s, "ScreenEffectsStrength", 1f, 0f, 2f,
                "Strength of the screen effects of the player's state (tiredness, infection, pain...) on colour and chromatic aberration. 1 = as in the game, 0 = none.");
            Bloom = Bind(cfg, s, "Bloom", 1f, 0f, 4f, "Bloom intensity multiplier. 1 = as in the game.");
            Vignette = Bind(cfg, s, "Vignette", 1f, 0f, 2f, "Vignette intensity multiplier (also the red vignette of damage). 1 = as in the game.");
            Chromatic = Bind(cfg, s, "ChromaticAberration", 1f, 0f, 4f, "Chromatic aberration multiplier. 1 = as in the game.");
            Grain = Bind(cfg, s, "Grain", 1f, 0f, 4f, "Film grain multiplier. 1 = as in the game.");
            NoAutoExposure = cfg.Bind(s, "DisableAutoExposure", false, "Switch off the eye adaptation (auto exposure), if the game uses it.");
            Dof = cfg.Bind("Photo", "DepthOfField", DofMode.Off, "Depth of field: Off, only in the free camera, or always.");
            DofAutoFocus = cfg.Bind("Photo", "DofAutoFocus", true, "Focus on what is in the centre of the screen.");
            DofDistance = Bind(cfg, "Photo", "DofDistance", 10f, 0.1f, 1000f, "Focus distance (m) when auto focus is off.");
            DofAperture = Bind(cfg, "Photo", "DofAperture", 2.8f, 0.5f, 32f, "Aperture (f-number): lower = more blur.");
            DofFocalLength = Bind(cfg, "Photo", "DofFocalLength", 50f, 10f, 300f, "Focal length (mm): higher = more blur.");
            DofKernel = cfg.Bind("Photo", "DofBlurSize", DepthOfFieldModel.KernelSize.Medium, "Size of the blur kernel (bokeh).");

            foreach (ConfigEntryBase e in new ConfigEntryBase[] { TaaSharpness, Exposure, Contrast, Saturation, Temperature, Tint,
                         EffectsStrength, Bloom, Vignette, Chromatic, Grain, NoAutoExposure, Dof })
            {
                switch (e)
                {
                    case ConfigEntry<float> f: f.SettingChanged += (o, a) => Refresh(); break;
                    case ConfigEntry<bool> b: b.SettingChanged += (o, a) => Refresh(); break;
                    case ConfigEntry<DofMode> d: d.SettingChanged += (o, a) => Refresh(); break;
                }
            }
            Refresh();
        }

        private static ConfigEntry<float> Bind(ConfigFile cfg, string section, string key, float def, float min, float max, string desc)
        {
            return cfg.Bind(section, key, def, new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));
        }

        private static bool Changed(ConfigEntry<float> e, float game) => Math.Abs(e.Value - game) > 0.0001f;

        internal static bool ColorChanged => Changed(Exposure, 0f) || Changed(Contrast, 1f) || Changed(Saturation, 1f) ||
                                             Changed(Temperature, 0f) || Changed(Tint, 0f) || Changed(EffectsStrength, 1f);

        internal static bool DofActive => Dof.Value == DofMode.Always || (Dof.Value == DofMode.FreeCameraOnly && FreeCamera.Active);

        internal static bool AnyChanged => TaaSharpness.Value >= 0f || ColorChanged || Changed(Bloom, 1f) || Changed(Vignette, 1f) ||
                                           Changed(Chromatic, 1f) || Changed(Grain, 1f) || NoAutoExposure.Value || DofActive ||
                                           TimeTweaks.Speed == 0f;

        /// <summary>Install or remove the postfix after a setting changed or the free camera was switched.</summary>
        internal static void Refresh()
        {
            if (TaaSharpness == null || Dof == null)
            {
                return; // still binding
            }
            bool need = AnyChanged;
            if (need == patched)
            {
                return;
            }
            try
            {
                harmony = harmony ?? new Harmony(Plugin.Guid + ".image");
                if (need)
                {
                    harmony.PatchAll(typeof(ImagePatches));
                    patched = true;
                    Plugin.Log.LogInfo("Image: post-processing changes on");
                }
                else
                {
                    harmony.UnpatchSelf();
                    patched = false;
                    Game.Valid = false;
                    ReleaseLut();
                    Plugin.Log.LogInfo("Image: back to the game's post-processing");
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("imagepatch", "Image: failed to patch: " + e);
            }
        }

        private static void ReleaseLut()
        {
            if (cg.bakedLut != null)
            {
                UnityEngine.Object.Destroy(cg.bakedLut); // a destroyed LUT counts as invalid and is created again when needed
            }
        }

        internal static void ResetAll()
        {
            TaaSharpness.Value = -1f;
            Exposure.Value = 0f;
            Contrast.Value = 1f;
            Saturation.Value = 1f;
            Temperature.Value = 0f;
            Tint.Value = 0f;
            EffectsStrength.Value = 1f;
            Bloom.Value = 1f;
            Vignette.Value = 1f;
            Chromatic.Value = 1f;
            Grain.Value = 1f;
            NoAutoExposure.Value = false;
        }

        /// <summary>The game camera's controller only (there may be others, e.g. on capture cameras).</summary>
        private static bool IsGameController(PostProcessingOverrideController c)
        {
            if (gameController != null)
            {
                return c == gameController;
            }
            GameCamera gc = GameCamera.Instance;
            if (gc == null || gc.Camera == null || c.gameObject != gc.Camera.gameObject)
            {
                return false;
            }
            gameController = c;
            return true;
        }

        /// <summary>The window, while the mod changes nothing: read the game's profile directly.</summary>
        internal static void ReadGameValuesForWindow()
        {
            if (patched)
            {
                return; // filled by the postfix
            }
            GameCamera gc = GameCamera.Instance;
            PostProcessingBehaviour b = gc != null && gc.Camera != null ? gc.Camera.GetComponent<PostProcessingBehaviour>() : null;
            if (b == null || b.profile == null)
            {
                Game.Valid = false;
                return;
            }
            Record(b.profile);
        }

        private static void Record(PostProcessingProfile p)
        {
            Game.Valid = true;
            Game.Taa = p.antialiasing != null && p.antialiasing.enabled && p.antialiasing.settings.method == AntialiasingModel.Method.Taa;
            Game.Sharpen = p.antialiasing != null ? p.antialiasing.settings.taaSettings.sharpen : 0f;
            Game.ColorGrading = p.colorGrading != null && p.colorGrading.enabled;
            if (p.colorGrading != null)
            {
                ColorGradingModel.BasicSettings basic = p.colorGrading.settings.basic;
                Game.Saturation = basic.saturation;
                Game.Contrast = basic.contrast;
                Game.Exposure = basic.postExposure;
                Game.Temperature = basic.temperature;
                Game.Tint = basic.tint;
            }
            GameCamera gc = GameCamera.Instance;
            Game.EffectsMixer = gc != null && gc.GamePostProcessingOverride != null && gc.GamePostProcessingOverride.ColorGrading.Override;
            Game.Bloom = p.bloom != null && p.bloom.enabled;
            Game.BloomIntensity = p.bloom != null ? p.bloom.settings.bloom.intensity : 0f;
            Game.Vignette = p.vignette != null && p.vignette.enabled;
            Game.VignetteIntensity = p.vignette != null ? p.vignette.settings.intensity : 0f;
            Game.Chromatic = p.chromaticAberration != null && p.chromaticAberration.enabled;
            Game.ChromaticIntensity = p.chromaticAberration != null ? p.chromaticAberration.settings.intensity : 0f;
            Game.Grain = p.grain != null && p.grain.enabled;
            Game.GrainIntensity = p.grain != null ? p.grain.settings.intensity : 0f;
            Game.EyeAdaptation = p.eyeAdaptation != null && p.eyeAdaptation.enabled;
            Game.Dof = p.depthOfField != null && p.depthOfField.enabled;
        }

        /// <summary>Postfix of PostProcessingOverrideController.LateUpdate: the "_Override" profile has just been rebuilt.</summary>
        internal static void AfterRebuild(PostProcessingOverrideController c, PostProcessingProfile p)
        {
            if (p == null || !IsGameController(c) || lastFrame == Time.frameCount)
            {
                return;
            }
            lastFrame = Time.frameCount;
            Record(p);

            if (TaaSharpness.Value >= 0f && Game.Taa)
            {
                AntialiasingModel.Settings s = p.antialiasing.settings;
                s.taaSettings.sharpen = TaaSharpness.Value;
                aa.enabled = true;
                aa.settings = s;
                p.antialiasing = aa;
            }

            if (ColorChanged && Game.ColorGrading)
            {
                ColorGradingModel.Settings s = p.colorGrading.settings;
                ColorGradingModel.BasicSettings b = s.basic;
                if (Game.EffectsMixer)
                {
                    float k = EffectsStrength.Value;
                    b.saturation = NeutralSaturation + (b.saturation - NeutralSaturation) * k;
                    b.contrast = NeutralContrast + (b.contrast - NeutralContrast) * k;
                    b.postExposure = NeutralExposure + (b.postExposure - NeutralExposure) * k;
                    b.temperature *= k;
                    b.tint *= k;
                }
                b.saturation = Mathf.Max(0f, b.saturation * Saturation.Value);
                b.contrast = Mathf.Max(0f, b.contrast * Contrast.Value);
                b.postExposure += Exposure.Value;
                b.temperature = Mathf.Clamp(b.temperature + Temperature.Value, -100f, 100f);
                b.tint = Mathf.Clamp(b.tint + Tint.Value, -100f, 100f);
                s.basic = b;
                cg.enabled = true;
                cg.settings = s; // marks the LUT dirty, as the game's own override does every frame
                p.colorGrading = cg;
            }

            if (Changed(Bloom, 1f) && Game.Bloom)
            {
                BloomModel.Settings s = p.bloom.settings;
                s.bloom.intensity *= Bloom.Value;
                bloom.enabled = Bloom.Value > 0f;
                bloom.settings = s;
                p.bloom = bloom;
            }

            if (Changed(Vignette, 1f) && Game.Vignette)
            {
                VignetteModel.Settings s = p.vignette.settings;
                s.intensity *= Vignette.Value;
                vignette.enabled = Vignette.Value > 0f;
                vignette.settings = s;
                p.vignette = vignette;
            }

            bool caEffects = Changed(EffectsStrength, 1f) && Game.EffectsMixer;
            if ((Changed(Chromatic, 1f) || caEffects) && Game.Chromatic)
            {
                ChromaticAberrationModel.Settings s = p.chromaticAberration.settings;
                float v = s.intensity;
                if (caEffects)
                {
                    v = NeutralChromatic + Mathf.Max(0f, v - NeutralChromatic) * EffectsStrength.Value;
                }
                s.intensity = Mathf.Clamp01(v * Chromatic.Value);
                chromatic.enabled = s.intensity > 0f;
                chromatic.settings = s;
                p.chromaticAberration = chromatic;
            }

            if (Changed(Grain, 1f) && Game.Grain)
            {
                GrainModel.Settings s = p.grain.settings;
                s.intensity *= Grain.Value;
                grain.enabled = Grain.Value > 0f;
                grain.settings = s;
                p.grain = grain;
            }

            if (NoAutoExposure.Value && Game.EyeAdaptation)
            {
                eye.settings = p.eyeAdaptation.settings;
                eye.enabled = false;
                p.eyeAdaptation = eye;
            }

            // With the time stopped (game speed 0, free camera) Time.deltaTime is 0: PPS motion blur divides by it
            // (shutterAngle / 360 / (deltaTime * 30)) and smears the whole picture, and the game's MotionTrail (the blur
            // effect of the player's state) stops blending new frames. Both are switched off for as long as the time stands.
            if (Time.timeScale == 0f)
            {
                if (p.motionBlur != null && p.motionBlur.enabled)
                {
                    motionBlur.settings = p.motionBlur.settings;
                    motionBlur.enabled = false;
                    p.motionBlur = motionBlur;
                }
                if (motionTrail == null)
                {
                    motionTrail = c.GetComponent<MotionTrail>();
                }
                if (motionTrail != null && motionTrail.enabled)
                {
                    motionTrail.enabled = false; // the game's BlurEffectNode sets it again every Update
                }
            }

            if (DofActive)
            {
                DepthOfFieldModel.Settings s = Game.Dof ? p.depthOfField.settings : DepthOfFieldModel.Settings.defaultSettings;
                s.focusDistance = DofAutoFocus.Value ? AutoFocus() : DofDistance.Value;
                s.aperture = DofAperture.Value;
                s.focalLength = DofFocalLength.Value;
                s.useCameraFov = false;
                s.kernelSize = DofKernel.Value;
                dof.enabled = true;
                dof.settings = s;
                p.depthOfField = dof;
            }
        }

        // Layers that are not "things to look at": Ignore Raycast, Triggers, Player, Fog Portals, InvisibleWall,
        // Reflection Probe, Regions, TriggerInteract.
        private const int FocusIgnore = (1 << 2) | (1 << 11) | (1 << 18) | (1 << 24) | (1 << 25) | (1 << 28) | (1 << 29) | (1 << 31);

        /// <summary>Distance to what is in the centre of the screen, smoothed (one raycast per frame, only with auto focus).</summary>
        private static float AutoFocus()
        {
            GameCamera gc = GameCamera.Instance;
            if (gc == null || gc.Camera == null)
            {
                return focus;
            }
            Transform t = gc.Camera.transform;
            float target = Physics.Raycast(t.position, t.forward, out RaycastHit hit, gc.Camera.farClipPlane, ~FocusIgnore,
                QueryTriggerInteraction.Ignore)
                ? Mathf.Max(0.1f, hit.distance)
                : Mathf.Min(gc.Camera.farClipPlane, 1000f);
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 8f);
            focus = Mathf.Lerp(focus, target, k);
            return focus;
        }
    }

    /// <summary>Installed only while some image setting differs from the game's (ImageTweaks.Refresh).</summary>
    [HarmonyPatch]
    internal static class ImagePatches
    {
        [HarmonyPatch(typeof(PostProcessingOverrideController), "LateUpdate")]
        [HarmonyPostfix]
        private static void LateUpdatePostfix(PostProcessingOverrideController __instance, PostProcessingProfile ___overrideProfile)
        {
            try
            {
                ImageTweaks.AfterRebuild(__instance, ___overrideProfile);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("image", "Image: " + e);
            }
        }
    }
}
