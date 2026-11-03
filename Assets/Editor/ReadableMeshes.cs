using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// The runtime NavMesh bake reads MeshCollider meshes on the CPU, which needs Read/Write enabled on the
    /// model import. In the editor Unity tolerates it with an error log; in a player build the collider is
    /// silently skipped and the enemy walks through that furniture. This scans the prefabs the level profile
    /// uses (room templates, prop and light prefabs) and flips Read/Write on for every mesh a MeshCollider uses.
    /// </summary>
    public static class ReadableMeshes
    {
        [MenuItem("CS462/Make Collider Meshes Readable")]
        public static void FixFromMenu() => EnsureReadable(CollectProfilePrefabs());

        public static int EnsureReadable(IEnumerable<GameObject> prefabs)
        {
            var fixedPaths = new HashSet<string>();
            foreach (var prefab in prefabs)
            {
                if (prefab == null) continue;
                foreach (var mc in prefab.GetComponentsInChildren<MeshCollider>(true))
                {
                    var mesh = mc.sharedMesh;
                    if (mesh == null || mesh.isReadable) continue;
                    string path = AssetDatabase.GetAssetPath(mesh);
                    if (string.IsNullOrEmpty(path) || fixedPaths.Contains(path)) continue;
                    var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (importer == null) continue;
                    importer.isReadable = true;
                    importer.SaveAndReimport();
                    fixedPaths.Add(path);
                    UnityEngine.Debug.Log($"[ReadableMeshes] Read/Write enabled on {path} (used by MeshCollider '{mc.name}' in {prefab.name})");
                }
            }
            return fixedPaths.Count;
        }

        /// <summary>Every prefab referenced by the default level profile: room templates, prop and light prefabs.</summary>
        public static List<GameObject> CollectProfilePrefabs()
        {
            var list = new List<GameObject>();
            var profile = AssetDatabase.LoadAssetAtPath<LevelGen.Unity.LevelGenProfile>(SceneBootstrap.ProfileAssetPath);
            if (profile == null) return list;
            var so = new SerializedObject(profile);
            foreach (string arrayName in new[] { "propPrefabs", "lightPrefabs" })
            {
                var arr = so.FindProperty(arrayName);
                if (arr == null) continue;
                for (int i = 0; i < arr.arraySize; i++)
                {
                    var prefab = arr.GetArrayElementAtIndex(i).FindPropertyRelative("prefab");
                    if (prefab != null && prefab.objectReferenceValue is GameObject go) list.Add(go);
                }
            }
            var templates = so.FindProperty("templates");
            if (templates != null)
                for (int i = 0; i < templates.arraySize; i++)
                    if (templates.GetArrayElementAtIndex(i).objectReferenceValue is ScriptableObject t)
                    {
                        var tp = new SerializedObject(t).FindProperty("prefab");
                        if (tp != null && tp.objectReferenceValue is GameObject go) list.Add(go);
                    }
            return list;
        }
    }
}
