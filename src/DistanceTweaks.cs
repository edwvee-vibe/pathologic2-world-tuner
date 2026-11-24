using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Engine.Source.Components;
using HarmonyLib;
using Scripts.Behaviours.LoadControllers;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pathologic2WorldTuner
{
    internal enum LoaderKind
    {
        Houses,
        Landmarks,
        Districts
    }

    /// <summary>
    /// Draw distance at runtime. What actually limits it in Pathologic 2 (see .claude/notes/draw-distance.md):
    ///  - CameraCullDistances writes Camera.layerCullDistances once in Awake: 150 m for most layers (trees, fences, house
    ///    facades, NPCs), 100 m infection decals, 30 m interiors, 2000 m key buildings / district proxies and terrain;
    ///  - LoadByDistance* load and unload the real houses and districts as separate scenes at 30-200 m;
    ///  - grass has its own distance in the material (_DrawDistance), LOD bias and shadow distance come from the game settings.
    /// Absolute values use -1 for "as in the game"; multipliers use 1.
    /// </summary>
    internal static class DistanceTweaks
    {
        internal const int LayerIndoor = 14, LayerKeyBuilding = 20, LayerUnimportant = 21, LayerTerrain = 23;

        // Rendering
        internal static ConfigEntry<float> Objects, Decals, Interiors, KeyBuildings, TerrainLayer, FarClip;
        internal static ConfigEntry<bool> DecalsSameAsObjects;
        // Streaming
        internal static ConfigEntry<float> Houses, Landmarks, Districts, IndoorLoad;
        // Detail
        internal static ConfigEntry<float> LodBias, ShadowDistance, GrassScale, GroundTexture, TerrainError, ReflectionCulling;

        private static bool dirty = true;
        private static readonly Queue<Scene> sceneQueue = new Queue<Scene>();
        private static int sceneScans;
        private static bool sceneObjectsTouched;

        internal static void BindConfig(ConfigFile cfg)
        {
            const string r = "Distance.Rendering", s = "Distance.Streaming", d = "Distance.Detail";
            Objects = Bind(cfg, r, "Objects", -1f, -1f, 6000f,
                "Draw distance of everything without its own limit (layer Default and others): trees, bushes, fences, house facades, " +
                "people, puddles. Meters, -1 = as in the game (150). Limited by FarClip.");
            Decals = Bind(cfg, r, "InfectionDecals", -1f, -1f, 6000f,
                "Draw distance of the infection decals on houses (layer Unimportant Object). -1 = as in the game (100). Ignored when InfectionDecalsSameAsObjects = true.");
            DecalsSameAsObjects = cfg.Bind(r, "InfectionDecalsSameAsObjects", true,
                "Infection decals use the Objects distance (they disappear together with the houses they are on).");
            Interiors = Bind(cfg, r, "Interiors", -1f, -1f, 1000f, "Draw distance of interiors (layer Indoor). -1 = as in the game (30).");
            KeyBuildings = Bind(cfg, r, "KeyBuildings", -1f, -1f, 6000f,
                "Draw distance of key buildings and low-poly district proxies (layer Key Building). -1 = as in the game (2000).");
            TerrainLayer = Bind(cfg, r, "Terrain", -1f, -1f, 6000f, "Draw distance of the terrain layer. -1 = as in the game (2000).");
            FarClip = Bind(cfg, r, "FarClip", -1f, -1f, 6000f,
                "Far clip plane of the game camera: nothing is drawn beyond it. -1 = as in the game (2000). Cutscenes use their own.");

            Houses = Bind(cfg, s, "Houses", 1f, 0.5f, 6f,
                "Multiplier of the distances at which ordinary houses are loaded and unloaded (LoadByDistanceFromSegment, 389 objects, 30-95 m). " +
                "Real houses instead of distant proxies; costs CPU, memory and loading hitches.");
            Landmarks = Bind(cfg, s, "Landmarks", 1f, 0.5f, 6f,
                "Multiplier for special buildings and places (LoadByDistance, 95 objects, 45-200 m).");
            Districts = Bind(cfg, s, "Districts", 1f, 0.5f, 4f,
                "Multiplier for whole districts (LoadByDistanceFromShapeOfRegion and LoadByDistanceOfRegion, 48 objects, 10-250 m).");
            IndoorLoad = Bind(cfg, s, "IndoorLoad", 1f, 0.5f, 3f,
                "Multiplier of the load distance used while the player is indoors (never above the outdoor load distance).");

            LodBias = Bind(cfg, d, "LodBias", -1f, -1f, 8f,
                "QualitySettings.lodBias: higher = detailed LODs farther away. -1 = the game setting \"Level of details\" (up to 2).");
            ShadowDistance = Bind(cfg, d, "ShadowDistance", -1f, -1f, 500f,
                "Shadow distance in meters. -1 = the game setting (up to 75). Far shadows cost GPU time and get blurrier.");
            GrassScale = Bind(cfg, d, "GrassScale", 1f, 0.5f, 6f,
                "Multiplier of the grass draw distance (30-50 m in the game). Grass chunks are built on the CPU.");
            GroundTexture = Bind(cfg, d, "GroundTexture", -1f, -1f, 2000f,
                "Terrain.basemapDistance: beyond it the ground is drawn with a blurry low-resolution texture. -1 = as in the game (20).");
            TerrainError = Bind(cfg, d, "TerrainError", -1f, -1f, 50f,
                "Terrain.heightmapPixelError: lower = more detailed terrain mesh in the distance (minimum 1). -1 = as in the game (20).");
            ReflectionCulling = Bind(cfg, d, "ReflectionCulling", -1f, -1f, 1f,
                "Planar reflections (puddles, water) draw objects up to this fraction of the camera distances. -1 = as in the game (0.75). " +
                "Lower it if larger draw distances make reflections expensive.");
        }

        private static ConfigEntry<float> Bind(ConfigFile cfg, string section, string key, float def, float min, float max, string description)
        {
            ConfigEntry<float> e = cfg.Bind(section, key, def, new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
            e.SettingChanged += (sender, args) => { dirty = true; };
            return e;
        }

        internal static void MarkDirty()
        {
            dirty = true;
        }

        /// <summary>
        /// A scene was loaded: its objects are scanned later, one scene per frame, and only that scene (no world-wide search;
        /// houses load constantly while walking). Nothing is queued while everything is at the game values.
        /// </summary>
        internal static void OnSceneLoaded(Scene scene)
        {
            if (!StreamingDefault || !DetailDefault)
            {
                sceneQueue.Enqueue(scene);
            }
        }

        /// <summary>Every frame: apply changed settings at once (the window changes them).</summary>
        internal static void FrameTick()
        {
            if (dirty)
            {
                dirty = false;
                ApplyCamera();
                ApplyQuality();
                ApplySceneObjects();
            }
            if (sceneQueue.Count > 0)
            {
                Scene scene = sceneQueue.Dequeue();
                if (scene.IsValid() && scene.isLoaded && NeedSceneObjects())
                {
                    ScanScene(scene);
                }
            }
        }

        /// <summary>Twice a second: re-apply what the game may have reset (new camera, cutscene far clip, settings menu, NGSS).</summary>
        internal static void Tick()
        {
            ApplyCamera();
            ApplyQuality();
        }

        // ------------------------------------------------------------ camera: layer cull distances and far clip

        private static readonly AccessTools.FieldRef<CameraCullDistances, CameraCullDistances.LayerFarClipping[]> layerFarClippings =
            AccessTools.FieldRefAccess<CameraCullDistances, CameraCullDistances.LayerFarClipping[]>("layerFarClippings");
        private static readonly AccessTools.FieldRef<CameraCullDistances, float> defaultFarClippingPlane =
            AccessTools.FieldRefAccess<CameraCullDistances, float>("defaultFarClippingPlane");

        private static int cameraId;
        private static float[] gameCull;          // as CameraCullDistances.ApplyImpl builds it
        private static bool[] ownLayer;           // layers with their own distance in the asset
        private static float gameDefaultCull = 150f;
        private static float gameFar = 2000f;
        private static float writtenFar = -1f;
        private static readonly float[] desiredCull = new float[32];

        internal static float GameDefaultCull => gameDefaultCull;
        internal static float GameFar => gameFar;
        internal static float GameLayerCull(int layer) => gameCull != null ? gameCull[layer] : (layer == LayerIndoor ? 30f : layer == LayerUnimportant ? 100f : 2000f);

        internal static Camera GameCam => GameCamera.Instance != null ? GameCamera.Instance.Camera : null;

        private static bool cameraTouched, qualityTouched;

        /// <summary>Any rendering distance differs from the game. While none does (and nothing is left to restore), no work at all.</summary>
        private static bool RenderingActive =>
            Objects.Value > 0f || (!DecalsSameAsObjects.Value && Decals.Value > 0f) || Interiors.Value > 0f ||
            KeyBuildings.Value > 0f || TerrainLayer.Value > 0f || FarClip.Value > 0f;

        private static bool QualityActive => LodBias.Value > 0f || ShadowDistance.Value > 0f;

        /// <summary>Read the game's camera distances (for the window's "game" values); cheap after the first call.</summary>
        internal static Camera CameraInfo()
        {
            Camera cam = GameCam;
            if (cam != null && cam.GetInstanceID() != cameraId)
            {
                cameraId = cam.GetInstanceID();
                ReadGameCull(cam);
                gameFar = cam.farClipPlane;
                writtenFar = -1f;
            }
            return cam;
        }

        private static void ApplyCamera()
        {
            if (!RenderingActive && !cameraTouched)
            {
                return;
            }
            Camera cam = CameraInfo();
            if (cam == null)
            {
                return;
            }
            cameraTouched = RenderingActive; // after one pass back to the game values there is nothing left to do

            // Far clip. Cutscenes copy the Cinemachine camera's far plane every frame and restore their own value later,
            // so the mod only writes when the value is the game's or ours (not in the middle of a cutscene).
            float far = FarClip.Value > 0f ? FarClip.Value : gameFar;
            float current = cam.farClipPlane;
            if (Math.Abs(current - far) > 0.01f && (Math.Abs(current - gameFar) < 0.01f || Math.Abs(current - writtenFar) < 0.01f))
            {
                cam.farClipPlane = far;
                writtenFar = far;
            }

            // Layer distances: same as CameraCullDistances.ApplyImpl, with the overrides on top.
            float objects = Objects.Value > 0f ? Objects.Value : gameDefaultCull;
            for (int i = 0; i < 32; i++)
            {
                desiredCull[i] = ownLayer[i] ? gameCull[i] : objects;
            }
            if (DecalsSameAsObjects.Value ? Objects.Value > 0f : Decals.Value > 0f)
            {
                desiredCull[LayerUnimportant] = DecalsSameAsObjects.Value ? objects : Decals.Value;
            }
            if (Interiors.Value > 0f) desiredCull[LayerIndoor] = Interiors.Value;
            if (KeyBuildings.Value > 0f) desiredCull[LayerKeyBuilding] = KeyBuildings.Value;
            if (TerrainLayer.Value > 0f) desiredCull[LayerTerrain] = TerrainLayer.Value;

            float[] actual = cam.layerCullDistances;
            bool same = actual != null && actual.Length == 32;
            for (int i = 0; same && i < 32; i++)
            {
                same = Math.Abs(actual[i] - desiredCull[i]) < 0.01f;
            }
            if (!same)
            {
                cam.layerCullDistances = (float[])desiredCull.Clone();
            }
        }

        private static void ReadGameCull(Camera cam)
        {
            gameCull = new float[32];
            ownLayer = new bool[32];
            CameraCullDistances ccd = cam.GetComponent<CameraCullDistances>();
            if (ccd == null)
            {
                // No component: keep what the camera has now.
                float[] now = cam.layerCullDistances;
                for (int i = 0; i < 32; i++)
                {
                    gameCull[i] = now != null && now.Length == 32 ? now[i] : 0f;
                    ownLayer[i] = true;
                }
                gameDefaultCull = gameCull[0];
                Plugin.Log.LogWarning("CameraCullDistances not found on the game camera; using its current layer distances.");
                return;
            }
            gameDefaultCull = defaultFarClippingPlane(ccd);
            for (int i = 0; i < 32; i++)
            {
                gameCull[i] = gameDefaultCull;
            }
            CameraCullDistances.LayerFarClipping[] list = layerFarClippings(ccd) ?? new CameraCullDistances.LayerFarClipping[0];
            foreach (CameraCullDistances.LayerFarClipping item in list)
            {
                int mask = item.Layer.value;
                for (int i = 0; i < 32; i++)
                {
                    if ((mask & (1 << i)) != 0)
                    {
                        gameCull[i] = item.FarClippingPlane;
                        ownLayer[i] = true;
                    }
                }
            }
            Plugin.Log.LogInfo($"Game camera: far clip {cam.farClipPlane}, default layer distance {gameDefaultCull}, " +
                               $"Indoor {gameCull[LayerIndoor]}, Key Building {gameCull[LayerKeyBuilding]}, " +
                               $"Unimportant {gameCull[LayerUnimportant]}, Terrain {gameCull[LayerTerrain]}");
        }

        // ------------------------------------------------------------ quality settings: LOD bias, shadows

        private static float gameLodBias = -1f, gameShadowDistance = -1f;
        private static float gameNgssDistance = -1f;
        private static int ngssId;

        internal static float GameLodBias => gameLodBias > 0f ? gameLodBias : QualitySettings.lodBias;
        internal static float GameShadowDistance => gameShadowDistance > 0f ? gameShadowDistance : QualitySettings.shadowDistance;

        /// <summary>Called after GraphicsSettingsApplier.ApplyQualitySettings: the values there are the game's.</summary>
        internal static void OnGameQualityApplied()
        {
            gameLodBias = QualitySettings.lodBias;
            gameShadowDistance = QualitySettings.shadowDistance;
            ApplyQuality();
        }

        private static void ApplyQuality()
        {
            if (!QualityActive && !qualityTouched)
            {
                return;
            }
            qualityTouched = QualityActive;
            if (gameLodBias < 0f)
            {
                gameLodBias = QualitySettings.lodBias;
            }
            if (gameShadowDistance < 0f)
            {
                gameShadowDistance = QualitySettings.shadowDistance;
            }

            float lod = LodBias.Value > 0f ? LodBias.Value : gameLodBias;
            if (Math.Abs(QualitySettings.lodBias - lod) > 0.0001f)
            {
                QualitySettings.lodBias = lod;
            }

            float shadow = ShadowDistance.Value > 0f ? ShadowDistance.Value : gameShadowDistance;
            // NGSS (soft shadows of the sun) overwrites the shadow distance every frame when its global override is on.
            NGSS_Directional ngss = MonoBehaviourInstance<NGSS_Directional>.Instance;
            if (ngss != null && ngss.GLOBAL_SETTINGS_OVERRIDE)
            {
                if (ngss.GetInstanceID() != ngssId)
                {
                    ngssId = ngss.GetInstanceID();
                    gameNgssDistance = ngss.GLOBAL_SHADOWS_DISTANCE;
                }
                float ngssValue = ShadowDistance.Value > 0f ? ShadowDistance.Value : gameNgssDistance;
                if (Math.Abs(ngss.GLOBAL_SHADOWS_DISTANCE - ngssValue) > 0.01f)
                {
                    ngss.GLOBAL_SHADOWS_DISTANCE = ngssValue;
                }
                return;
            }
            if (Math.Abs(QualitySettings.shadowDistance - shadow) > 0.01f)
            {
                QualitySettings.shadowDistance = shadow;
                // Same cascade choice as GraphicsSettingsApplier.ApplyQualitySettings.
                if (shadow <= 5f)
                {
                    QualitySettings.shadowCascades = 1;
                }
                else if (shadow <= 15f)
                {
                    QualitySettings.shadowCascades = 2;
                    QualitySettings.shadowCascade2Split = 1f / 3f;
                }
                else
                {
                    QualitySettings.shadowCascades = 4;
                    QualitySettings.shadowCascade4Split = new Vector3(1f / 15f, 0.2f, 0.4666667f);
                }
            }
        }

        internal static bool NgssOverride
        {
            get
            {
                NGSS_Directional ngss = MonoBehaviourInstance<NGSS_Directional>.Instance;
                return ngss != null && ngss.GLOBAL_SETTINGS_OVERRIDE;
            }
        }

        // ------------------------------------------------------------ scene objects: loaders, grass, terrain, reflections

        private sealed class LoaderInfo
        {
            public BaseLoadByDistance Component;
            public LoaderKind Kind;
            public float Load, Unload, Indoor;
            public bool HasIndoor;
        }

        private sealed class TypeFields
        {
            public FieldInfo Load, Unload, Indoor, Initialise, Model;
        }

        private static readonly Dictionary<int, LoaderInfo> loaders = new Dictionary<int, LoaderInfo>();
        private static readonly Dictionary<Type, TypeFields> typeFields = new Dictionary<Type, TypeFields>();
        private static readonly Dictionary<int, KeyValuePair<Material, Vector4>> grass = new Dictionary<int, KeyValuePair<Material, Vector4>>();
        private static readonly Dictionary<int, KeyValuePair<Terrain, Vector2>> terrains = new Dictionary<int, KeyValuePair<Terrain, Vector2>>();
        private static readonly Dictionary<int, KeyValuePair<PlanarReflectionCapture, float>> reflections =
            new Dictionary<int, KeyValuePair<PlanarReflectionCapture, float>>();
        private static readonly int DrawDistanceId = Shader.PropertyToID("_DrawDistance");

        private static bool StreamingDefault =>
            Math.Abs(Houses.Value - 1f) < 0.0001f && Math.Abs(Landmarks.Value - 1f) < 0.0001f &&
            Math.Abs(Districts.Value - 1f) < 0.0001f && Math.Abs(IndoorLoad.Value - 1f) < 0.0001f;

        private static bool DetailDefault =>
            Math.Abs(GrassScale.Value - 1f) < 0.0001f && GroundTexture.Value < 0f && TerrainError.Value < 0f && ReflectionCulling.Value < 0f;

        /// <summary>Scanning is skipped while everything is at the game values and nothing has been changed yet.</summary>
        private static bool NeedSceneObjects() => sceneObjectsTouched || !StreamingDefault || !DetailDefault;

        /// <summary>Rescan the scene now (window button).</summary>
        internal static void Rescan()
        {
            ScanSceneObjects();
            ApplySceneObjects();
            statsAt = -10f;
        }

        /// <summary>Record the loaders and game values for the window without changing anything (only finds new objects).</summary>
        internal static void ScanForWindow()
        {
            ScanSceneObjects();
            statsAt = -10f;
        }

        /// <summary>Whole-world search: only on the first change away from the game values and from the window.</summary>
        private static void ScanSceneObjects()
        {
            PurgeDestroyed();
            foreach (BaseLoadByDistance c in UnityEngine.Object.FindObjectsOfType<BaseLoadByDistance>(true))
            {
                AddLoader(c);
            }
            foreach (TerrainDetailLayer layer in UnityEngine.Object.FindObjectsOfType<TerrainDetailLayer>(true))
            {
                AddGrass(layer);
            }
            foreach (Terrain t in Terrain.activeTerrains)
            {
                AddTerrain(t);
            }
            foreach (PlanarReflectionCapture p in UnityEngine.Object.FindObjectsOfType<PlanarReflectionCapture>(true))
            {
                AddReflection(p);
            }
        }

        private static readonly List<GameObject> roots = new List<GameObject>();
        private static readonly List<BaseLoadByDistance> foundLoaders = new List<BaseLoadByDistance>();
        private static readonly List<TerrainDetailLayer> foundGrass = new List<TerrainDetailLayer>();
        private static readonly List<Terrain> foundTerrains = new List<Terrain>();
        private static readonly List<PlanarReflectionCapture> foundReflections = new List<PlanarReflectionCapture>();
        private static readonly List<LoaderInfo> freshLoaders = new List<LoaderInfo>();

        /// <summary>Only the objects of one newly loaded scene; settings are applied to what is new.</summary>
        private static void ScanScene(Scene scene)
        {
            if (++sceneScans % 64 == 0)
            {
                PurgeDestroyed();
            }
            freshLoaders.Clear();
            bool other = false;
            roots.Clear();
            scene.GetRootGameObjects(roots);
            foreach (GameObject root in roots)
            {
                root.GetComponentsInChildren(true, foundLoaders);
                foreach (BaseLoadByDistance c in foundLoaders)
                {
                    LoaderInfo info = AddLoader(c);
                    if (info != null)
                    {
                        freshLoaders.Add(info);
                    }
                }
                root.GetComponentsInChildren(true, foundGrass);
                foreach (TerrainDetailLayer g in foundGrass)
                {
                    other |= AddGrass(g);
                }
                root.GetComponentsInChildren(true, foundTerrains);
                foreach (Terrain t in foundTerrains)
                {
                    other |= AddTerrain(t);
                }
                root.GetComponentsInChildren(true, foundReflections);
                foreach (PlanarReflectionCapture p in foundReflections)
                {
                    other |= AddReflection(p);
                }
            }
            roots.Clear();
            foundLoaders.Clear();
            foundGrass.Clear();
            foundTerrains.Clear();
            foundReflections.Clear();
            if (freshLoaders.Count > 0)
            {
                ApplyLoaders(freshLoaders);
                freshLoaders.Clear();
            }
            if (other)
            {
                ApplyDetailObjects();
            }
        }

        private static LoaderInfo AddLoader(BaseLoadByDistance c)
        {
            int id = c.GetInstanceID();
            if (loaders.ContainsKey(id))
            {
                return null;
            }
            TypeFields f = FieldsOf(c.GetType());
            if (f.Load == null || f.Unload == null)
            {
                return null;
            }
            var info = new LoaderInfo
            {
                Component = c,
                Kind = KindOf(c),
                Load = (float)f.Load.GetValue(c),
                Unload = (float)f.Unload.GetValue(c),
                HasIndoor = f.Indoor != null
            };
            if (info.HasIndoor)
            {
                info.Indoor = (float)f.Indoor.GetValue(c);
            }
            loaders[id] = info;
            return info;
        }

        private static bool AddGrass(TerrainDetailLayer layer)
        {
            Material m = layer.Material;
            if (m == null || grass.ContainsKey(m.GetInstanceID()) || !m.HasProperty(DrawDistanceId))
            {
                return false;
            }
            grass[m.GetInstanceID()] = new KeyValuePair<Material, Vector4>(m, m.GetVector(DrawDistanceId));
            return true;
        }

        private static bool AddTerrain(Terrain t)
        {
            if (t == null || terrains.ContainsKey(t.GetInstanceID()))
            {
                return false;
            }
            terrains[t.GetInstanceID()] = new KeyValuePair<Terrain, Vector2>(t, new Vector2(t.basemapDistance, t.heightmapPixelError));
            return true;
        }

        private static bool AddReflection(PlanarReflectionCapture p)
        {
            if (reflections.ContainsKey(p.GetInstanceID()))
            {
                return false;
            }
            reflections[p.GetInstanceID()] = new KeyValuePair<PlanarReflectionCapture, float>(p, p.RelativeWorldCullingDistance);
            return true;
        }

        private static void PurgeDestroyed()
        {
            RemoveWhere(loaders, v => v.Component == null);
            RemoveWhere(grass, v => v.Key == null);
            RemoveWhere(terrains, v => v.Key == null);
            RemoveWhere(reflections, v => v.Key == null);
        }

        private static void RemoveWhere<T>(Dictionary<int, T> dict, Func<T, bool> dead)
        {
            List<int> remove = null;
            foreach (KeyValuePair<int, T> pair in dict)
            {
                if (dead(pair.Value))
                {
                    (remove ?? (remove = new List<int>())).Add(pair.Key);
                }
            }
            if (remove != null)
            {
                foreach (int k in remove)
                {
                    dict.Remove(k);
                }
            }
        }

        private static LoaderKind KindOf(BaseLoadByDistance c)
        {
            if (c is LoadByDistanceFromSegment) return LoaderKind.Houses;
            if (c is LoadByDistance) return LoaderKind.Landmarks;
            return LoaderKind.Districts; // LoadByDistanceFromShapeOfRegion, LoadByDistanceOfRegion and anything unknown
        }

        private static TypeFields FieldsOf(Type t)
        {
            if (!typeFields.TryGetValue(t, out TypeFields f))
            {
                f = new TypeFields
                {
                    Load = AccessTools.Field(t, "loadDistance"),
                    Unload = AccessTools.Field(t, "unloadDistance"),
                    Indoor = AccessTools.Field(t, "loadIndoorDistance"),
                    Initialise = AccessTools.Field(t, "initialise"),
                    Model = AccessTools.Field(t, "model")
                };
                if (f.Load != null && f.Load.FieldType != typeof(float)) f.Load = null;
                if (f.Unload != null && f.Unload.FieldType != typeof(float)) f.Unload = null;
                if (f.Indoor != null && f.Indoor.FieldType != typeof(float)) f.Indoor = null;
                if (f.Initialise != null && f.Initialise.FieldType != typeof(bool)) f.Initialise = null;
                typeFields[t] = f;
            }
            return f;
        }

        private static float ScaleOf(LoaderKind kind)
        {
            switch (kind)
            {
                case LoaderKind.Houses: return Houses.Value;
                case LoaderKind.Landmarks: return Landmarks.Value;
                default: return Districts.Value;
            }
        }

        private static void ApplySceneObjects()
        {
            if (!NeedSceneObjects())
            {
                return;
            }
            if (!sceneObjectsTouched)
            {
                sceneObjectsTouched = true;
                ScanSceneObjects(); // first change away from the game values: nothing has been scanned yet
            }
            ApplyLoaders(loaders.Values);
            ApplyDetailObjects();
            if (StreamingDefault && DetailDefault)
            {
                sceneObjectsTouched = false; // everything is back to the game values: stop tracking scenes
                sceneQueue.Clear();
            }
        }

        private static void ApplyLoaders(IEnumerable<LoaderInfo> list)
        {
            int changed = 0;
            foreach (LoaderInfo info in list)
            {
                if (info.Component == null)
                {
                    continue;
                }
                TypeFields f = FieldsOf(info.Component.GetType());
                float k = ScaleOf(info.Kind);
                // The game requires load <= unload and indoor <= load (CheckAndFix); scaling all of them keeps that.
                float load = info.Load * k;
                float unload = Mathf.Max(info.Unload * k, load + 1f);
                if (Math.Abs(k - 1f) < 0.0001f)
                {
                    unload = info.Unload;
                }
                bool any = false;
                any |= SetFloat(f.Load, info.Component, load);
                any |= SetFloat(f.Unload, info.Component, unload);
                if (info.HasIndoor)
                {
                    float indoor = Mathf.Min(info.Indoor * k * IndoorLoad.Value, load);
                    if (Math.Abs(k - 1f) < 0.0001f && Math.Abs(IndoorLoad.Value - 1f) < 0.0001f)
                    {
                        indoor = info.Indoor;
                    }
                    any |= SetFloat(f.Indoor, info.Component, indoor);
                }
                if (any)
                {
                    changed++;
                    // LoadByDistanceFromShapeOfRegion precomputes its bounds from the distances once.
                    f.Initialise?.SetValue(info.Component, false);
                }
            }
            if (changed > 0)
            {
                Plugin.Log.LogInfo($"Streaming distances set on {changed} loaders (houses x{Houses.Value:0.##}, landmarks x{Landmarks.Value:0.##}, " +
                                   $"districts x{Districts.Value:0.##}, indoor x{IndoorLoad.Value:0.##})");
            }
        }

        private static void ApplyDetailObjects()
        {

            foreach (KeyValuePair<Material, Vector4> g in grass.Values)
            {
                if (g.Key == null) continue;
                Vector4 v = g.Value;
                float k = GrassScale.Value;
                var want = new Vector4(v.x * k, v.y * k, v.z, v.w);
                if (g.Key.GetVector(DrawDistanceId) != want)
                {
                    g.Key.SetVector(DrawDistanceId, want);
                }
            }

            foreach (KeyValuePair<Terrain, Vector2> t in terrains.Values)
            {
                if (t.Key == null) continue;
                float basemap = GroundTexture.Value > 0f ? GroundTexture.Value : t.Value.x;
                float error = TerrainError.Value > 0f ? Mathf.Max(1f, TerrainError.Value) : t.Value.y;
                if (Math.Abs(t.Key.basemapDistance - basemap) > 0.01f) t.Key.basemapDistance = basemap;
                if (Math.Abs(t.Key.heightmapPixelError - error) > 0.01f) t.Key.heightmapPixelError = error;
            }

            foreach (KeyValuePair<PlanarReflectionCapture, float> p in reflections.Values)
            {
                if (p.Key == null) continue;
                float want = ReflectionCulling.Value > 0f ? ReflectionCulling.Value : p.Value;
                if (Math.Abs(p.Key.RelativeWorldCullingDistance - want) > 0.0001f) p.Key.RelativeWorldCullingDistance = want;
            }

        }

        private static bool SetFloat(FieldInfo field, object target, float value)
        {
            if (field == null)
            {
                return false;
            }
            float old = (float)field.GetValue(target);
            if (Math.Abs(old - value) < 0.001f)
            {
                return false;
            }
            field.SetValue(target, value);
            return true;
        }

        // ------------------------------------------------------------ info for the window

        internal struct LoaderStats
        {
            public int Total, Loaded;
            public float MinLoad, MaxLoad;
        }

        private static readonly LoaderStats[] stats = new LoaderStats[3];
        private static float statsAt = -10f;
        internal static int GrassMaterials => grass.Count;
        internal static int TerrainCount => terrains.Count;
        internal static float GameGrassDistance
        {
            get
            {
                float max = 0f;
                foreach (KeyValuePair<Material, Vector4> g in grass.Values)
                {
                    max = Mathf.Max(max, g.Value.x + g.Value.y);
                }
                return max;
            }
        }

        internal static float GameGroundTexture
        {
            get
            {
                foreach (KeyValuePair<Terrain, Vector2> t in terrains.Values) return t.Value.x;
                return 20f;
            }
        }

        internal static float GameTerrainError
        {
            get
            {
                foreach (KeyValuePair<Terrain, Vector2> t in terrains.Values) return t.Value.y;
                return 20f;
            }
        }

        internal static float GameReflectionCulling
        {
            get
            {
                foreach (KeyValuePair<PlanarReflectionCapture, float> p in reflections.Values) return p.Value;
                return 0.75f;
            }
        }

        internal static bool Scanned => sceneObjectsTouched;

        /// <summary>Loader counts by kind; recalculated at most once a second (only while the window shows them).</summary>
        internal static LoaderStats Stats(LoaderKind kind)
        {
            if (Time.unscaledTime - statsAt > 1f)
            {
                statsAt = Time.unscaledTime;
                for (int i = 0; i < stats.Length; i++)
                {
                    stats[i] = new LoaderStats { MinLoad = float.MaxValue };
                }
                foreach (LoaderInfo info in loaders.Values)
                {
                    if (info.Component == null) continue;
                    ref LoaderStats s = ref stats[(int)info.Kind];
                    s.Total++;
                    s.MinLoad = Mathf.Min(s.MinLoad, info.Load);
                    s.MaxLoad = Mathf.Max(s.MaxLoad, info.Load);
                    FieldInfo model = FieldsOf(info.Component.GetType()).Model;
                    if (model != null && model.GetValue(info.Component) is StaticModelComponent m && m.NeedLoad)
                    {
                        s.Loaded++;
                    }
                }
            }
            return stats[(int)kind];
        }
    }

    [HarmonyPatch]
    internal static class DistancePatches
    {
        [HarmonyPatch(typeof(GraphicsSettingsApplier), "ApplyQualitySettings")]
        [HarmonyPostfix]
        private static void ApplyQualitySettingsPostfix()
        {
            try
            {
                DistanceTweaks.OnGameQualityApplied();
                MemoryTweaks.OnGameQualityApplied();
            }
            catch (Exception e)
            {
                Plugin.LogOnce("quality", "Quality settings: " + e);
            }
        }
    }
}
