// Builds Assets/Scenes/Main.unity from code so the playable scene is reproducible and reviewable in git
// as a generator rather than hand-edited YAML. Headless-safe: callable with
//   -batchmode -nographics -quit -executeMethod Game.Editor.SceneBootstrap.CreateMainScene
// so nothing here may touch UI focus, Selection, or the active window.
//
// v3 / URP notes
// -------------
// The project is URP 17.6 Forward+ (Assets/Settings/PC_RPAsset.asset). Built-in-pipeline knobs are GONE:
// Camera.renderingPath, Camera.allowHDR, QualitySettings.pixelLightCount and QualitySettings.shadows do
// nothing under URP, so what used to be "deferred + 16 pixel lights" is now a property of the URP ASSET.
// Two of the three URP properties we need are read-only in the C# API, so the asset is edited through
// SerializedObject with its serialized field names (verified against the asset's own YAML).
//
// This file also authors the generated LevelGen assets (profile + room templates). They are created with
// AssetDatabase.CreateAsset and filled through SerializedObject rather than typed field access, because
// LevelGen.Unity.LevelGenProfile / RoomTemplateAsset live in a different assembly: resolving them by name at
// runtime means SceneBootstrap keeps compiling while those types evolve, and a renamed field degrades to a
// warning in the log instead of a red editor.
//
// NOTE: the sibling namespace Game.Debug shadows the type name Debug, so UnityEngine.Debug is qualified.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    public static class SceneBootstrap
    {
        const string SceneFolder = "Assets/Scenes";
        const string ScenePath = "Assets/Scenes/Main.unity";
        const int DefaultSeed = 42;

        // ---- URP ----
        const string UrpAssetPath = "Assets/Settings/PC_RPAsset.asset";
        const int WantedMaxAdditionalLights = 8;

        // ---- generated assets ----
        const string LevelGenFolder = "Assets/LevelGen";
        const string TemplatesFolder = "Assets/LevelGen/Templates";
        // The default profile lives in a Resources folder so LevelBuilder can find it by name at runtime
        // (Resources.Load) when its scene reference is empty - in the editor, in play mode and in builds.
        const string ResourcesFolder = "Assets/LevelGen/Resources";
        public const string ProfileAssetPath = "Assets/LevelGen/Resources/" + LevelGen.Unity.LevelBuilder.DefaultProfileResourceName + ".asset";
        const string TeammateTemplatePath = "Assets/LevelGen/Templates/TeammateRoom.asset";
        const string SpawnTemplatePath = "Assets/LevelGen/Templates/SpawnRoom_OneDoor.asset";
        const string ProceduralTemplatePath = "Assets/LevelGen/Templates/Procedural.asset";

        /// <summary>The teammate-built walled room we demonstrate prefab rooms with.</summary>
        const string FloorPrefabPath = "Assets/RoomFolder/Prefabs/Floor.prefab";

        /// <summary>Renderers whose name (or any ancestor's) contains this are not part of the room footprint.</summary>
        const string FootprintIgnoreToken = "Balloon";

        const string ProfileTypeName = "LevelGen.Unity.LevelGenProfile";
        const string TemplateTypeName = "LevelGen.Unity.RoomTemplateAsset";

        // ---- teammates' imported models, mapped onto the default profile ----
        const string BedPrefabPath = "Assets/CobleGames/Prefabs/hospital bed.prefab";
        const string ChairPrefabPath = "Assets/GeniusCrate_Games/Chair_sofa_set(interior)/Prefab/Chair_sofa_set/Chair_01.prefab";
        const string CorridorLightPrefabPath = "Assets/PolyKebap/LED Light Essentials Pack/Prefabs/ON/LED_Light_3.prefab";

        // ---- placeholder enemy (Game.Enemy assembly, resolved by name so the scene builds without it) ----
        public const string MonsterModelPath = "Assets/Models/basicmonster.obj";
        const string EnemySpawnerTypeName = "Game.Enemy.EnemySpawner";
        const string EnemyHudTypeName = "Game.Enemy.EnemyHud";
        const float MonsterHeight = 2.8f;
        const int EnemyCount = 1;

        public const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

        [MenuItem("CS462/Create Main Scene")]
        public static void CreateMainScene()
        {
            // Render pipeline and generated assets first: the scene references the profile, and the URP asset
            // is a project-level setting the scene has no say over.
            ApplyUrpSettings();
            ScriptableObject profile = EnsureLevelGenAssets();

            // AssetDatabase.Refresh/SaveAssets inside EnsureLevelGenAssets can reload the asset, which turns the
            // reference we hold into a destroyed (fake-null) object. Re-resolve it by path before using it.
            profile = AssetDatabase.LoadAssetAtPath<ScriptableObject>(ProfileAssetPath);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ---- level builder ----
            var builderGo = new GameObject("LevelBuilder");
            var builder = builderGo.AddComponent<LevelGen.Unity.LevelBuilder>();
            var so = new SerializedObject(builder);
            SetInt(so, "seed", DefaultSeed);
            SetBool(so, "randomSeedOnStart", false);
            if (profile != null) SetObject(so, "profile", profile);
            so.ApplyModifiedPropertiesWithoutUndo();
            UnityEngine.Debug.Log($"[SceneBootstrap] builder profile = {(profile != null ? AssetDatabase.GetAssetPath(profile) : "not assigned in scene; LevelBuilder will load Resources/" + LevelGen.Unity.LevelBuilder.DefaultProfileResourceName)}");

            // ---- player ----
            var playerGo = new GameObject("Player");
            playerGo.transform.position = new Vector3(0f, 1.1f, 0f);

            var cc = playerGo.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.4f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.slopeLimit = 45f;
            cc.stepOffset = 0.4f;
            cc.skinWidth = 0.08f;

            var fpc = playerGo.AddComponent<Game.Player.FirstPersonController>();

            // Reuse the default scene's Main Camera (it already carries the MainCamera tag + AudioListener)
            // instead of deleting and re-tagging, which is one more thing to get wrong headlessly.
            var camGo = FindCameraGameObject(scene);
            if (camGo == null)
            {
                camGo = new GameObject("Camera");
                camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
                camGo.tag = "MainCamera";
            }
            camGo.name = "Camera";
            if (camGo.GetComponent<AudioListener>() == null) camGo.AddComponent<AudioListener>();
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(playerGo.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            camGo.transform.localRotation = Quaternion.identity;
            camGo.transform.localScale = Vector3.one;

            var cam = camGo.GetComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 150f;
            cam.fieldOfView = 75f;
            ConfigureUrpCamera(cam);

            fpc.cameraPivot = camGo.transform;

            // ---- debug driver ----
            var debugGo = new GameObject("Debug");
            var dbg = debugGo.AddComponent<Game.Debug.LevelDebugController>();
            dbg.builder = builder;
            dbg.player = playerGo.transform;

            var overhead = debugGo.AddComponent<Game.Debug.OverheadDebugView>();
            overhead.builder = builder;
            overhead.player = fpc;
            overhead.playerCamera = cam;

            // ---- placeholder enemy ----
            AddEnemies(builder, playerGo.transform);

            // Fog and ambient are owned by LevelBuilder at Build() time now, so they are NOT set here.
            // The sun is only a faint blue wash; the builder's fixtures do the lighting (and may kill it outright).
            var sun = FindDirectionalLight(scene);
            if (sun != null)
            {
                sun.intensity = 0.05f;
                sun.color = new Color(0.72f, 0.78f, 1f, 1f);
                sun.shadows = LightShadows.Soft;
            }

            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                UnityEngine.Debug.Log("[SceneBootstrap] colorSpace -> Linear");
            }
            DemoSettings.Apply();

            if (!Directory.Exists(SceneFolder)) Directory.CreateDirectory(SceneFolder);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EnsureBuildSceneOrder();

            UnityEngine.Debug.Log("[SceneBootstrap] wrote Assets/Scenes/Main.unity");
        }

        // =============================================================== URP

        /// <summary>
        /// The builder places one real-time point light per fixture (20+ visible, with shadows). Under URP that
        /// budget lives on the pipeline ASSET, not on the camera or in QualitySettings:
        ///   m_AdditionalLightShadowsSupported   -> lamps cast shadows at all
        ///   m_AdditionalLightsRenderingMode     -> PerPixel (PerVertex would flatten every lamp)
        ///   m_AdditionalLightsPerObjectLimit    -> how many lamps may light one mesh (the "max additional lights")
        /// supportsAdditionalLightShadows and additionalLightsRenderingMode are read-only properties in URP 17,
        /// so the typed asset is used to READ/assert and SerializedObject to WRITE. Forward+ ignores the
        /// per-object limit at draw time, but it is still the value the inspector shows and Forward (non-plus)
        /// honours it, so it is kept correct either way.
        /// </summary>
        [MenuItem("CS462/Apply URP Settings")]
        public static void ApplyUrpSettings()
        {
            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (urp == null)
            {
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] no UniversalRenderPipelineAsset at {UrpAssetPath} " +
                                             "- URP light budget NOT applied. Is the project still on URP?");
                return;
            }

            var before = $"additionalLightShadows={urp.supportsAdditionalLightShadows} " +
                         $"additionalLightsMode={urp.additionalLightsRenderingMode} " +
                         $"maxAdditionalLights={urp.maxAdditionalLightsCount}";

            var so = new SerializedObject(urp);
            var changes = new List<string>();
            if (SetBoolIfDifferent(so, "m_AdditionalLightShadowsSupported", true, changes)) { }
            if (SetEnumIfDifferent(so, "m_AdditionalLightsRenderingMode", (int)LightRenderingMode.PerPixel,
                                   LightRenderingMode.PerPixel.ToString(), changes)) { }
            if (SetIntIfDifferent(so, "m_AdditionalLightsPerObjectLimit", WantedMaxAdditionalLights, changes)) { }

            if (changes.Count == 0)
            {
                UnityEngine.Debug.Log($"[SceneBootstrap] URP asset already correct ({before})");
                return;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(urp);
            AssetDatabase.SaveAssets();
            UnityEngine.Debug.Log($"[SceneBootstrap] URP {UrpAssetPath}: changed {string.Join(", ", changes)} (was {before})");
        }

        /// <summary>
        /// URP adds UniversalAdditionalCameraData itself the first time it renders, but the scene is authored
        /// headlessly and never renders here, so it is added explicitly and post-processing is turned off
        /// (there is no volume profile for Main.unity, and an uninitialised post stack is a black screen risk).
        /// </summary>
        static void ConfigureUrpCamera(Camera cam)
        {
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) data = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = false;
            data.renderShadows = true;
            data.requiresColorOption = CameraOverrideOption.UsePipelineSettings;
            data.requiresDepthOption = CameraOverrideOption.UsePipelineSettings;
            UnityEngine.Debug.Log("[SceneBootstrap] camera: UniversalAdditionalCameraData present, postProcessing=off, shadows=on");
        }

        /// <summary>
        /// Build Settings order: Main.unity at index 0 (the scene a player build OPENS - the demo exe must start
        /// in the game, not the template scene), SampleScene.unity second, every other scene after in its
        /// existing order. Nothing is dropped: teammates' scenes survive. Main and SampleScene are force-enabled;
        /// other scenes keep their enabled flag. Idempotent; logs the final order either way.
        /// </summary>
        [MenuItem("CS462/Fix Build Scene Order")]
        public static void EnsureBuildSceneOrder()
        {
            var existing = (EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0])
                .Where(s => s != null && !string.IsNullOrEmpty(s.path))
                .ToList();

            var ordered = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(ScenePath, true) };
            if (File.Exists(SampleScenePath) || existing.Any(s => s.path == SampleScenePath))
                ordered.Add(new EditorBuildSettingsScene(SampleScenePath, true));
            foreach (var s in existing)
                if (s.path != ScenePath && s.path != SampleScenePath && ordered.All(o => o.path != s.path))
                    ordered.Add(s);

            bool same = ordered.Count == existing.Count &&
                        ordered.Zip(existing, (a, b) => a.path == b.path && a.enabled == b.enabled).All(x => x);
            if (!same) EditorBuildSettings.scenes = ordered.ToArray();

            UnityEngine.Debug.Log($"[SceneBootstrap] build scene order {(same ? "already correct" : "updated")}: " +
                                  string.Join(", ", EditorBuildSettings.scenes.Select((s, i) =>
                                      $"[{i}] {s.path}{(s.enabled ? "" : " (disabled)")}")));
        }

        // =============================================================== generated LevelGen assets

        /// <summary>
        /// Creates (if missing) Assets/LevelGen/DefaultLevelGenProfile.asset plus the sample room templates,
        /// and returns the profile. Safe to call repeatedly: existing assets are reused, never overwritten.
        /// </summary>
        [MenuItem("CS462/Create Default Level Gen Assets")]
        public static void CreateDefaultLevelGenAssetsMenu()
        {
            var p = EnsureLevelGenAssets();
            UnityEngine.Debug.Log(p != null
                ? $"[SceneBootstrap] profile ready at {ProfileAssetPath}"
                : "[SceneBootstrap] could not create the profile - see warnings above");
        }

        public static ScriptableObject EnsureLevelGenAssets()
        {
            EnsureFolder(LevelGenFolder);
            EnsureFolder(TemplatesFolder);
            EnsureFolder(ResourcesFolder);

            var profileType = FindType(ProfileTypeName);
            var templateType = FindType(TemplateTypeName);
            if (profileType == null)
            {
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] type {ProfileTypeName} not found - the LevelGen.Unity " +
                                             "assembly has not defined it yet. Skipping profile creation.");
                return null;
            }

            var profile = LoadOrCreate(ProfileAssetPath, profileType);
            if (profile == null) return null;

            // Teammates' furniture / fixture models. Independent of templates, so done before that early-out.
            ConfigurePrefabMaps(profile);

            if (templateType == null)
            {
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] type {TemplateTypeName} not found - profile created " +
                                             "with no templates (everything will generate procedurally).");
                return profile;
            }

            // 1. Plain procedural template, weight 4: the ordinary generated hospital room. Weighted 4 against the
            //    prefab room's 1 so the teammate room SHOWS UP without dominating the level.
            var procedural = LoadOrCreate(ProceduralTemplatePath, templateType);
            ConfigureProcedural(procedural);

            // 2. The teammate's hand-built prefab room, sized from the prefab's own renderers.
            var teammate = LoadOrCreate(TeammateTemplatePath, templateType);
            ConfigureTeammateRoom(teammate);

            // 3. A single-entrance spawn room: procedural, SpawnOnly, one door, forced Lobby archetype.
            var spawn = LoadOrCreate(SpawnTemplatePath, templateType);
            ConfigureSpawnRoom(spawn);

            AssignTemplates(profile, new[] { procedural, teammate, spawn });

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            // No AssetDatabase.Refresh() here: a refresh can reimport the asset we just saved and destroy the
            // managed reference callers still hold. Callers re-resolve the asset by path when they need it.
            return profile;
        }

        // =============================================================== prefab maps (teammates' models)

        /// <summary>
        /// Maps the teammates' imported models onto the profile's propPrefabs / lightPrefabs. Each entry is found
        /// by its <c>type</c> value and updated in place, or appended; a missing asset is skipped with a warning
        /// (its entry, if any, is left alone). Idempotent. Field names follow LevelGen.Unity.PropPrefabEntry /
        /// LightPrefabEntry; a field the runtime does not have yet warns and is skipped, never throws.
        /// </summary>
        static void ConfigurePrefabMaps(ScriptableObject profile)
        {
            var so = new SerializedObject(profile);
            var bed = LoadMapped(BedPrefabPath, "HospitalBed/Gurney");
            var chair = LoadMapped(ChairPrefabPath, "Chair");
            var strip = LoadMapped(CorridorLightPrefabPath, "CorridorStrip");

            if (bed != null)
            {
                MapProp(so, LevelGen.Core.PropType.HospitalBed, bed, "FitFootprint");
                MapProp(so, LevelGen.Core.PropType.Gurney, bed, "FitFootprint");
            }
            if (chair != null) MapProp(so, LevelGen.Core.PropType.Chair, chair, "UniformToHeight");
            if (strip != null)
            {
                var e = FindOrAppendEntry(so, "lightPrefabs", (int)LevelGen.Core.LightType.CorridorStrip);
                if (e != null)
                {
                    RelObject(e, "prefab", strip);
                    RelBool(e, "applyDataToLight", true);
                    RelBool(e, "disableExtraLights", true);
                    RelBool(e, "fitToFixture", true);
                    RelFloat(e, "fixtureHeightMetres", 0.15f);
                    RelVector3(e, "extraRotationEuler", Vector3.zero);
                    RelVector3(e, "mountOffset", new Vector3(0f, -0.05f, 0f)); // hang just under the ceiling plane
                    UnityEngine.Debug.Log($"[SceneBootstrap] light map CorridorStrip -> {strip.name} " +
                                          "(disableExtraLights, applyDataToLight, fitToFixture 0.15 m, mountOffset y=-0.05)");
                }
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
        }

        static GameObject LoadMapped(string path, string label)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] prefab map {label}: {path} not found - left unmapped (primitives are used)");
            return go;
        }

        static void MapProp(SerializedObject so, LevelGen.Core.PropType type, GameObject prefab, string fit)
        {
            var e = FindOrAppendEntry(so, "propPrefabs", (int)type);
            if (e == null) return;
            // Every field is written, not just the interesting ones: an appended element starts as a COPY of
            // the previous one, so anything left unwritten would silently inherit another archetype's setting.
            RelObject(e, "prefab", prefab);
            RelBool(e, "addColliderIfMissing", true);
            RelEnumByName(e, "fit", fit);
            RelBool(e, "groundToFloor", true);
            RelBool(e, "centreXZ", true);
            RelVector3(e, "extraRotationEuler", Vector3.zero);
            RelBool(e, "keepPrefabColliders", false);
            UnityEngine.Debug.Log($"[SceneBootstrap] prop map {type} -> {prefab.name} (fit {fit}, groundToFloor, centreXZ, keepPrefabColliders false)");
        }

        /// <summary>Element of array <paramref name="arrayName"/> whose <c>type</c> enum equals <paramref name="typeValue"/>; appended if absent.</summary>
        static SerializedProperty FindOrAppendEntry(SerializedObject so, string arrayName, int typeValue)
        {
            var arr = so.FindProperty(arrayName);
            if (arr == null || !arr.isArray)
            {
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] profile has no serialized array '{arrayName}' - prefab map skipped");
                return null;
            }
            for (int i = 0; i < arr.arraySize; i++)
            {
                var el = arr.GetArrayElementAtIndex(i);
                var key = el.FindPropertyRelative("type");
                if (key != null && key.intValue == typeValue) return el;
            }
            arr.arraySize++;
            var added = arr.GetArrayElementAtIndex(arr.arraySize - 1);
            var k = added.FindPropertyRelative("type");
            if (k == null)
            {
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] '{arrayName}' elements have no 'type' field - prefab map skipped");
                arr.arraySize--;
                return null;
            }
            k.intValue = typeValue;
            return added;
        }

        static SerializedProperty Rel(SerializedProperty el, string name)
        {
            var p = el.FindPropertyRelative(name);
            if (p == null)
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] {el.propertyPath}: no field '{name}' (runtime not updated yet?) - skipped");
            return p;
        }

        static void RelBool(SerializedProperty el, string n, bool v) { var p = Rel(el, n); if (p != null) p.boolValue = v; }
        static void RelFloat(SerializedProperty el, string n, float v) { var p = Rel(el, n); if (p != null) p.floatValue = v; }
        static void RelObject(SerializedProperty el, string n, UnityEngine.Object v) { var p = Rel(el, n); if (p != null) p.objectReferenceValue = v; }
        static void RelVector3(SerializedProperty el, string n, Vector3 v) { var p = Rel(el, n); if (p != null) p.vector3Value = v; }

        /// <summary>Sets an enum field by member NAME, so a reordered enum cannot silently pick the wrong mode.</summary>
        static void RelEnumByName(SerializedProperty el, string n, string member)
        {
            var p = Rel(el, n);
            if (p == null) return;
            int idx = p.propertyType == SerializedPropertyType.Enum ? Array.IndexOf(p.enumNames, member) : -1;
            if (idx < 0)
            {
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] {p.propertyPath}: enum has no member '{member}' - skipped");
                return;
            }
            p.enumValueIndex = idx;
        }

        // =============================================================== placeholder enemy

        /// <summary>
        /// "Enemies" object with Game.Enemy.EnemySpawner + EnemyHud. Both types are resolved by name, so Main.unity
        /// still builds (enemy-less, with a warning) on a checkout where the Game.Enemy assembly is missing.
        /// A missing monster model leaves modelPrefab empty, which makes the spawner use its capsule fallback.
        /// </summary>
        static void AddEnemies(LevelGen.Unity.LevelBuilder builder, Transform player)
        {
            var spawnerType = FindType(EnemySpawnerTypeName);
            if (spawnerType == null)
            {
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] {EnemySpawnerTypeName} not found (Game.Enemy assembly missing?) - " +
                                             "scene built WITHOUT an enemy");
                return;
            }

            var go = new GameObject("Enemies");
            var spawner = go.AddComponent(spawnerType);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(MonsterModelPath);
            if (model == null)
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] monster model {MonsterModelPath} not found - modelPrefab left empty, " +
                                             "the spawner's capsule fallback is used");

            var so = new SerializedObject(spawner);
            SetObject(so, "builder", builder);
            SetObject(so, "player", player);
            SetObject(so, "modelPrefab", model);
            SetFloat(so, "modelHeight", MonsterHeight);
            SetBool(so, "spawnOnLevelBuilt", true);
            SetInt(so, "enemyCount", EnemyCount);
            so.ApplyModifiedPropertiesWithoutUndo();

            var hudType = FindType(EnemyHudTypeName);
            if (hudType != null)
            {
                var hud = go.AddComponent(hudType);
                // Optional wiring: only if the HUD exposes a spawner slot (no warning when it does not).
                var hso = new SerializedObject(hud);
                var slot = hso.FindProperty("spawner");
                if (slot != null && slot.propertyType == SerializedPropertyType.ObjectReference)
                {
                    slot.objectReferenceValue = spawner;
                    hso.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            else UnityEngine.Debug.LogWarning($"[SceneBootstrap] {EnemyHudTypeName} not found - no enemy HUD");

            UnityEngine.Debug.Log($"[SceneBootstrap] Enemies: {spawnerType.Name} count={EnemyCount} height={MonsterHeight} " +
                                  $"model={(model != null ? MonsterModelPath : "(capsule fallback)")} hud={(hudType != null)}");
        }

        static void ConfigureProcedural(ScriptableObject asset)
        {
            if (asset == null) return;
            var so = new SerializedObject(asset);
            // Pinned explicitly: RoomTemplateAsset falls back to the ASSET FILE NAME when templateName is empty,
            // and Room.TemplateName (which HeadlessVerify and the HUD match on) must not change if someone
            // renames the file.
            SetString(so, "templateName", "Procedural");
            SetFloat(so, "weight", 4f);
            SetEnum(so, "affinity", (int)LevelGen.Core.RoleAffinity.Any);
            SetFloat(so, "fixedSizeX", 0f);
            SetFloat(so, "fixedSizeZ", 0f);
            SetInt(so, "minDoors", 1);
            SetInt(so, "maxDoors", 4);
            SetAllowedDoors(so, LevelGen.Core.DoorSides.All);
            SetBool(so, "generateFloor", true);
            SetBool(so, "generateWalls", true);
            SetBool(so, "generateCeiling", true);
            SetBool(so, "generateProps", true);
            SetBool(so, "generateLights", true);
            SetObject(so, "prefab", null);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        static void ConfigureTeammateRoom(ScriptableObject asset)
        {
            if (asset == null) return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FloorPrefabPath);
            Vector2 footprint = MeasureFootprintXZ(prefab, out Vector2 prefabCentre);

            var so = new SerializedObject(asset);
            SetString(so, "templateName", "TeammateRoom");
            SetFloat(so, "weight", 1f);
            SetEnum(so, "affinity", (int)LevelGen.Core.RoleAffinity.Any);
            SetFloat(so, "fixedSizeX", footprint.x);
            SetFloat(so, "fixedSizeZ", footprint.y);
            SetEnum(so, "shape", (int)LevelGen.Core.RoomShape.Rectangle);
            SetAllowedDoors(so, LevelGen.Core.DoorSides.All);
            SetInt(so, "minDoors", 1);
            SetInt(so, "maxDoors", 1); // sealed prefab (no doorways yet) must be a dead end so it never cuts the level
            // The prefab owns its walls, ceiling, furniture and lamps. We only generate the floor under it.
            SetBool(so, "generateFloor", true);
            SetBool(so, "generateWalls", false);
            SetBool(so, "generateCeiling", false);
            SetBool(so, "generateProps", false);
            SetBool(so, "generateLights", false);
            SetObject(so, "prefab", prefab);
            // The generator places the prefab's origin at the room centre; shift it so the measured geometry centre
            // lands there instead (teammate prefabs are not always authored around their origin).
            SetVector3(so, "prefabOffset", new Vector3(-prefabCentre.x, 0f, -prefabCentre.y));
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);

            if (prefab == null)
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] TeammateRoom: {FloorPrefabPath} missing; " +
                                             $"template left prefab-less with a {footprint.x} x {footprint.y} m fallback footprint.");
        }

        static void ConfigureSpawnRoom(ScriptableObject asset)
        {
            if (asset == null) return;
            var so = new SerializedObject(asset);
            SetString(so, "templateName", "SpawnRoom_OneDoor");
            SetFloat(so, "weight", 1f);
            SetEnum(so, "affinity", (int)LevelGen.Core.RoleAffinity.SpawnOnly);
            SetEnum(so, "shape", (int)LevelGen.Core.RoomShape.Rectangle);
            SetFloat(so, "fixedSizeX", 0f);
            SetFloat(so, "fixedSizeZ", 0f);
            SetAllowedDoors(so, LevelGen.Core.DoorSides.All);
            SetInt(so, "minDoors", 1);
            SetInt(so, "maxDoors", 1);   // the whole point: one entrance
            SetForcedType(so, LevelGen.Core.RoomType.Lobby);
            SetBool(so, "generateFloor", true);
            SetBool(so, "generateWalls", true);
            SetBool(so, "generateCeiling", true);
            SetBool(so, "generateProps", true);
            SetBool(so, "generateLights", true);
            SetObject(so, "prefab", null);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        /// <summary>
        /// Combined XZ renderer bounds of a prefab, balloon excluded, rounded UP to the next 0.5 m so the
        /// generated floor is never a hair smaller than the prefab's own walls. Falls back to 12 x 12 m
        /// when the prefab cannot be loaded (so the template is still a usable demo).
        /// </summary>
        public static Vector2 MeasureFootprintXZ(GameObject prefab) => MeasureFootprintXZ(prefab, out _);

        /// <summary>Footprint size (rounded up to 0.5 m) plus the XZ centre of the prefab's renderers relative to its origin.</summary>
        public static Vector2 MeasureFootprintXZ(GameObject prefab, out Vector2 centreXZ)
        {
            centreXZ = Vector2.zero;
            if (prefab == null) return new Vector2(12f, 12f);

            // Measure in the prefab ROOT's local space. The builder places the root at the room centre (plus
            // prefabOffset), so what matters is where the geometry sits relative to the root, not where the
            // root happens to be parked inside the prefab asset.
            bool any = false;
            Bounds combined = default;
            var root = prefab.transform;
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (IgnoredForFootprint(r.transform, root)) continue;
                var wb = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? wb.min.x : wb.max.x,
                                             (i & 2) == 0 ? wb.min.y : wb.max.y,
                                             (i & 4) == 0 ? wb.min.z : wb.max.z);
                    var local = root.InverseTransformPoint(corner);
                    if (!any) { combined = new Bounds(local, Vector3.zero); any = true; }
                    else combined.Encapsulate(local);
                }
            }
            if (!any)
            {
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] {prefab.name} has no usable Renderer - falling back to 12 x 12 m");
                return new Vector2(12f, 12f);
            }

            var size = new Vector2(RoundUpTo(combined.size.x, 0.5f), RoundUpTo(combined.size.z, 0.5f));
            centreXZ = new Vector2(combined.center.x, combined.center.z);
            UnityEngine.Debug.Log($"[SceneBootstrap] measured {prefab.name}: root-local XZ = {combined.size.x:F3} x {combined.size.z:F3} m " +
                                  $"(centre {combined.center.x:F2},{combined.center.z:F2} relative to the prefab root) -> fixedSize {size.x} x {size.y} m " +
                                  $"(ignoring renderers named *{FootprintIgnoreToken}*)");
            return size;
        }

        static bool IgnoredForFootprint(Transform t, Transform root)
        {
            for (var cur = t; cur != null; cur = cur.parent)
            {
                if (cur.name.IndexOf(FootprintIgnoreToken, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (cur == root) break;
            }
            return false;
        }

        static float RoundUpTo(float v, float step) => Mathf.Ceil(Mathf.Abs(v) / step) * step;

        /// <summary>Fills the profile's <c>templates</c> list with the given assets, in order.</summary>
        static void AssignTemplates(ScriptableObject profile, ScriptableObject[] templates)
        {
            var so = new SerializedObject(profile);
            var list = so.FindProperty("templates");
            if (list == null || !list.isArray)
            {
                UnityEngine.Debug.LogWarning("[SceneBootstrap] profile has no serialized array 'templates' - templates not wired up.");
                return;
            }
            var wanted = templates.Where(t => t != null).ToArray();
            list.arraySize = wanted.Length;
            for (int i = 0; i < wanted.Length; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue = wanted[i];
            so.ApplyModifiedPropertiesWithoutUndo();
            UnityEngine.Debug.Log($"[SceneBootstrap] profile templates = {string.Join(", ", wanted.Select(t => t.name))}");
        }

        static ScriptableObject LoadOrCreate(string path, Type type)
        {
            var existing = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (existing != null) return existing;

            var created = ScriptableObject.CreateInstance(type);
            if (created == null)
            {
                UnityEngine.Debug.LogWarning($"[SceneBootstrap] could not instantiate {type.FullName}");
                return null;
            }
            AssetDatabase.CreateAsset(created, path);
            UnityEngine.Debug.Log($"[SceneBootstrap] created {path} ({type.FullName})");
            return created;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
            UnityEngine.Debug.Log($"[SceneBootstrap] created folder {folder}");
        }

        /// <summary>Resolves a type by full name across the loaded assemblies (the LevelGen.Unity assembly may rename it).</summary>
        static Type FindType(string fullName)
        {
            var t = Type.GetType(fullName);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        // =============================================================== SerializedObject helpers
        // Every setter is name-tolerant: a missing property warns once and is skipped, so a field rename in
        // a rename in another assembly cannot break scene generation.

        static SerializedProperty Find(SerializedObject so, string name, params string[] alternates)
        {
            var p = so.FindProperty(name);
            if (p != null) return p;
            foreach (var alt in alternates)
            {
                p = so.FindProperty(alt);
                if (p != null) return p;
            }
            UnityEngine.Debug.LogWarning($"[SceneBootstrap] {so.targetObject?.name}: no serialized property '{name}' - skipped");
            return null;
        }

        static void SetInt(SerializedObject so, string n, int v) { var p = Find(so, n); if (p != null) p.intValue = v; }
        static void SetFloat(SerializedObject so, string n, float v) { var p = Find(so, n); if (p != null) p.floatValue = v; }
        static void SetBool(SerializedObject so, string n, bool v) { var p = Find(so, n); if (p != null) p.boolValue = v; }
        static void SetString(SerializedObject so, string n, string v) { var p = Find(so, n); if (p != null) p.stringValue = v; }
        static void SetEnum(SerializedObject so, string n, int v) { var p = Find(so, n); if (p != null) p.intValue = v; }
        static void SetObject(SerializedObject so, string n, UnityEngine.Object v) { var p = Find(so, n); if (p != null) p.objectReferenceValue = v; }
        static void SetVector3(SerializedObject so, string n, Vector3 v) { var p = Find(so, n); if (p != null) p.vector3Value = v; }

        /// <summary>
        /// Allowed doors may be authored either as one DoorSides mask or as four bools, depending on how the
        /// RoomTemplateAsset is authored. Both are written if present.
        /// </summary>
        static void SetAllowedDoors(SerializedObject so, LevelGen.Core.DoorSides sides)
        {
            var mask = so.FindProperty("allowedDoors") ?? so.FindProperty("doorSides");
            if (mask != null) { mask.intValue = (int)sides; return; }

            bool wroteAny = false;
            wroteAny |= TrySetBool(so, "doorNorth", Has(sides, LevelGen.Core.DoorSides.North));
            wroteAny |= TrySetBool(so, "doorEast", Has(sides, LevelGen.Core.DoorSides.East));
            wroteAny |= TrySetBool(so, "doorSouth", Has(sides, LevelGen.Core.DoorSides.South));
            wroteAny |= TrySetBool(so, "doorWest", Has(sides, LevelGen.Core.DoorSides.West));
            if (!wroteAny)
                UnityEngine.Debug.LogWarning("[SceneBootstrap] RoomTemplateAsset exposes neither 'allowedDoors' nor door*-bools - doors left at their defaults");
        }

        /// <summary>RoomType? cannot be serialized, so it is a bool+enum pair or an enum with a None member.</summary>
        static void SetForcedType(SerializedObject so, LevelGen.Core.RoomType type)
        {
            var p = so.FindProperty("forcedType");
            if (p != null) p.intValue = (int)type;
            TrySetBool(so, "hasForcedType", true);
            TrySetBool(so, "useForcedType", true);
            TrySetBool(so, "forceType", true);
            if (p == null)
                UnityEngine.Debug.LogWarning("[SceneBootstrap] RoomTemplateAsset has no 'forcedType' - spawn template left un-forced");
        }

        static bool Has(LevelGen.Core.DoorSides mask, LevelGen.Core.DoorSides flag) => (mask & flag) != 0;

        static bool TrySetBool(SerializedObject so, string n, bool v)
        {
            var p = so.FindProperty(n);
            if (p == null) return false;
            p.boolValue = v;
            return true;
        }

        static bool SetBoolIfDifferent(SerializedObject so, string n, bool v, List<string> changes)
        {
            var p = so.FindProperty(n);
            if (p == null) { UnityEngine.Debug.LogWarning($"[SceneBootstrap] URP asset has no '{n}'"); return false; }
            if (p.boolValue == v) return false;
            p.boolValue = v;
            changes.Add($"{n}: {!v} -> {v}");
            return true;
        }

        static bool SetIntIfDifferent(SerializedObject so, string n, int v, List<string> changes)
        {
            var p = so.FindProperty(n);
            if (p == null) { UnityEngine.Debug.LogWarning($"[SceneBootstrap] URP asset has no '{n}'"); return false; }
            if (p.intValue == v) return false;
            changes.Add($"{n}: {p.intValue} -> {v}");
            p.intValue = v;
            return true;
        }

        static bool SetEnumIfDifferent(SerializedObject so, string n, int v, string label, List<string> changes)
        {
            var p = so.FindProperty(n);
            if (p == null) { UnityEngine.Debug.LogWarning($"[SceneBootstrap] URP asset has no '{n}'"); return false; }
            if (p.intValue == v) return false;
            changes.Add($"{n}: {p.intValue} -> {v} ({label})");
            p.intValue = v;
            return true;
        }

        // =============================================================== scene helpers

        static Light FindDirectionalLight(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var l in root.GetComponentsInChildren<Light>(true))
                    if (l.type == LightType.Directional) return l;
            }
            return null;
        }

        /// <summary>Rebuild the open scene's level in edit mode - eyeball layouts without entering Play.</summary>
        [MenuItem("CS462/Rebuild Level In Scene")]
        public static void RebuildLevelInScene()
        {
            var builder = FindBuilderInOpenScene();
            if (builder == null)
            {
                UnityEngine.Debug.LogWarning("[SceneBootstrap] no LevelBuilder in the open scene.");
                return;
            }

            var so = new SerializedObject(builder);
            var seedProp = so.FindProperty("seed");
            int seed = seedProp != null ? seedProp.intValue : DefaultSeed;
            builder.Build(seed);
            UnityEngine.Debug.Log("[SceneBootstrap] rebuilt level with seed " + seed);
        }

        static LevelGen.Unity.LevelBuilder FindBuilderInOpenScene()
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<LevelGen.Unity.LevelBuilder>(true);
                if (found != null) return found;
            }
            return null;
        }

        static GameObject FindCameraGameObject(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                var cam = root.GetComponentInChildren<Camera>(true);
                if (cam != null) return cam.gameObject;
            }
            return null;
        }
    }
}
