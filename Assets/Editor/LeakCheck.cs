using System.Linq;
// Edit-mode leak check: 100 builds of Main.unity, sampling the loaded-object counts every 10 builds.
//
// Run: Unity.exe -batchmode -nographics -quit -projectPath <proj> -executeMethod Game.Editor.LeakCheck.Run
//
// What it is actually looking for: LevelBuilder destroys its "Level" child on every build, but meshes created
// by RoomMeshBuilder and materials created by LevelMaterials are plain assets held by a MeshFilter /
// MeshRenderer - destroying the GameObject does NOT destroy them. If they are not released, a 100-build
// session is where it shows. The verdict compares the 100th build against the 20th (the first 10-20 builds
// still include one-off warm-up allocations: shaders, the URP asset, the editor's own caches).
//
// NOTE: the sibling namespace Game.Debug shadows the type name Debug, so UnityEngine.Debug is qualified.
using System.Collections.Generic;
using System.Text;
using LevelGen.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Editor
{
    public static class LeakCheck
    {
        const int BuildCount = 100;
        const int SampleEvery = 10;

        /// <summary>Build whose sample is the baseline; earlier builds still carry warm-up allocations.</summary>
        const int BaselineBuild = 20;

        /// <summary>Allowed growth of mesh / material counts between the baseline and the final sample.</summary>
        const float MaxGrowth = 0.15f;

        struct Sample
        {
            public int Build;
            public int Meshes;
            public int Materials;
            public int Lights;
            public int Objects;
            public long ManagedBytes;
        }

        [MenuItem("CS462/Leak Check (100 builds)")]
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var builder = Object.FindAnyObjectByType<LevelBuilder>();
            if (builder == null)
            {
                UnityEngine.Debug.LogError("[LeakCheck] no LevelBuilder in Main.unity");
                EditorApplication.Exit(1);
                return;
            }

            var samples = new List<Sample>();

            for (int build = 1; build <= BuildCount; build++)
            {
                builder.Build(build);
                if (build % SampleEvery != 0) continue;

                samples.Add(new Sample
                {
                    Build = build,
                    Meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length,
                    Materials = Resources.FindObjectsOfTypeAll<Material>().Length,
                    Lights = Resources.FindObjectsOfTypeAll<Light>().Length,
                    Objects = builder.transform.GetComponentsInChildren<Transform>(true).Length,
                    // true: collect first, so the number is live managed memory and not just allocation churn.
                    ManagedBytes = System.GC.GetTotalMemory(true),
                });
            }

            var table = new StringBuilder();
            table.AppendLine("[LeakCheck] build | meshes | materials | lights | objects | managedBytes");
            foreach (Sample s in samples)
                table.AppendLine($"[LeakCheck] {s.Build,5} | {s.Meshes,6} | {s.Materials,9} | " +
                                 $"{s.Lights,6} | {s.Objects,7} | {s.ManagedBytes,12}");
            UnityEngine.Debug.Log(table.ToString());

            Sample baseline = default, final = default;
            bool haveBaseline = false, haveFinal = false;
            foreach (Sample s in samples)
            {
                if (s.Build == BaselineBuild) { baseline = s; haveBaseline = true; }
                if (s.Build == BuildCount) { final = s; haveFinal = true; }
            }

            if (!haveBaseline || !haveFinal)
            {
                UnityEngine.Debug.LogError($"[LeakCheck] FAILED: missing the build {BaselineBuild} or build " +
                                           $"{BuildCount} sample ({samples.Count} samples taken)");
                EditorApplication.Exit(1);
                return;
            }

            float meshGrowth = Growth(baseline.Meshes, final.Meshes);
            float materialGrowth = Growth(baseline.Materials, final.Materials);
            bool meshesMonotonic = IsNonDecreasingFrom(samples, BaselineBuild, s => s.Meshes);
            bool materialsMonotonic = IsNonDecreasingFrom(samples, BaselineBuild, s => s.Materials);

            UnityEngine.Debug.Log($"[LeakCheck] build{BaselineBuild} -> build{BuildCount}: " +
                $"meshes {baseline.Meshes}->{final.Meshes} ({meshGrowth * 100f:F1}%, monotonic={meshesMonotonic}) " +
                $"materials {baseline.Materials}->{final.Materials} ({materialGrowth * 100f:F1}%, monotonic={materialsMonotonic}) " +
                $"objects {baseline.Objects}->{final.Objects} " +
                $"managedBytes {baseline.ManagedBytes}->{final.ManagedBytes}");

            // Mesh count legitimately tracks the size of the CURRENT level (a 9-room level has more meshes than
            // a 5-room one), so a point-to-point comparison is noise. A leak shows as growth that never comes
            // back down: compare the lowest sample in the second half against the highest in the first half.
            int half = samples.Count / 2;
            int firstHalfMax = samples.Take(half).Max(s => s.Meshes);
            int secondHalfMin = samples.Skip(half).Min(s => s.Meshes);
            bool meshesLeak = secondHalfMin > firstHalfMax * (1f + MaxGrowth);
            UnityEngine.Debug.Log($"[LeakCheck] mesh envelope: firstHalfMax={firstHalfMax} secondHalfMin={secondHalfMin} leak={meshesLeak}");

            var failures = new List<string>();
            if (meshesLeak)
                failures.Add($"loaded Mesh count grew {meshGrowth * 100f:F1}% " +
                             $"({baseline.Meshes} -> {final.Meshes}) over builds {BaselineBuild}-{BuildCount}, " +
                             $"limit {MaxGrowth * 100f:F0}% - meshes from RoomMeshBuilder are being orphaned " +
                             $"rather than destroyed with their GameObject (monotonic={meshesMonotonic})");
            if (materialGrowth > MaxGrowth)
                failures.Add($"loaded Material count grew {materialGrowth * 100f:F1}% " +
                             $"({baseline.Materials} -> {final.Materials}) over builds {BaselineBuild}-{BuildCount}, " +
                             $"limit {MaxGrowth * 100f:F0}% - materials should be created once per build and " +
                             $"shared (monotonic={materialsMonotonic})");

            if (failures.Count > 0)
            {
                UnityEngine.Debug.LogError("[LeakCheck] FAILED\n" + string.Join("\n", failures));
                EditorApplication.Exit(1);
                return;
            }

            UnityEngine.Debug.Log($"[LeakCheck] PASSED {BuildCount} builds");
            EditorApplication.Exit(0);
        }

        static float Growth(int from, int to) => from <= 0 ? (to > 0 ? 1f : 0f) : (to - from) / (float)from;

        static bool IsNonDecreasingFrom(List<Sample> samples, int firstBuild, System.Func<Sample, int> select)
        {
            int previous = int.MinValue;
            foreach (Sample s in samples)
            {
                if (s.Build < firstBuild) continue;
                int v = select(s);
                if (v < previous) return false;
                previous = v;
            }
            return true;
        }
    }
}
