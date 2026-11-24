using System;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using Engine.Source.Blueprints.Effects;
using HarmonyLib;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// On every landing after a jump the player's ability JumpEnd starts the blueprint PlayerEndJump
    /// (Resources/VisualEffects/Punches_Player/Fall_Damage, template 2.xml.gz). Besides the landing sound it pushes the field
    /// of view: TriggerNode -> SmoothNode 0.15 s -> curve (peak 1.275 early, back to 0) -> x1.2 -> FloatOutputParameterNode
    /// "Fov", which the effect mixer adds to GameCamera.AdditionalFov. That short FOV kick reads as a jerk of the view.
    /// A postfix on FloatOutputParameterNode.Value scales the value of the node that belongs to that blueprint only; the
    /// sound and every other effect stay. Installed only while the setting is not 1.
    /// </summary>
    internal static class LandingPush
    {
        internal static ConfigEntry<float> Scale;

        private const string BlueprintName = "PlayerEndJump";
        private static readonly ConditionalWeakTable<FloatOutputParameterNode, object> landingNodes = new ConditionalWeakTable<FloatOutputParameterNode, object>();
        private static readonly ConditionalWeakTable<FloatOutputParameterNode, object> otherNodes = new ConditionalWeakTable<FloatOutputParameterNode, object>();
        private static readonly object Mark = new object();
        private static Harmony harmony;
        private static bool patched, logged;

        internal static void BindConfig(ConfigFile cfg)
        {
            Scale = cfg.Bind("Player", "LandingCameraPush", 1f, new ConfigDescription(
                "Strength of the short field-of-view push the game plays on every landing after a jump (blueprint PlayerEndJump). " +
                "1 = as in the game, 0 = none. The landing sound stays.", new AcceptableValueRange<float>(0f, 2f)));
            Scale.SettingChanged += (s, e) => Refresh();
        }

        internal static void Refresh()
        {
            bool need = Math.Abs(Scale.Value - 1f) > 0.0001f;
            try
            {
                harmony = harmony ?? new Harmony(Plugin.Guid + ".landingpush");
                if (need && !patched)
                {
                    harmony.PatchAll(typeof(LandingPushPatches));
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
                Plugin.LogOnce("landingpush", "Landing camera push: " + e);
            }
        }

        /// <summary>Whether the node is the FOV output of the landing blueprint (looked up once per node).</summary>
        internal static bool IsLanding(FloatOutputParameterNode node)
        {
            if (landingNodes.TryGetValue(node, out _))
            {
                return true;
            }
            if (otherNodes.TryGetValue(node, out _))
            {
                return false;
            }
            bool landing = false;
            try
            {
                var agent = node.graph != null ? node.graph.agent : null;
                landing = agent != null && agent.gameObject.name.StartsWith(BlueprintName, StringComparison.Ordinal);
            }
            catch
            {
                // a node without a graph: not ours
            }
            (landing ? landingNodes : otherNodes).Add(node, Mark);
            if (landing && !logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"Landing camera push: the landing FOV kick is scaled x{Scale.Value:0.##}");
            }
            return landing;
        }
    }

    /// <summary>Installed while LandingCameraPush is not 1.</summary>
    [HarmonyPatch]
    internal static class LandingPushPatches
    {
        [HarmonyPatch(typeof(FloatOutputParameterNode), nameof(FloatOutputParameterNode.Value), MethodType.Getter)]
        [HarmonyPostfix]
        private static void ValuePostfix(FloatOutputParameterNode __instance, ref float __result)
        {
            if (__result == 0f)
            {
                return;
            }
            try
            {
                if (LandingPush.IsLanding(__instance))
                {
                    __result *= LandingPush.Scale.Value;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("landingpush-value", "Landing camera push: " + e);
            }
        }
    }
}
