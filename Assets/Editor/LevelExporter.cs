using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LevelGen.Core;
using LevelGen.Unity;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// Freezes the level currently built by the LevelBuilder into a prefab, so a generated layout can be opened,
    /// measured and edited in the editor like any hand-made asset (no Play mode needed). Runtime meshes and
    /// generated materials are saved as assets next to the prefab, and a .txt sidecar lists every room and
    /// corridor with its dimensions. Prefab-based props/rooms stay prefab instances.
    ///
    /// Menu: CS462 > Export Current Level To Prefab (uses whatever is built in the open scene; use
    /// "CS462 > Rebuild Level In Scene" or the LevelBuilder inspector buttons first to pick a seed).
    /// Headless: -executeMethod Game.Editor.LevelExporter.ExportSeed  (env LEVELGEN_SEED, default 42)
    /// Output: Assets/LevelGen/Exported/Level_seed&lt;N&gt;/
    /// </summary>
    public static class LevelExporter
    {
        const string ExportRoot = "Assets/LevelGen/Exported";

        [MenuItem("CS462/Export Current Level To Prefab")]
        public static void ExportCurrentLevel()
        {
            var builder = UnityEngine.Object.FindAnyObjectByType<LevelBuilder>();
            if (builder == null || builder.CurrentLayout == null || builder.LevelRoot == null)
            {
                UnityEngine.Debug.LogWarning("[LevelExporter] no built level in the open scene - build one first (CS462 > Rebuild Level In Scene).");
                return;
            }
            string path = Export(builder);
            if (!string.IsNullOrEmpty(path)) EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        }

        public static void ExportSeed()
        {
            int seed = 42;
            string env = Environment.GetEnvironmentVariable("LEVELGEN_SEED");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out int s)) seed = s;

            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            var builder = UnityEngine.Object.FindAnyObjectByType<LevelBuilder>();
            if (builder == null) { UnityEngine.Debug.LogError("[LevelExporter] no LevelBuilder in Main.unity"); EditorApplication.Exit(1); return; }
            builder.Build(seed);
            string path = Export(builder);
            if (Application.isBatchMode) EditorApplication.Exit(string.IsNullOrEmpty(path) ? 1 : 0);
        }

        /// <summary>Exports the builder's current level. Returns the prefab asset path, or null on failure.</summary>
        public static string Export(LevelBuilder builder)
        {
            LevelLayout layout = builder.CurrentLayout;
            string folder = $"{ExportRoot}/Level_seed{layout.Seed}";
            EnsureFolder(folder);
            EnsureFolder(folder + "/Meshes");
            EnsureFolder(folder + "/Materials");

            // Work on a copy so the live level is untouched.
            GameObject clone = UnityEngine.Object.Instantiate(builder.LevelRoot.gameObject);
            clone.name = $"Level_seed{layout.Seed}";
            clone.transform.SetParent(null, true);

            var meshMap = new Dictionary<Mesh, Mesh>();
            var matMap = new Dictionary<Material, Material>();
            int meshCount = 0, matCount = 0;

            foreach (var mf in clone.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || AssetDatabase.Contains(mf.sharedMesh)) continue;
                mf.sharedMesh = Persist(mf.sharedMesh, meshMap, folder + "/Meshes", ref meshCount);
            }
            foreach (var mc in clone.GetComponentsInChildren<MeshCollider>(true))
            {
                if (mc.sharedMesh == null || AssetDatabase.Contains(mc.sharedMesh)) continue;
                mc.sharedMesh = Persist(mc.sharedMesh, meshMap, folder + "/Meshes", ref meshCount);
            }
            foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || AssetDatabase.Contains(mats[i])) continue;
                    mats[i] = PersistMaterial(mats[i], matMap, folder + "/Materials", ref matCount);
                    changed = true;
                }
                if (changed) r.sharedMaterials = mats;
            }

            // The baked NavMesh data is not an asset; drop it so the prefab has no dangling reference.
            foreach (var surface in clone.GetComponentsInChildren<NavMeshSurface>(true)) surface.navMeshData = null;

            string prefabPath = $"{folder}/Level_seed{layout.Seed}.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(clone, prefabPath, out bool ok);
            UnityEngine.Object.DestroyImmediate(clone);
            if (!ok || prefab == null)
            {
                UnityEngine.Debug.LogError($"[LevelExporter] prefab save failed at {prefabPath}");
                return null;
            }

            string txtPath = $"{folder}/Level_seed{layout.Seed}.txt";
            File.WriteAllText(txtPath, Describe(layout));
            AssetDatabase.ImportAsset(txtPath);
            AssetDatabase.SaveAssets();

            UnityEngine.Debug.Log($"[LevelExporter] exported {prefabPath} (meshes={meshCount} materials={matCount}) + {txtPath}");
            return prefabPath;
        }

        static Mesh Persist(Mesh runtime, Dictionary<Mesh, Mesh> map, string folder, ref int count)
        {
            if (map.TryGetValue(runtime, out Mesh saved)) return saved;
            Mesh copy = UnityEngine.Object.Instantiate(runtime);
            copy.name = runtime.name;
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitise(runtime.name)}.asset");
            AssetDatabase.CreateAsset(copy, path);
            map[runtime] = copy;
            count++;
            return copy;
        }

        static Material PersistMaterial(Material runtime, Dictionary<Material, Material> map, string folder, ref int count)
        {
            if (map.TryGetValue(runtime, out Material saved)) return saved;
            Material copy = new Material(runtime) { name = runtime.name };
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitise(runtime.name)}.mat");
            AssetDatabase.CreateAsset(copy, path);
            map[runtime] = copy;
            count++;
            return copy;
        }

        /// <summary>Human-readable dimensions: what a level designer wants when asking "how big is the hallway".</summary>
        public static string Describe(LevelLayout layout)
        {
            var s = layout.Settings;
            var sb = new StringBuilder();
            sb.AppendLine($"Level seed {layout.Seed} - generated {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Units: metres. X east, Z north, Y up. Spawn room centre is (0,0).");
            sb.AppendLine();
            sb.AppendLine("GLOBAL");
            sb.AppendLine($"  corridor width       {s.CorridorWidth:F2}");
            sb.AppendLine($"  ceiling height       {s.CeilingHeight:F2}");
            sb.AppendLine($"  doorway opening      {s.CorridorWidth:F2} wide (plus wall thickness each side), full height");
            sb.AppendLine($"  lattice pitch        {s.CellSize:F2}  (room centres sit on a {s.GridWidth}x{s.GridHeight} grid)");
            sb.AppendLine($"  room size range      {s.MinRoomSize:F1} .. {s.MaxRoomSize:F1} per axis");
            sb.AppendLine();
            sb.AppendLine("ROOMS  id  role     type           template        shape      centre(x,z)        size(x by z)  doors");
            foreach (var r in layout.Rooms)
                sb.AppendLine($"  {r.Id,3}  {r.Role,-8} {r.Type,-14} {r.TemplateName,-15} {r.Shape,-10} ({r.Center.X,7:F1},{r.Center.Z,7:F1})  {r.SizeX,5:F1} x {r.SizeZ,-5:F1}  {r.DoorCount}");
            sb.AppendLine();
            sb.AppendLine("CORRIDORS  id  rooms   dir    length  width  from(x,z)        to(x,z)");
            foreach (var c in layout.Corridors)
            {
                var a = c.Path[0]; var b = c.Path[c.Path.Count - 1];
                sb.AppendLine($"  {c.Id,3}  {c.RoomA,2}->{c.RoomB,-2}  {c.DirectionFromA,-6} {c.Length,6:F2}  {c.Width,5:F2}  ({a.X,7:F1},{a.Z,7:F1})  ({b.X,7:F1},{b.Z,7:F1})");
            }
            if (layout.Corridors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"  corridor length  min {layout.Corridors.Min(c => c.Length):F2}  max {layout.Corridors.Max(c => c.Length):F2}  avg {layout.Corridors.Average(c => c.Length):F2}");
            }
            return sb.ToString();
        }

        static string Sanitise(string name)
        {
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            return string.IsNullOrEmpty(name) ? "mesh" : name;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
