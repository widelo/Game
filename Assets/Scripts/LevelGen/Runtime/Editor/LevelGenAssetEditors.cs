// Editor-only inspectors for the two authoring assets. Nothing here runs in a player build (this assembly is
// Editor-only), and nothing here is required for the level to generate - it exists so a teammate dropping in
// their own room/furniture/light prefabs is told, at authoring time, when the numbers cannot work.
//
// Deliberately NOT here: render-pipeline settings. Pixel light count / rendering path live on the URP asset
// and the renderer asset, which another part of the project owns.
using LevelGen.Core;
using UnityEditor;
using UnityEngine;

namespace LevelGen.Unity.Editor
{
    [CustomEditor(typeof(LevelGenProfile))]
    public sealed class LevelGenProfileEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var p = (LevelGenProfile)target;

            EditorGUILayout.Space();

            if (p.maxRoomSize >= p.cellSize)
                EditorGUILayout.HelpBox(
                    $"maxRoomSize ({p.maxRoomSize}) must be smaller than cellSize ({p.cellSize}), otherwise " +
                    "adjacent rooms touch and corridors have zero or negative length.", MessageType.Error);

            if (p.minRoomSize > p.maxRoomSize)
                EditorGUILayout.HelpBox("minRoomSize is greater than maxRoomSize.", MessageType.Error);

            if (p.minRooms > p.maxRooms)
                EditorGUILayout.HelpBox("minRooms is greater than maxRooms.", MessageType.Error);

            int cells = Mathf.Max(1, p.gridWidth) * Mathf.Max(1, p.gridHeight);
            if (p.maxRooms > cells)
                EditorGUILayout.HelpBox(
                    $"maxRooms ({p.maxRooms}) exceeds the {cells} cells in the {p.gridWidth}x{p.gridHeight} " +
                    "lattice; the generator will place fewer rooms than asked.", MessageType.Warning);

            if (p.keyRooms + 1 > p.minRooms)
                EditorGUILayout.HelpBox(
                    $"minRooms ({p.minRooms}) leaves no room for {p.keyRooms} key rooms plus the spawn room.",
                    MessageType.Warning);

            if (p.corridorWidth + 2f * p.wallThickness >= p.minRoomSize)
                EditorGUILayout.HelpBox(
                    "corridorWidth + 2 * wallThickness is at least as wide as the smallest room side, so a " +
                    "corridor mouth would consume a whole wall.", MessageType.Warning);

            ValidateTemplates(p);
            ValidatePrefabMaps(p);
        }

        void ValidateTemplates(LevelGenProfile p)
        {
            if (p.templates == null || p.templates.Count == 0) return;

            var seen = new System.Collections.Generic.HashSet<string>();
            bool anySpawnCapable = false;

            for (int i = 0; i < p.templates.Count; i++)
            {
                RoomTemplateAsset t = p.templates[i];
                if (t == null)
                {
                    EditorGUILayout.HelpBox($"Template slot {i} is empty; it will be ignored.", MessageType.Warning);
                    continue;
                }

                if (!seen.Add(t.SpecName))
                    EditorGUILayout.HelpBox($"Two templates are named '{t.SpecName}'. Names must be unique - " +
                                            "Room.TemplateName is how the builder finds the prefab.",
                                            MessageType.Error);

                if (t.AllowedDoors == DoorSides.None)
                    EditorGUILayout.HelpBox($"'{t.SpecName}' allows no doors at all, so it can never be placed.",
                                            MessageType.Error);

                if (t.prefab != null && (t.fixedSizeX <= 0f || t.fixedSizeZ <= 0f))
                    EditorGUILayout.HelpBox($"'{t.SpecName}' has a prefab but no fixed footprint. Set " +
                                            "fixedSizeX / fixedSizeZ to the prefab's real size in metres, or " +
                                            "the generated floor and corridor mouths will not line up with it.",
                                            MessageType.Error);

                if (t.fixedSizeX > p.cellSize || t.fixedSizeZ > p.cellSize)
                    EditorGUILayout.HelpBox($"'{t.SpecName}' is larger than cellSize ({p.cellSize} m); it will " +
                                            "collide with the neighbouring cells.", MessageType.Error);

                if (!t.generateFloor)
                    EditorGUILayout.HelpBox($"'{t.SpecName}' generates no floor. Unless the prefab contains a " +
                                            "floor COLLIDER, the NavMesh will have nothing to bake on in that room.",
                                            MessageType.Warning);

                if (t.affinity != RoleAffinity.SpawnOnly && t.affinity != RoleAffinity.NeverSpawn) anySpawnCapable = true;
                if (t.affinity == RoleAffinity.SpawnOnly) anySpawnCapable = true;
            }

            if (!anySpawnCapable)
                EditorGUILayout.HelpBox("No template can be used for the spawn room; it will fall back to " +
                                        "Procedural (which is fine).", MessageType.Info);
        }

        void ValidatePrefabMaps(LevelGenProfile p)
        {
            if (p.propPrefabs != null)
            {
                var seen = new System.Collections.Generic.HashSet<PropType>();
                for (int i = 0; i < p.propPrefabs.Length; i++)
                {
                    var e = p.propPrefabs[i];
                    if (e == null) continue;
                    if (e.prefab == null)
                        EditorGUILayout.HelpBox($"Prop prefab slot for {e.type} is empty; that archetype " +
                                                "falls back to generated primitives.", MessageType.Info);
                    else if (!seen.Add(e.type))
                        EditorGUILayout.HelpBox($"{e.type} is mapped more than once; the last entry wins.",
                                                MessageType.Warning);
                }
            }

            if (p.lightPrefabs == null) return;
            var lightsSeen = new System.Collections.Generic.HashSet<LevelGen.Core.LightType>();
            for (int i = 0; i < p.lightPrefabs.Length; i++)
            {
                var e = p.lightPrefabs[i];
                if (e == null) continue;
                if (e.prefab == null)
                {
                    EditorGUILayout.HelpBox($"Light prefab slot for {e.type} is empty; that fixture kind falls " +
                                            "back to generated primitives.", MessageType.Info);
                    continue;
                }
                if (!lightsSeen.Add(e.type))
                    EditorGUILayout.HelpBox($"{e.type} is mapped more than once; the last entry wins.",
                                            MessageType.Warning);
                if (e.prefab.GetComponentInChildren<Light>(true) == null)
                    EditorGUILayout.HelpBox($"'{e.prefab.name}' contains no Light component anywhere in its " +
                                            "hierarchy, so the fixture would be placed but emit nothing.",
                                            MessageType.Error);
            }
        }
    }

    [CustomEditor(typeof(RoomTemplateAsset))]
    public sealed class RoomTemplateAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var t = (RoomTemplateAsset)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Resolved spec name", t.SpecName);
            EditorGUILayout.LabelField("Allowed doors", t.AllowedDoors.ToString());

            if (t.prefab != null && (t.generateWalls || t.generateCeiling))
                EditorGUILayout.HelpBox("This template has a room prefab but still generates walls/ceiling. " +
                                        "If the prefab brings its own, you will get both sets of geometry.",
                                        MessageType.Warning);

            if (t.minDoors > t.maxDoors)
                EditorGUILayout.HelpBox("minDoors is greater than maxDoors; the spec clamps maxDoors up.",
                                        MessageType.Warning);

            if (GUI.changed) EditorUtility.SetDirty(t);

            EditorGUILayout.Space();
            if (GUILayout.Button("Configure as prefab room (floor only)"))
            {
                Undo.RecordObject(t, "Configure as prefab room");
                t.ConfigureAsPrefabRoom();
                EditorUtility.SetDirty(t);
            }
        }
    }
}
