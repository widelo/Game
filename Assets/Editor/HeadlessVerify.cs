using System.Collections.Generic;
using System.Diagnostics;
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
    /// v4 additions:
    ///  - the per-seed body is extracted into VerifySeed, so the 20-seed smoke run and the long stress sweep
    ///    apply byte-for-byte the same checks;
    ///  - BuildLevelsStress() runs LEVELGEN_SEEDS (default 200) seeds and reports aggregates instead of a
    ///    line per seed.
    ///
    /// Run: Unity.exe -batchmode -nographics -quit -projectPath ... -executeMethod Game.Editor.HeadlessVerify.BuildLevels
    ///      Unity.exe -batchmode -nographics -quit -projectPath ... -executeMethod Game.Editor.HeadlessVerify.BuildLevelsStress
    /// Exits with code 1 on any failure. Inspector callers use Run(false) so the editor survives.
    /// </summary>
    public static class HeadlessVerify
    {
        const int SeedCount = 20;

        /// <summary>Default seed count for the stress sweep, overridable with the LEVELGEN_SEEDS env var.</summary>
        const int DefaultStressSeeds = 200;

        /// <summary>Walkable surfaces are all at y=0, so any NavMesh vertex above this is a bake on furniture or a ceiling.</summary>
        const float MaxNavVertexY = 0.5f;

        /// <summary>Template whose placement is required at least once over the sweep (the prefab-room demo).</summary>
        const string PrefabTemplateName = "TeammateRoom";

        const string TeammateTemplateAssetPath = "Assets/LevelGen/Templates/TeammateRoom.asset";

        [MenuItem("CS462/Headless Verify (20 seeds)")]
        public static void BuildLevels() { Run(true); }

        [MenuItem("CS462/Headless Verify - Stress (LEVELGEN_SEEDS, default 200)")]
        public static void BuildLevelsStress() { RunStress(true); }

        /// <summary>Existing signature, preserved: the 20-seed sweep starting at seed 0.</summary>
        public static bool Run(bool exitOnDone) { return Run(exitOnDone, SeedCount, 0); }

        // ------------------------------------------------------------------ per-seed result

        /// <summary>Everything one seed contributes to the log line and to the stress aggregates.</summary>
        struct SeedStats
        {
            public bool LayoutOk;
            public int Rooms, Corridors, Ellipses;
            public int Props, LayoutProps, Lights, LayoutLights;
            public int Renderers, NavVerts, PrefabRooms;
            public float NavMaxY;
            public long BuildMs;
            public int WidenedRooms;
            public int AuthoringWarnings;
            public string TemplateUsage;
            public Dictionary<string, int> TemplateCounts;
        }

        // ------------------------------------------------------------------ the 20-seed smoke run

        /// <summary>
        /// Returns true when every seed passed. <paramref name="exitOnDone"/> false keeps the editor alive.
        /// <paramref name="seedCount"/> seeds are built starting at <paramref name="firstSeed"/>.
        /// </summary>
        public static bool Run(bool exitOnDone, int seedCount, int firstSeed)
        {
            LevelBuilder builder = OpenMainScene();
            if (builder == null) return Fail("no LevelBuilder in Main.unity", exitOnDone);

            // Name of the prefab the TeammateRoom template points at, read from the asset so this verifier
            // never hardcodes "Floor" - renaming the prefab must not quietly disable the check.
            string prefabName = ReadTemplatePrefabName(TeammateTemplateAssetPath);

            var failures = new List<string>();
            int prefabRoomCount = 0;   // rooms built from PrefabTemplateName, summed over every seed

            for (int i = 0; i < seedCount; i++)
            {
                int seed = firstSeed + i;
                SeedStats s = VerifySeed(builder, seed, prefabName, failures);
                prefabRoomCount += s.PrefabRooms;
                if (!s.LayoutOk) continue;

                UnityEngine.Debug.Log($"[HeadlessVerify] seed={seed} rooms={s.Rooms} corridors={s.Corridors} " +
                          $"ellipses={s.Ellipses} " +
                          $"props={s.Props}/{s.LayoutProps} lights={s.Lights}/{s.LayoutLights} " +
                          $"navVerts={s.NavVerts} navMaxY={s.NavMaxY:F2} renderers={s.Renderers} " +
                          $"buildMs={s.BuildMs} " +
                          $"spawnDoors={SpawnDoors(builder)} " +
                          $"templates={{{s.TemplateUsage}}}");
            }

            // The prefab-room template existing but never being placed is the quiet failure mode of the whole
            // templates feature, so it is an error rather than a warning.
            UnityEngine.Debug.Log($"[HeadlessVerify] rooms built from template \"{PrefabTemplateName}\" across " +
                                  $"{seedCount} seeds: {prefabRoomCount}");
            if (prefabRoomCount < 1)
                failures.Add($"template \"{PrefabTemplateName}\" was never placed in {seedCount} seeds " +
                             "(is it in the profile's templates list, and does its footprint fit a lattice cell?)");

            if (failures.Count > 0)
            {
                UnityEngine.Debug.LogError("[HeadlessVerify] FAILED\n" + string.Join("\n", failures));
                if (exitOnDone) EditorApplication.Exit(1);
                return false;
            }
            if (!CheckOverheadView(builder, exitOnDone)) return false;

            UnityEngine.Debug.Log($"[HeadlessVerify] PASSED {seedCount} seeds");
            if (exitOnDone && Application.isBatchMode) EditorApplication.Exit(0);
            return true;
        }

        // ------------------------------------------------------------------ the stress sweep

        /// <summary>
        /// Long sweep for CI: LEVELGEN_SEEDS (default 200) seeds through exactly the same per-seed checks as
        /// <see cref="Run(bool,int,int)"/>, but reporting one summary line per 20 seeds plus totals instead of
        /// a line per seed. Exits 1 on any failure.
        /// </summary>
        public static bool RunStress(bool exitOnDone)
        {
            int seedCount = ReadSeedCountFromEnv();
            UnityEngine.Debug.Log($"[HeadlessVerify] STRESS start seeds={seedCount} (LEVELGEN_SEEDS)");

            LevelBuilder builder = OpenMainScene();
            if (builder == null) return StressFail("no LevelBuilder in Main.unity", exitOnDone);

            string prefabName = ReadTemplatePrefabName(TeammateTemplateAssetPath);

            var failures = new List<string>();
            var templateTotals = new Dictionary<string, int>();

            long totalBuildMs = 0, maxBuildMs = 0;
            int minProps = int.MaxValue, maxProps = 0;
            int minLights = int.MaxValue, maxLights = 0;
            int prefabRoomCount = 0, authoringWarnings = 0, widenedRooms = 0, okSeeds = 0;

            // Per-chunk accumulators, reset every 20 seeds.
            long chunkBuildMs = 0;
            int chunkSeeds = 0, chunkFailuresAtStart = 0;

            for (int i = 0; i < seedCount; i++)
            {
                if (i % 20 == 0) { chunkBuildMs = 0; chunkSeeds = 0; chunkFailuresAtStart = failures.Count; }

                SeedStats s = VerifySeed(builder, i, prefabName, failures);
                prefabRoomCount += s.PrefabRooms;
                authoringWarnings += s.AuthoringWarnings;
                widenedRooms += s.WidenedRooms;
                totalBuildMs += s.BuildMs;
                chunkBuildMs += s.BuildMs;
                chunkSeeds++;
                if (s.BuildMs > maxBuildMs) maxBuildMs = s.BuildMs;

                if (s.LayoutOk)
                {
                    okSeeds++;
                    if (s.LayoutProps < minProps) minProps = s.LayoutProps;
                    if (s.LayoutProps > maxProps) maxProps = s.LayoutProps;
                    if (s.LayoutLights < minLights) minLights = s.LayoutLights;
                    if (s.LayoutLights > maxLights) maxLights = s.LayoutLights;
                    if (s.TemplateCounts != null)
                        foreach (var kv in s.TemplateCounts)
                        {
                            templateTotals.TryGetValue(kv.Key, out int n);
                            templateTotals[kv.Key] = n + kv.Value;
                        }
                }

                bool lastSeed = i == seedCount - 1;
                if ((i + 1) % 20 == 0 || lastSeed)
                    UnityEngine.Debug.Log($"[HeadlessVerify] STRESS seeds {i + 1 - chunkSeeds}-{i}: " +
                        $"avgBuildMs={(chunkSeeds > 0 ? chunkBuildMs / (double)chunkSeeds : 0):F1} " +
                        $"newFailures={failures.Count - chunkFailuresAtStart} " +
                        $"runningFailures={failures.Count}");
            }

            if (minProps == int.MaxValue) minProps = 0;
            if (minLights == int.MaxValue) minLights = 0;

            // NOTE: the NavMesh bake happens inside LevelBuilder.Build(), which this verifier may not modify,
            // so the bake cannot be timed separately from the rest of the build. buildMs is the whole
            // generate + geometry + bake pass; it is the only timing a Stopwatch around Build can give.
            UnityEngine.Debug.Log($"[HeadlessVerify] STRESS totals: seeds={seedCount} okSeeds={okSeeds} " +
                $"avgBuildMs={(seedCount > 0 ? totalBuildMs / (double)seedCount : 0):F1} maxBuildMs={maxBuildMs} " +
                $"(navmesh bake is included in buildMs and is not separable from outside LevelBuilder) " +
                $"props=[{minProps}..{maxProps}] lights=[{minLights}..{maxLights}] " +
                $"prefabRooms={prefabRoomCount} prefabAuthoringWarnings={authoringWarnings} " +
                $"widenedSampleRooms={widenedRooms} " +
                $"templates={{{string.Join(", ", templateTotals.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}"))}}}");

            if (prefabRoomCount < 1)
                failures.Add($"template \"{PrefabTemplateName}\" was never placed in {seedCount} seeds");

            if (failures.Count > 0)
            {
                UnityEngine.Debug.LogError($"[HeadlessVerify] STRESS FAILED {seedCount} seeds, " +
                                           $"{failures.Count} failures\n" + string.Join("\n", failures));
                if (exitOnDone) EditorApplication.Exit(1);
                return false;
            }
            if (!CheckOverheadView(builder, exitOnDone)) return false;

            UnityEngine.Debug.Log($"[HeadlessVerify] STRESS PASSED {seedCount} seeds");
            if (exitOnDone && Application.isBatchMode) EditorApplication.Exit(0);
            return true;
        }

        static int ReadSeedCountFromEnv()
        {
            string raw = System.Environment.GetEnvironmentVariable("LEVELGEN_SEEDS");
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw.Trim(), out int n) && n > 0) return n;
            if (!string.IsNullOrEmpty(raw))
                UnityEngine.Debug.LogWarning($"[HeadlessVerify] LEVELGEN_SEEDS=\"{raw}\" is not a positive " +
                                             $"integer; using {DefaultStressSeeds}");
            return DefaultStressSeeds;
        }

        static LevelBuilder OpenMainScene()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            return Object.FindAnyObjectByType<LevelBuilder>();
        }

        static string SpawnDoors(LevelBuilder builder)
        {
            Room spawn = builder.CurrentLayout != null ? builder.CurrentLayout.SpawnRoom : null;
            return spawn != null ? spawn.DoorCount.ToString() : "-";
        }

        static bool StressFail(string msg, bool exitOnDone)
        {
            UnityEngine.Debug.LogError("[HeadlessVerify] STRESS FAILED: " + msg);
            if (exitOnDone) EditorApplication.Exit(1);
            return false;
        }

        // ------------------------------------------------------------------ one seed, end to end

        /// <summary>
        /// Builds one seed and applies every check. Appends to <paramref name="failures"/>; the returned stats
        /// are what the two callers turn into their log lines. LayoutOk false means the build produced nothing
        /// usable and the rest of the stats are meaningless.
        /// </summary>
        static SeedStats VerifySeed(LevelBuilder builder, int seed, string prefabName, List<string> failures)
        {
            var stats = new SeedStats();

            var sw = Stopwatch.StartNew();
            builder.Build(seed);
            sw.Stop();
            stats.BuildMs = sw.ElapsedMilliseconds;

            LevelLayout layout = builder.CurrentLayout;
            if (layout == null) { failures.Add($"seed {seed}: CurrentLayout null"); return stats; }

            foreach (var msg in LevelValidator.Validate(layout)) failures.Add($"seed {seed}: validator: {msg}");

            CountContents(layout, out int layoutLights, out int layoutProps);
            stats.LayoutLights = layoutLights;
            stats.LayoutProps = layoutProps;
            stats.Rooms = layout.Rooms.Count;
            stats.Corridors = layout.Corridors.Count;
            stats.Ellipses = layout.Rooms.Count(r => r.Shape == RoomShape.Ellipse);
            stats.TemplateUsage = TemplateUsage(layout);
            stats.TemplateCounts = layout.Rooms
                .GroupBy(r => string.IsNullOrEmpty(r.TemplateName) ? "(none)" : r.TemplateName)
                .ToDictionary(g => g.Key, g => g.Count());

            // ---- v3: the spawn room is a single-entrance dead end ----
            CheckSpawnDoors(seed, layout, failures);

            // ---- v2 scene content ----
            var renderers = builder.transform.GetComponentsInChildren<MeshRenderer>(true);
            stats.Renderers = renderers.Length;
            if (!renderers.Any(r => r.name.Contains("Ceiling")))
                failures.Add($"seed {seed}: no MeshRenderer with \"Ceiling\" in its name");

            int enabledLights = builder.transform.GetComponentsInChildren<Light>(true)
                                       .Count(l => l.enabled && l.gameObject.activeInHierarchy);
            stats.Lights = enabledLights;
            if (enabledLights < layout.Rooms.Count)
                failures.Add($"seed {seed}: {enabledLights} enabled lights < {layout.Rooms.Count} rooms");

            int propObjects = builder.transform.GetComponentsInChildren<Transform>(true)
                                     .Count(t => t.name.StartsWith("Prop_"));
            stats.Props = propObjects;
            if (propObjects != layoutProps)
                failures.Add($"seed {seed}: {propObjects} Prop_* objects != {layoutProps} layout props");

            // ---- v3: prefab rooms reached the scene ----
            foreach (var room in layout.Rooms)
            {
                if (room.TemplateName != PrefabTemplateName) continue;
                stats.PrefabRooms++;
                CheckPrefabRoom(seed, builder, room, prefabName, failures);
            }

            // ---- NavMesh ----
            var tri = NavMesh.CalculateTriangulation();
            if (tri.vertices.Length == 0) { failures.Add($"seed {seed}: NavMesh has 0 vertices"); return stats; }
            stats.NavVerts = tri.vertices.Length;

            float maxY = tri.vertices.Max(v => v.y);
            stats.NavMaxY = maxY;
            if (maxY > MaxNavVertexY)
                failures.Add($"seed {seed}: NavMesh vertex at y={maxY:F2} > {MaxNavVertexY} (baked on a ceiling or furniture top)");

            var spawn = builder.PlayerSpawnPoint;
            if (!NavMesh.SamplePosition(spawn, out var spawnHit, 1.0f, NavMesh.AllAreas))
            { failures.Add($"seed {seed}: spawn point not on NavMesh"); return stats; }

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
            stats.WidenedRooms = widened.Count;
            stats.AuthoringWarnings = authoredRooms.Count;
            if (widened.Count > 0)
                UnityEngine.Debug.LogWarning($"[HeadlessVerify] seed={seed} rooms needing a 1.5 m sample: {string.Join(",", widened)}");
            if (authoredRooms.Count > 0)
                UnityEngine.Debug.LogWarning($"[HeadlessVerify] seed={seed} prefab authoring: {string.Join("; ", authoredRooms)}");

            stats.LayoutOk = true;
            return stats;
        }

        /// <summary>
        /// Smoke-test the overhead debug view: hides every ceiling, restores every ceiling. Returns false (and
        /// exits 1 when asked) on a failure, true when it passes or the scene has no OverheadDebugView.
        /// </summary>
        static bool CheckOverheadView(LevelBuilder builder, bool exitOnDone)
        {
            var overhead = Object.FindAnyObjectByType<Game.Debug.OverheadDebugView>();
            if (overhead == null)
            {
                UnityEngine.Debug.LogWarning("[HeadlessVerify] no OverheadDebugView in scene (regenerate Main.unity)");
                return true;
            }

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
