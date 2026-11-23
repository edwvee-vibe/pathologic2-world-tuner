using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using Engine.Common;
using Engine.Common.Services;
using Engine.Impl.Services;
using Engine.Source.Commons;
using Engine.Source.Services.CameraServices;
using Engine.Source.Settings;
using Engine.Source.Settings.External;
using HarmonyLib;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// Free camera for screenshots. It uses the game's own camera slot for the developers' fly camera
    /// (CameraService.Kind = Fly): while it is active the player is not controlled (PlayerUtility.IsPlayerCanControlling is
    /// false), so the game does not turn the player or run interactions. The game's FlyCameraController is replaced by a
    /// prefix while the free camera is on (it starts looking north, runs on game time and lets WASD reach the player).
    /// Input is read by the plugin before the game's scripts; the game's input is left alone (it does not control the player
    /// in Fly mode), except Esc, which leaves the free camera. NPC look-at IK is kept on while flying (FreeCameraPatches).
    /// No load while off: the patch is installed on entering and removed on leaving, nothing runs per frame otherwise.
    /// </summary>
    internal static class FreeCamera
    {
        internal static ConfigEntry<KeyboardShortcut> Key;
        internal static ConfigEntry<bool> FreezeWorld, HideUi, HidePlayer, LoadAroundCamera;
        internal static ConfigEntry<float> MoveSpeed, Fov;

        internal const float MinFov = 5f, MaxFov = 140f;

        private static bool active;
        private static Harmony harmony;
        private static CameraKindEnum previousKind;
        private static float yaw, pitch;
        internal static float Roll;
        private static float speedScale = 1f;

        private static bool frozeWorld;
        private static float speedBefore;
        private static bool uiHidden;
        private static readonly List<Renderer> hiddenRenderers = new List<Renderer>();

        // Input captured in Plugin.Update, before the game clears it.
        private static Vector2 look;
        private static Vector3 move;
        private static float rollInput, fovInput;

        private static readonly AccessTools.FieldRef<GameCamera, Camera[]> additionalCameras =
            AccessTools.FieldRefAccess<GameCamera, Camera[]>("additionalCameras");

        internal static bool Active => active;
        internal static float SpeedNow => MoveSpeed.Value * speedScale;

        internal static void BindConfig(ConfigFile cfg)
        {
            Key = cfg.Bind("Photo", "FreeCameraKey", new KeyboardShortcut(KeyCode.F9), "Key to switch the free camera on and off.");
            FreezeWorld = cfg.Bind("Photo", "FreezeWorld", true, "Stop the world (game speed 0) while the free camera is on.");
            HideUi = cfg.Bind("Photo", "HideUi", true, "Hide the game's interface while the free camera is on.");
            HidePlayer = cfg.Bind("Photo", "HidePlayer", true, "Hide the player's hands and items while the free camera is on.");
            LoadAroundCamera = cfg.Bind("Photo", "LoadAroundCamera", false,
                "While the free camera is on, load houses and districts around the camera instead of around the player. " +
                "Flying fast loads and unloads many houses: hitches and more memory; leaving the camera loads the player's area again.");
            LoadAroundCamera.SettingChanged += (s, e) => UpdateLoadPatch();
            MoveSpeed = cfg.Bind("Photo", "MoveSpeed", 4f, new ConfigDescription("Free camera speed, m/s (Shift x4, Ctrl x0.25).",
                new AcceptableValueRange<float>(0.1f, 100f)));
            Fov = cfg.Bind("Photo", "Fov", -1f, new ConfigDescription("Field of view of the free camera, degrees. -1 = the game's.",
                new AcceptableValueRange<float>(-1f, MaxFov)));
        }

        internal static void Toggle()
        {
            Set(!active);
        }

        internal static void Set(bool on)
        {
            if (on == active)
            {
                return;
            }
            if (on)
            {
                Enter();
            }
            else
            {
                Leave("Free camera off");
            }
        }

        private static void Enter()
        {
            CameraService cs = ServiceLocator.GetService<CameraService>();
            GameCamera gc = GameCamera.Instance;
            if (cs == null || gc == null || gc.CameraTransform == null)
            {
                Plugin.ShowMessage("Free camera: no game camera (main menu or loading)");
                return;
            }
            CameraKindEnum kind;
            try
            {
                kind = cs.Kind;
            }
            catch
            {
                Plugin.ShowMessage("Free camera: the camera is not ready yet");
                return;
            }
            // Only from normal play: dialogs, trade and cutscenes have their own camera rigs, which must not be shut down.
            if (kind != CameraKindEnum.FirstPerson_Controlling && kind != CameraKindEnum.FirstPerson_Controlling2)
            {
                Plugin.ShowMessage($"Free camera: not available now (camera: {kind})");
                return;
            }
            try
            {
                harmony = harmony ?? new Harmony(Plugin.Guid + ".freecamera");
                harmony.PatchAll(typeof(FreeCameraPatches));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Free camera: failed to patch: " + e);
                harmony?.UnpatchSelf();
                return;
            }

            Transform t = gc.CameraTransform;
            Vector3 euler = t.rotation.eulerAngles;
            yaw = euler.y;
            pitch = Mathf.DeltaAngle(0f, -euler.x);
            Roll = 0f;
            speedScale = 1f;
            look = Vector2.zero;
            move = Vector3.zero;
            rollInput = fovInput = 0f;

            previousKind = kind;
            active = true;
            cs.Kind = CameraKindEnum.Fly;
            UpdateLoadPatch();

            if (FreezeWorld.Value)
            {
                speedBefore = TimeTweaks.Speed;
                TimeTweaks.SetSpeed(0f);
                frozeWorld = true;
            }
            if (HideUi.Value)
            {
                SetUiHidden(true);
            }
            if (HidePlayer.Value)
            {
                SetPlayerHidden(true);
            }
            ImageTweaks.Refresh();
            Plugin.ShowMessage($"Free camera on ({Key.Value}: off). WASD, Q/E up/down, Shift/Ctrl speed, wheel: speed, Z/C: roll, R: level");
        }

        private static void Leave(string message)
        {
            active = false;
            try
            {
                CameraService cs = ServiceLocator.GetService<CameraService>();
                if (cs != null && cs.Kind == CameraKindEnum.Fly)
                {
                    cs.Kind = previousKind;
                }
                else if (cs != null)
                {
                    // A game window (pause menu, inventory, map, dialog...) is open: it remembered Fly and puts it back when it
                    // closes. Without the mod's control that is the developers' fly camera, so it is caught and undone then.
                    restorePending = true;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("freecam-kind", "Free camera: restoring the camera: " + e);
            }
            harmony?.UnpatchSelf();
            UpdateLoadPatch();
            RestoreFov();
            if (frozeWorld)
            {
                frozeWorld = false;
                if (TimeTweaks.Speed == 0f) // the user may have changed it meanwhile
                {
                    TimeTweaks.SetSpeed(speedBefore);
                }
            }
            SetUiHidden(false);
            SetPlayerHidden(false);
            ImageTweaks.Refresh();
            if (message != null)
            {
                Plugin.ShowMessage(message);
            }
        }

        // ------------------------------------------------------------ loading around the camera

        private static Harmony loadHarmony;
        private static bool loadPatched;

        /// <summary>The loaders' patch is installed only while the camera is on and the option is set.</summary>
        private static void UpdateLoadPatch()
        {
            bool need = active && LoadAroundCamera.Value;
            try
            {
                loadHarmony = loadHarmony ?? new Harmony(Plugin.Guid + ".freecamera.load");
                if (need && !loadPatched)
                {
                    loadHarmony.PatchAll(typeof(CameraLoadPatches));
                    loadPatched = true;
                    Plugin.Log.LogInfo("Free camera: loading houses around the camera");
                }
                else if (!need && loadPatched)
                {
                    loadHarmony.UnpatchSelf();
                    loadPatched = false;
                    segmentLoaders = null;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("freecam-load", "Free camera: loading around the camera: " + e);
            }
        }

        private static LoadByDistanceFromSegment[] segmentLoaders;
        private static int segmentScenes = -1;

        /// <summary>
        /// Houses (LoadByDistanceFromSegment) are updated by the game's RegionDistanceUpdater, which accumulates Time.deltaTime
        /// and so stands still at game speed 0. While the world is stopped and houses load around the camera, they are run here
        /// twice a second; the list is refreshed after scene loads.
        /// </summary>
        private static void UpdateLoadersWhileStopped()
        {
            if (!loadPatched || Time.timeScale != 0f)
            {
                return;
            }
            if (segmentLoaders == null || segmentScenes != MemoryTweaks.ScenesLoaded + MemoryTweaks.ScenesUnloaded)
            {
                segmentLoaders = UnityEngine.Object.FindObjectsOfType<LoadByDistanceFromSegment>();
                segmentScenes = MemoryTweaks.ScenesLoaded + MemoryTweaks.ScenesUnloaded;
            }
            foreach (LoadByDistanceFromSegment l in segmentLoaders)
            {
                if (l != null && l.isActiveAndEnabled)
                {
                    l.ComputeUpdate();
                }
            }
        }

        /// <summary>Replaces IEntityView.Position (the player's position) in the house and district loaders.</summary>
        internal static Vector3 LoadCenter(IEntityView player)
        {
            GameCamera gc = GameCamera.Instance;
            if (active && gc != null && gc.CameraTransform != null)
            {
                return gc.CameraTransform.position;
            }
            return player.Position;
        }

        /// <summary>Replaces Time.deltaTime in the district loader's random check (0 while the world is stopped).</summary>
        internal static float LoadDeltaTime()
        {
            return Time.timeScale == 0f ? Time.unscaledDeltaTime : Time.deltaTime;
        }

        private static bool restorePending;

        internal static bool RestorePending => restorePending;

        /// <summary>Esc leaves only while really flying; over a game window (Kind Unknown) it is the window's.</summary>
        private static bool IsFlying()
        {
            try
            {
                return ServiceLocator.GetService<CameraService>()?.Kind == CameraKindEnum.Fly;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Every frame while the camera was left over a game window: when the window puts Fly back, the camera returns to the
        /// kind it had before the free camera. If the game sets a first-person or other camera of its own first, nothing to do.
        /// </summary>
        internal static void WatchRestore()
        {
            if (active)
            {
                restorePending = false;
                return;
            }
            CameraService cs = ServiceLocator.GetService<CameraService>();
            if (cs == null)
            {
                restorePending = false;
                return;
            }
            CameraKindEnum kind = cs.Kind;
            if (kind == CameraKindEnum.Fly)
            {
                cs.Kind = previousKind;
                restorePending = false;
                Plugin.Log.LogInfo($"Free camera: a game window put the fly camera back, restored {previousKind}");
            }
            else if (kind == CameraKindEnum.FirstPerson_Controlling || kind == CameraKindEnum.FirstPerson_Controlling2)
            {
                restorePending = false;
            }
        }

        /// <summary>A save or a new game is being loaded.</summary>
        internal static void OnLoad()
        {
            if (active)
            {
                Leave(null);
            }
        }

        /// <summary>Twice a second while active: leave when the game took the camera (cutscene, death, dialog).</summary>
        internal static void Tick()
        {
            if (!active)
            {
                return;
            }
            try
            {
                UpdateLoadersWhileStopped();
            }
            catch (Exception e)
            {
                Plugin.LogOnce("freecam-loaders", "Free camera: house loaders while stopped: " + e);
            }
            try
            {
                CameraService cs = ServiceLocator.GetService<CameraService>();
                // Unknown: a menu (pause, inventory, map) is open; it puts Fly back when closed, then flying goes on.
                if (cs == null || GameCamera.Instance == null || (cs.Kind != CameraKindEnum.Fly && cs.Kind != CameraKindEnum.Unknown))
                {
                    Leave("Free camera off: the game took the camera");
                }
            }
            catch
            {
                Leave(null);
            }
        }

        /// <summary>
        /// Plugin.Update, before the game's scripts. The game's input is not cleared while flying: in Fly mode the game itself
        /// does not control the player (PlayerUtility.IsPlayerCanControlling), and clearing all input also swallowed Esc.
        /// Returns true when Esc was pressed: the plugin leaves the free camera and clears that frame's input, so the pause
        /// menu does not open on top of Fly (it would put Fly back on closing, without the mod's control).
        /// </summary>
        internal static bool CaptureInput(bool windowOpen)
        {
            if (!active)
            {
                return false;
            }
            if (windowOpen)
            {
                look = Vector2.zero;
                move = Vector3.zero;
                rollInput = fovInput = 0f;
                return false;
            }
            if (Input.GetKeyDown(KeyCode.Escape) && IsFlying())
            {
                Leave("Free camera off");
                return true;
            }
            look = new Vector2(Input.GetAxisRaw("MouseX"), Input.GetAxisRaw("MouseY"));
            // Held keys are read from Windows: after Input.ResetInputAxes (called every frame to keep the input from the game)
            // Unity reports a held key as released until it is pressed again, so Input.GetKey would see only the first frame.
            bool focused = Application.isFocused;
            move = new Vector3(
                Held(focused, VkD) - Held(focused, VkA),
                Held(focused, VkQ) - Held(focused, VkE),
                Held(focused, VkW) - Held(focused, VkS));
            float k = Held(focused, VkShift) > 0f ? 4f : Held(focused, VkControl) > 0f ? 0.25f : 1f;
            move *= k;
            rollInput = Held(focused, VkZ) - Held(focused, VkC);
            fovInput = -Mathf.Max(Held(focused, VkPlus), Held(focused, VkAdd)) + Mathf.Max(Held(focused, VkMinus), Held(focused, VkSubtract));
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f)
            {
                speedScale = Mathf.Clamp(speedScale * Mathf.Pow(1.25f, wheel), 0.02f, 50f);
            }
            if (Input.GetKeyDown(KeyCode.R))
            {
                Roll = 0f;
            }
            if (Input.GetKeyDown(KeyCode.H))
            {
                SetUiHidden(!uiHidden);
            }
            return false;
        }

        private const int VkShift = 0x10, VkControl = 0x11, VkA = 0x41, VkC = 0x43, VkD = 0x44, VkE = 0x45, VkQ = 0x51, VkS = 0x53,
            VkW = 0x57, VkZ = 0x5A, VkAdd = 0x6B, VkSubtract = 0x6D, VkPlus = 0xBB, VkMinus = 0xBD;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vk);

        private static float Held(bool focused, int vk)
        {
            return focused && (GetAsyncKeyState(vk) & 0x8000) != 0 ? 1f : 0f;
        }

        /// <summary>Replaces FlyCameraController.Update (CameraService, in the game's LateUpdate).</summary>
        internal static void Apply()
        {
            GameCamera gc = GameCamera.Instance;
            if (!active || gc == null)
            {
                return;
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            Transform t = gc.CameraTransform;

            // Mouse look with the game's own sensitivity and invert settings (as FirstPersonCameraController does).
            InputGameSetting input = InstanceByRequest<InputGameSetting>.Instance;
            ExternalInputSettings ext = ExternalSettingsInstance<ExternalInputSettings>.Instance;
            float sens = Mathf.Lerp(ext.MinMouseSensitivity, ext.MaxMouseSensitivity, input.MouseSensitivity.Value);
            float fov = CurrentFov(gc);
            sens *= Mathf.Clamp(fov / 60f, 0.1f, 1.5f); // slower look when zoomed in
            yaw += look.x * sens;
            pitch += (input.MouseInvert.Value ? -look.y : look.y) * sens;
            pitch = Mathf.Clamp(pitch, -89.9f, 89.9f);
            Roll = Mathf.Repeat(Roll + rollInput * 45f * dt + 180f, 360f) - 180f;
            look = Vector2.zero;

            Quaternion rot = Quaternion.Euler(-pitch, yaw, Roll);
            Vector3 pos = t.position + rot * new Vector3(move.x, 0f, move.z) * (SpeedNow * dt) + Vector3.up * (move.y * SpeedNow * dt);
            t.SetPositionAndRotation(pos, rot);

            if (fovInput != 0f)
            {
                Fov.Value = Mathf.Clamp(fov * Mathf.Pow(1.6f, fovInput * dt), MinFov, MaxFov);
            }
            if (Fov.Value > 0f)
            {
                SetFov(gc, Fov.Value);
            }
        }

        private static float CurrentFov(GameCamera gc)
        {
            return Fov.Value > 0f ? Fov.Value : gc.Camera.fieldOfView;
        }

        private static void SetFov(GameCamera gc, float fov)
        {
            if (gc.Camera.fieldOfView != fov)
            {
                gc.Camera.fieldOfView = fov;
            }
            Camera[] extra = additionalCameras(gc);
            if (extra != null)
            {
                for (int i = 0; i < extra.Length; i++)
                {
                    if (extra[i] != null && extra[i].fieldOfView != fov)
                    {
                        extra[i].fieldOfView = fov;
                    }
                }
            }
        }

        /// <summary>The game sets the FOV only when its settings are applied, so it is put back explicitly.</summary>
        internal static void RestoreFov()
        {
            try
            {
                GameCamera gc = GameCamera.Instance;
                if (gc != null)
                {
                    AccessTools.Method(typeof(GameCamera), "ApplyFov")?.Invoke(gc, null);
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("freecam-fov", "Free camera: restoring the FOV: " + e);
            }
        }

        /// <summary>The same as the game's Ctrl+F11 (UIService.Visible); restored on leaving.</summary>
        internal static void SetUiHidden(bool hide)
        {
            if (hide == uiHidden)
            {
                return;
            }
            try
            {
                UIService ui = ServiceLocator.GetService<UIService>();
                if (ui == null)
                {
                    return;
                }
                if (hide)
                {
                    if (!ui.Visible)
                    {
                        return; // already hidden by the game or by Ctrl+F11: leave it to them
                    }
                    ui.Visible = false;
                    uiHidden = true;
                }
                else
                {
                    ui.Visible = true;
                    uiHidden = false;
                }
            }
            catch (Exception e)
            {
                uiHidden = false;
                Plugin.LogOnce("freecam-ui", "Free camera: interface: " + e);
            }
        }

        internal static bool UiHidden => uiHidden;

        /// <summary>The player's hands, sleeves and items: their renderers are switched off and back on (only those that were on).</summary>
        private static void SetPlayerHidden(bool hide)
        {
            if (!hide)
            {
                foreach (Renderer r in hiddenRenderers)
                {
                    if (r != null)
                    {
                        r.enabled = true;
                    }
                }
                hiddenRenderers.Clear();
                return;
            }
            try
            {
                IEntity p = ServiceLocator.GetService<ISimulation>()?.Player;
                GameObject go = p != null ? ((IEntityView)p).GameObject : null;
                if (go == null)
                {
                    return;
                }
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
                {
                    if (r.enabled)
                    {
                        r.enabled = false;
                        hiddenRenderers.Add(r);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("freecam-player", "Free camera: hiding the player: " + e);
            }
        }
    }

    /// <summary>Installed while the free camera is on with LoadAroundCamera: the loaders measure from the camera.</summary>
    [HarmonyPatch]
    internal static class CameraLoadPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(LoadByDistanceFromSegment), "ComputeUpdate");
            yield return AccessTools.Method(typeof(LoadByDistance), "Update");
            yield return AccessTools.Method(typeof(LoadByDistanceFromShapeOfRegion), "Update");
            yield return AccessTools.Method(typeof(LoadByDistanceOfRegion), "Update");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            MethodInfo getPosition = AccessTools.PropertyGetter(typeof(IEntityView), nameof(IEntityView.Position));
            MethodInfo getDelta = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            MethodInfo center = AccessTools.Method(typeof(FreeCamera), nameof(FreeCamera.LoadCenter));
            MethodInfo delta = AccessTools.Method(typeof(FreeCamera), nameof(FreeCamera.LoadDeltaTime));
            int replaced = 0;
            foreach (CodeInstruction ci in code)
            {
                if (ci.Calls(getPosition))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = center;
                    replaced++;
                }
                else if (ci.Calls(getDelta))
                {
                    ci.operand = delta;
                }
                yield return ci;
            }
            if (replaced == 0)
            {
                Plugin.LogOnce("freecam-load-" + original.DeclaringType?.Name, $"Free camera: {original.DeclaringType?.Name} has no player position read (game updated?)");
            }
        }
    }

    /// <summary>Installed only while the free camera is on (FreeCamera.Set).</summary>
    [HarmonyPatch]
    internal static class FreeCameraPatches
    {
        private static readonly AccessTools.FieldRef<Engine.Behaviours.Engines.Controllers.IKController, RootMotion.FinalIK.LookAtIK> lookAtIK =
            AccessTools.FieldRefAccess<Engine.Behaviours.Engines.Controllers.IKController, RootMotion.FinalIK.LookAtIK>("lookAtIK");
        private static readonly AccessTools.FieldRef<Engine.Behaviours.Engines.Controllers.IKController, float> lookWeight =
            AccessTools.FieldRefAccess<Engine.Behaviours.Engines.Controllers.IKController, float>("weigthLookAt");

        /// <summary>
        /// NPCs turn their head and body to the player with look-at IK, but IKController sets its weight to 0 whenever the camera
        /// is not FirstPerson_Controlling (also in the pause menu): they snap out of the pose. While flying, keep the weight the
        /// game computed (it still follows the player, not the free camera).
        /// </summary>
        [HarmonyPatch(typeof(Engine.Behaviours.Engines.Controllers.IKController), "OnPreLookAtIKUpdate")]
        [HarmonyPostfix]
        private static void LookAtPostfix(Engine.Behaviours.Engines.Controllers.IKController __instance)
        {
            try
            {
                if (__instance.TestMode)
                {
                    return;
                }
                RootMotion.FinalIK.LookAtIK ik = lookAtIK(__instance);
                float w = lookWeight(__instance);
                if (ik != null && w > 0f && ik.solver.IKPositionWeight == 0f)
                {
                    ik.solver.IKPositionWeight = Engine.Source.Components.Utilities.SmoothUtility.Smooth22(w);
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("freecam-lookat", "Free camera: NPC look-at: " + e);
            }
        }

        [HarmonyPatch(typeof(FlyCameraController), nameof(FlyCameraController.Update))]
        [HarmonyPrefix]
        private static bool FlyUpdatePrefix()
        {
            try
            {
                FreeCamera.Apply();
            }
            catch (Exception e)
            {
                Plugin.LogOnce("freecam", "Free camera: " + e);
            }
            return false;
        }
    }
}
