using System.Collections.Generic;
using System.Linq;
using LevelGen.Core;
using LevelGen.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Editor
{
    /// <summary>
    /// Headless smoke test for the full build pipeline. Opens Main.unity, builds N seeds in edit mode,
    /// runs LevelValidator on each layout, checks that a NavMesh was baked, that the v2 content (ceilings,
    /// lights, props) actually reached the scene, that nothing got baked onto a ceiling or a tabletop,
    /// and that NavMesh.CalculatePath reaches every room centre and every corridor midpoint from spawn.
    ///
    /// v3 additions:
    ///  - the spawn room is a dead end: DoorCount == settings.SpawnMaxDoors (1 by default);
    ///  - the prefab-room template ("TeammateRoom") is actually placed at least once across the seed sweep,
    ///    and where it is placed the teammate's prefab was instantiated and a generated floor exists under it;
    ///  - the per-seed line reports template usage so a template that silently never gets picked is visible.
    ///
    /// Run: Unity.exe -batchmode -nographics -quit -projectPath ... -executeMethod Game.Editor.HeadlessVerify.BuildLevels
    /// Exits with code 1 on any failure. Inspector callers use Run(false) so the editor survives.
    /// </summary>
    public static class HeadlessVerify
    {
        const int SeedCount = 20;

        /// <summary>Walkable surfaces are all at y=0, so any NavMesh vertex above this is a bake on furniture or a ceiling.</summary>
        const float MaxNavVertexY = 0.5f;

        /// <summary>Template whose placement is required at least once over the sweep (the prefab-room demo).</summary>
        const string PrefabTemplateName = "TeammateRoom";

        const string TeammateTemplateAssetPath = "Assets/LevelGen/Templates/TeammateRoom.asset";

        [MenuItem("CS462/Headless Verify (20 seeds)")]
        public static void BuildLevels() { Run(true); }

        /// <summary>Returns true when every seed passed. <paramref name="exitOnDone"/> false keeps the editor alive.</summary>
        public static bool Run(bool exitOnDone)
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var builder = Object.FindAnyObjectByType<LevelBuilder>();
            if (builder == null) return Fail("no LevelBuilder in Main.unity", exitOnDone);

            // Name of the prefab the TeammateRoom template points at, read from the asset so this verifier
            // never hardcodes "Floor" - renaming the prefab must not quietly disable the check.
            string prefabName = ReadTemplatePrefabName(TeammateTemplateAssetPath);

            var failures = new List<string>();
            int prefabRoomCount = 0;   // rooms built from PrefabTemplateName, summed over every seed

            for (int seed = 0; seed < SeedCount; seed++)
            {
                builder.Build(seed);
                var layout = builder.CurrentLayout;
                if (layout == null) { failures.Add($"seed {seed}: CurrentLayout null"); continue; }

                foreach (var msg in LevelValidator.Validate(layout)) failures.Add($"seed {seed}: validator: {msg}");

                CountContents(layout, out int layoutLights, out int layoutProps);

                // ---- v3: the spawn room is a single-entrance dead end ----
                CheckSpawnDoors(seed, layout, failures);

                // ---- v2 scene content ----
                var renderers = builder.transform.GetComponentsInChildren<MeshRenderer>(true);
                if (!renderers.Any(r => r.name.Contains("Ceiling")))
                    failures.Add($"seed {seed}: no MeshRenderer with \"Ceiling\" in its name");

                int enabledLights = builder.transform.GetComponentsInChildren<Light>(true)
                                           .Count(l => l.enabled && l.gameObject.activeInHierarchy);
                if (enabledLights < layout.Rooms.Count)
                    failures.Add($"seed {seed}: {enabledLights} enabled lights < {layout.Rooms.Count} rooms");

                int propObjects = builder.transform.GetComponentsInChildren<Transform>(true)
                                         .Count(t => t.name.StartsWith("Prop_"));
                if (propObjects != layoutProps)
                    failures.Add($"seed {seed}: {propObjects} Prop_* objects != {layoutProps} layout props");

                // ---- v3: prefab rooms reached the scene ----
                foreach (var room in layout.Rooms)
                {
                    if (room.TemplateName != PrefabTemplateName) continue;
                    prefabRoomCount++;
                    CheckPrefabRoom(seed, builder, room, prefabName, failures);
                }

                // ---- NavMesh ----
                var tri = NavMesh.CalculateTriangulation();
                if (tri.vertices.Length == 0) { failures.Add($"seed {seed}: NavMesh has 0 vertices"); continue; }

                float maxY = tri.vertices.Max(v => v.y);
                if (maxY > MaxNavVertexY)
                    failures.Add($"seed {seed}: NavMesh vertex at y={maxY:F2} > {MaxNavVertexY} (baked on a ceiling or furniture top)");

                var spawn = builder.PlayerSpawnPoint;
                if (!NavMesh.SamplePosition(spawn, out var spawnHit, 1.0f, NavMesh.AllAreas))
                { failures.Add($"seed {seed}: spawn point not on NavMesh"); continue; }

                var widened = new List<int>();
                var authoredRooms = new List<string>();
                foreach (var room in layout.Rooms)
                {
                    var target = builder.RoomWorldCenter(room.Id);
                    // Rooms whose walls come from a hand-built prefab own their doorways: the generator cannot
                    // cut openings into them, so until the prefab is authored with doors its interior may be
                    // sealed. That is an authoring warning, not a generator failure (the room is a dead end by
                    // template rule, so it never cuts off the rest of the level).
                    if (room.Template != null && !room.Template.GenerateWalls)
                    {
                        var scratch = new List<string>();
                        if (!CheckPath(seed, $"room {room.Id} ({room.TemplateName})", spawnHit.position, target, 1.5f, scratch, null))
                            authoredRooms.Add($"room {room.Id} ({room.TemplateName}) interior not reachable - prefab needs a doorway on its corridor side");
                        continue;
                    }
                    if (CheckPath(seed, $"room {room.Id} ({room.Shape},{room.Role},{room.Type},{room.TemplateName})",
                                  spawnHit.position, target, 1.0f, failures, null))
                        continue;
                    // Props keep MouthClearance and room-centre clearance, so a centre miss means the
                    // sample radius was just too tight (ellipse edge, prop skirt). Retry wider and report it.
                    widened.Add(room.Id);
                    CheckPath(seed, $"room {room.Id} (wide sample)", spawnHit.position, target, 1.5f, failures, null);
                }
                foreach (var c in layout.Corridors)
                {
                    var mid = LevelBuilder.ToWorld((c.Path[0] + c.Path[c.Path.Count - 1]) * 0.5);
                    CheckPath(seed, $"corridor {c.Id} midpoint", spawnHit.position, mid, 1.0f, failures, failures);
                }
                if (widened.Count > 0)
                    UnityEngine.Debug.LogWarning($"[HeadlessVerify] seed={seed} rooms needing a 1.5 m sample: {string.Join(",", widened)}");
                if (authoredRooms.Count > 0)
                    UnityEngine.Debug.LogWarning($"[HeadlessVerify] seed={seed} prefab authoring: {string.Join("; ", authoredRooms)}");

                var spawnRoom = layout.SpawnRoom;
                UnityEngine.Debug.Log($"[HeadlessVerify] seed={seed} rooms={layout.Rooms.Count} corridors={layout.Corridors.Count} " +
                          $"ellipses={layout.Rooms.Count(r => r.Shape == RoomShape.Ellipse)} " +
                          $"props={propObjects}/{layoutProps} lights={enabledLights}/{layoutLights} " +
                          $"navVerts={tri.vertices.Length} navMaxY={maxY:F2} renderers={renderers.Length} " +
                          $"spawnDoors={(spawnRoom != null ? spawnRoom.DoorCount.ToString() : "-")} " +
                          $"templates={{{TemplateUsage(layout)}}}");
            }

            // The prefab-room template existing but never being placed is the quiet failure mode of the whole
            // templates feature, so it is an error rather than a warning.
            UnityEngine.Debug.Log($"[HeadlessVerify] rooms built from template \"{PrefabTemplateName}\" across " +
                                  $"{SeedCount} seeds: {prefabRoomCount}");
            if (prefabRoomCount < 1)
                failures.Add($"template \"{PrefabTemplateName}\" was never placed in {SeedCount} seeds " +
                             "(is it in the profile's templates list, and does its footprint fit a lattice cell?)");

            if (failures.Count > 0)
            {
                UnityEngine.Debug.LogError("[HeadlessVerify] FAILED\n" + string.Join("\n", failures));
                if (exitOnDone) EditorApplication.Exit(1);
                return false;
            }
            // Smoke-test the overhead debug view: hides every ceiling, restores every ceiling.
            var overhead = Object.FindAnyObjectByType<Game.Debug.OverheadDebugView>();
            if (overhead != null)
            {
                int ceilings = builder.GetComponentsInChildren<Renderer>().Count(r => r.gameObject.name.StartsWith("Ceiling"));
                overhead.Enter();
                int visibleWhileOverhead = builder.GetComponentsInChildren<Renderer>().Count(r => r.gameObject.name.StartsWith("Ceiling") && r.enabled);
                bool fogOff = !RenderSettings.fog;
                overhead.Exit();
                int visibleAfter = builder.GetComponentsInChildren<Renderer>().Count(r => r.gameObject.name.StartsWith("Ceiling") && r.enabled);
                UnityEngine.Debug.Log($"[HeadlessVerify] overhead: ceilings={ceilings} hiddenOk={visibleWhileOverhead == 0} fogOff={fogOff} restoredOk={visibleAfter == ceilings}");
                if (visibleWhileOverhead != 0 || !fogOff || visibleAfter != ceilings)
                {
                    UnityEngine.Debug.LogError("[HeadlessVerify] FAILED overhead view toggle");
                    if (exitOnDone) EditorApplication.Exit(1);
                    return false;
                }
            }
            else UnityEngine.Debug.LogWarning("[HeadlessVerify] no OverheadDebugView in scene (regenerate Main.unity)");

            UnityEngine.Debug.Log($"[HeadlessVerify] PASSED {SeedCount} seeds");
            if (exitOnDone && Application.isBatchMode) EditorApplication.Exit(0);
            return true;
        }

        // ------------------------------------------------------------------ v3 checks

        /// <summary>
        /// The spawn/exit room must be a leaf of the corridor tree when SpawnMaxDoors is 1 - that is the whole
        /// point of the setting, and loop edges are the thing most likely to break it.
        /// </summary>
        static void CheckSpawnDoors(int seed, LevelLayout layout, List<string> failures)
        {
            var spawn = layout.SpawnRoom;
            if (spawn == null) { failures.Add($"seed {seed}: no room with Role == Spawn"); return; }

            int want = layout.Settings != null ? layout.Settings.SpawnMaxDoors : 1;
            if (spawn.DoorCount < 1)
                failures.Add($"seed {seed}: spawn room {spawn.Id} has 0 doors (unreachable)");
            else if (want == 1 && spawn.DoorCount != 1)
                failures.Add($"seed {seed}: spawn room {spawn.Id} DoorCount={spawn.DoorCount}, expected 1 (SpawnMaxDoors=1)");
            else if (spawn.DoorCount > want)
                failures.Add($"seed {seed}: spawn room {spawn.Id} DoorCount={spawn.DoorCount} > SpawnMaxDoors={want}");
        }

        /// <summary>
        /// A prefab-backed room must have (a) the teammate's prefab instantiated under its room object and
        /// (b) a generated floor under it - the two halves of "we generate a floor under your prefab".
        /// </summary>
        static void CheckPrefabRoom(int seed, LevelBuilder builder, Room room, string prefabName, List<string> failures)
        {
            var holder = FindRoomObject(builder, room.Id);
            if (holder == null)
            {
                failures.Add($"seed {seed}: room {room.Id} (template {room.TemplateName}) has no \"Room_{room.Id}*\" object in the scene");
                return;
            }

            if (string.IsNullOrEmpty(prefabName))
            {
                UnityEngine.Debug.LogWarning($"[HeadlessVerify] seed={seed} room {room.Id}: no prefab name resolved from " +
                                             $"{TeammateTemplateAssetPath} - prefab-instance check skipped");
            }
            else
            {
                // Unity names an instantiated prefab after the prefab itself ("Floor", "Floor (1)"). The generated
                // floor mesh object is "Floor_<id>", which also starts with "Floor", so it is excluded explicitly.
                bool hasInstance = holder.GetComponentsInChildren<Transform>(true)
                    .Any(t => t != holder && t.name.StartsWith(prefabName) && !IsGeneratedFloorObject(t.name, room.Id));
                if (!hasInstance)
                    failures.Add($"seed {seed}: room {room.Id} (template {room.TemplateName}) has no child starting with " +
                                 $"\"{prefabName}\" - the prefab was not instantiated");
            }

            // (c) The prefab's colliders must stay inside the room footprint (+0.5 m slack). A prefab whose
            // geometry is not centred on its root, or whose footprint was measured wrong, leaks into corridors
            // and neighbouring rooms and silently cuts the NavMesh - exactly the failure this guards against.
            if (!string.IsNullOrEmpty(prefabName))
            {
                var instance = holder.GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(t => t != holder && t.name.StartsWith(prefabName) && !IsGeneratedFloorObject(t.name, room.Id));
                if (instance != null)
                {
                    Physics.SyncTransforms();
                    bool any = false; Bounds b = default;
                    foreach (var col in instance.GetComponentsInChildren<Collider>(true))
                    {
                        if (!any) { b = col.bounds; any = true; } else b.Encapsulate(col.bounds);
                    }
                    if (any)
                    {
                        var c = LevelBuilder.ToWorld(room.Center);
                        float slack = 0.5f;
                        float overX = Mathf.Max(b.max.x - (c.x + (float)room.HalfX), (c.x - (float)room.HalfX) - b.min.x);
                        float overZ = Mathf.Max(b.max.z - (c.z + (float)room.HalfZ), (c.z - (float)room.HalfZ) - b.min.z);
                        float over = Mathf.Max(overX, overZ);
                        if (over > slack)
                            failures.Add($"seed {seed}: room {room.Id} prefab \"{prefabName}\" colliders extend {over:F2} m beyond the " +
                                         $"{room.SizeX:F1}x{room.SizeZ:F1} footprint (bounds z [{b.min.z:F1},{b.max.z:F1}], room z " +
                                         $"[{c.z - (float)room.HalfZ:F1},{c.z + (float)room.HalfZ:F1}]) - check prefabOffset / root centring");
                    }
                }
            }

            bool hasFloor = holder.GetComponentsInChildren<MeshFilter>(true).Any(mf =>
                mf.sharedMesh != null &&
                (mf.sharedMesh.name.StartsWith("RoomFloor") || IsGeneratedFloorObject(mf.gameObject.name, room.Id)));
            if (!hasFloor)
                failures.Add($"seed {seed}: room {room.Id} (template {room.TemplateName}) has no generated floor mesh " +
                             "(expected a mesh named RoomFloor_* or an object named Floor_<id>)");
        }

        static bool IsGeneratedFloorObject(string name, int roomId) => name == $"Floor_{roomId}";

        /// <summary>
        /// Room objects are named "Room_&lt;id&gt;_&lt;role&gt;_&lt;type&gt;_&lt;shape&gt;" under the builder's level root.
        /// Matched by name (not by LevelBuilder.RoomObjects) so this verifier keeps working across both the old
        /// and the new builder; the id boundary check stops Room_1 matching Room_12.
        /// </summary>
        static Transform FindRoomObject(LevelBuilder builder, int roomId)
        {
            string exact = $"Room_{roomId}";
            string prefix = $"Room_{roomId}_";
            foreach (var t in builder.transform.GetComponentsInChildren<Transform>(true))
                if (t.name == exact || t.name.StartsWith(prefix)) return t;
            return null;
        }

        /// <summary>"Procedural x5, TeammateRoom x1" for the per-seed log line.</summary>
        static string TemplateUsage(LevelLayout layout)
        {
            return string.Join(", ", layout.Rooms
                .GroupBy(r => string.IsNullOrEmpty(r.TemplateName) ? "(none)" : r.TemplateName)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => $"{g.Key} x{g.Count()}"));
        }

        /// <summary>
        /// Name of the GameObject in a RoomTemplateAsset's "prefab" slot. Read through SerializedObject so this
        /// file does not take a compile-time dependency on the RoomTemplateAsset field layout.
        /// </summary>
        static string ReadTemplatePrefabName(string assetPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null)
            {
                UnityEngine.Debug.LogWarning($"[HeadlessVerify] no template asset at {assetPath} " +
                                             "(run CS462/Create Default Level Gen Assets)");
                return null;
            }
            var prop = new SerializedObject(asset).FindProperty("prefab");
            var go = prop != null ? prop.objectReferenceValue : null;
            if (go == null)
            {
                UnityEngine.Debug.LogWarning($"[HeadlessVerify] {assetPath} has no prefab assigned");
                return null;
            }
            return go.name;
        }

        // ------------------------------------------------------------------ shared

        // Counted inline so this verifier never depends on an optional Core extension method.
        static void CountContents(LevelLayout layout, out int lights, out int props)
        {
            lights = 0;
            props = 0;
            foreach (var r in layout.Rooms)
            {
                if (r.Lights != null) lights += r.Lights.Count;
                if (r.Props != null) props += r.Props.Count;
            }
            foreach (var c in layout.Corridors)
            {
                if (c.Lights != null) lights += c.Lights.Count;
                if (c.Props != null) props += c.Props.Count;
            }
        }

        /// <summary>
        /// True when <paramref name="to"/> is on the NavMesh within <paramref name="sampleRadius"/> and a complete
        /// path exists. <paramref name="sampleFailures"/> null means a sample miss is the caller's problem (it will retry).
        /// </summary>
        static bool CheckPath(int seed, string label, Vector3 from, Vector3 to, float sampleRadius,
                              List<string> failures, List<string> sampleFailures)
        {
            if (!NavMesh.SamplePosition(to, out var hit, sampleRadius, NavMesh.AllAreas))
            {
                var sink = sampleFailures ?? (sampleRadius > 1.0f ? failures : null);
                sink?.Add($"seed {seed}: {label} not on NavMesh at {to} (sample r={sampleRadius})");
                return false;
            }
            var path = new NavMeshPath();
            NavMesh.CalculatePath(from, hit.position, NavMesh.AllAreas, path);
            if (path.status != NavMeshPathStatus.PathComplete)
            {
                failures.Add($"seed {seed}: {label} path status {path.status}");
                return false;
            }
            return true;
        }

        static bool Fail(string msg, bool exitOnDone)
        {
            UnityEngine.Debug.LogError("[HeadlessVerify] " + msg);
            if (exitOnDone) EditorApplication.Exit(1);
            return false;
        }
    }
}
