// Every authorable knob of one generated level, in one asset.
//
// WHY: LevelGenSettings is pure C# (LevelGen.Core has noEngineReferences) so it cannot be a serialized field,
// and the old LevelBuilder mirrored it knob-for-knob as private [SerializeField]s. That meant settings lived
// in the SCENE: two people could not tune a level without a scene merge conflict, and nothing could be
// swapped at runtime. Everything except the seed policy now lives here instead, and LevelBuilder holds one
// reference to it. Create one with Assets > Create > CS462 > Level Gen Profile.
//
// A null profile on the builder is legal: it creates a transient default instance in code, so a scene with a
// bare LevelBuilder still generates the stock level with zero assets authored.
using System;
using System.Collections.Generic;
using LevelGen.Core;
using UnityEngine;
using CoreLightType = LevelGen.Core.LightType;

namespace LevelGen.Unity
{
    /// <summary>Maps one furniture archetype onto a teammate's prefab. Unmapped types fall back to primitives.</summary>
    [Serializable]
    public sealed class PropPrefabEntry
    {
        public PropType type = PropType.HospitalBed;

        [Tooltip("Instantiated at the prop's world position and Y rotation. Author it with its footprint " +
                 "centred on the origin and its base at y = 0.")]
        public GameObject prefab;

        [Tooltip("Add a root BoxCollider sized from the Core footprint when the prefab contains no collider " +
                 "at all. Turn this off for a prefab whose own colliders are authored (or that is decoration " +
                 "the player should walk through).")]
        public bool addColliderIfMissing = true;
    }

    /// <summary>Maps one light fixture kind onto a teammate's prefab. Unmapped kinds fall back to primitives.</summary>
    [Serializable]
    public sealed class LightPrefabEntry
    {
        public CoreLightType type = CoreLightType.CeilingLamp;

        [Tooltip("Prefab containing a Light component SOMEWHERE in its hierarchy. Ceiling fixtures are " +
                 "placed with their root at the ceiling plane and are expected to hang downwards; desk lamps " +
                 "and sconces are placed with their root at the fixture height.")]
        public GameObject prefab;

        [Tooltip("Copy colour / intensity / range from the generated LightSource onto the prefab's Light. " +
                 "Turn this off to keep the prefab's own hand-tuned light values (the generator still " +
                 "switches the Light off for a dead fixture and still attaches the flicker).")]
        public bool applyDataToLight = true;
    }

    [CreateAssetMenu(menuName = "CS462/Level Gen Profile", fileName = "LevelGenProfile")]
    public sealed class LevelGenProfile : ScriptableObject
    {
        // ------------------------------------------------------------------ layout (LevelGenSettings)
        [Header("Layout")]
        [Min(1)] public int minRooms = 5;
        [Min(1)] public int maxRooms = 9;
        [Min(0)] public int keyRooms = 3;

        [Tooltip("Lattice pitch: distance between adjacent cell centres. Must exceed maxRoomSize so corridors " +
                 "have positive length.")]
        public float cellSize = 30f;
        public float minRoomSize = 7f;
        public float maxRoomSize = 20f;
        public float corridorWidth = 3.2f;
        [Range(0f, 1f)] public float loopEdgeChance = 0.35f;
        [Min(1)] public int gridWidth = 4;
        [Min(1)] public int gridHeight = 4;
        [Range(0f, 1f)] public float ellipseChance = 0.4f;

        [Tooltip("Maximum corridors on the spawn/exit room. 1 = the spawn room is a dead-end leaf with one doorway.")]
        [Min(1)] public int spawnMaxDoors = 1;

        // ------------------------------------------------------------------ contents (LevelGenSettings)
        [Header("Contents")]
        public float ceilingHeight = 3.0f;

        [Tooltip("Extra width added to corridorWidth for the clear lane from each corridor mouth to the room " +
                 "centre. No prop may intrude into it; this is what keeps every mouth reachable.")]
        public float mouthClearance = 2.0f;

        public float propSpacing = 0.6f;
        public float lightAreaPerLamp = 45f;
        [Min(1)] public int maxLightsPerRoom = 4;
        [Range(0f, 1f)] public float darkRoomChance = 0.15f;
        [Range(0f, 1f)] public float flickerChance = 0.25f;
        public float corridorLightSpacing = 7f;

        // ------------------------------------------------------------------ templates
        [Header("Room templates")]
        [Tooltip("Templates the generator may place. Empty = every room is procedural. Cells that no template " +
                 "fits fall back to Procedural, so a short list is safe.")]
        public List<RoomTemplateAsset> templates = new List<RoomTemplateAsset>();

        // ------------------------------------------------------------------ prefab maps
        [Header("Prefab libraries")]
        [Tooltip("Furniture prefabs by archetype. Any archetype left unmapped is built from primitives.")]
        public PropPrefabEntry[] propPrefabs = new PropPrefabEntry[0];

        [Tooltip("Light prefabs by fixture kind. Any kind left unmapped is built from primitives + a runtime Light.")]
        public LightPrefabEntry[] lightPrefabs = new LightPrefabEntry[0];

        // ------------------------------------------------------------------ geometry / materials
        [Header("Geometry")]
        public float wallThickness = 0.25f;
        [Tooltip("Edge length of the spawn / key placeholder cubes.")]
        public float markerSize = 0.5f;

        [Header("Materials (empty = generated URP Lit defaults)")]
        public Material floorMaterial;
        public Material wallMaterial;
        public Material corridorFloorMaterial;
        public Material ceilingMaterial;

        // ------------------------------------------------------------------ render settings
        [Header("Lighting / atmosphere")]
        [Tooltip("Zero the intensity of every Directional Light in the scene. Indoor horror wants no sun.")]
        public bool killDirectionalLight = true;
        [ColorUsage(false, true)] public Color ambientLight = new Color(0.015f, 0.015f, 0.02f, 1f);
        public bool fogEnabled = true;
        public float fogDensity = 0.028f;
        public Color fogColor = new Color(0.02f, 0.02f, 0.025f, 1f);

        // ------------------------------------------------------------------ nav
        [Header("NavMesh")]
        public bool bakeNavMesh = true;
        [Tooltip("NavMesh agent type id the runtime surface is baked for. 0 = Humanoid.")]
        public int agentTypeId = 0;

        // ------------------------------------------------------------------ conversion

        /// <summary>
        /// The pure-C# settings LevelGen.Core consumes, including the template list projected to
        /// <see cref="RoomTemplateSpec"/>. Null and duplicate-named template entries are dropped with a warning
        /// rather than handed to the generator.
        /// </summary>
        public LevelGenSettings ToSettings()
        {
            var s = new LevelGenSettings
            {
                MinRooms = minRooms,
                MaxRooms = Mathf.Max(minRooms, maxRooms),
                KeyRooms = keyRooms,
                CellSize = cellSize,
                MinRoomSize = minRoomSize,
                MaxRoomSize = maxRoomSize,
                CorridorWidth = corridorWidth,
                CeilingHeight = ceilingHeight,
                MouthClearance = mouthClearance,
                PropSpacing = propSpacing,
                LightAreaPerLamp = lightAreaPerLamp,
                MaxLightsPerRoom = maxLightsPerRoom,
                DarkRoomChance = darkRoomChance,
                FlickerChance = flickerChance,
                CorridorLightSpacing = corridorLightSpacing,
                LoopEdgeChance = loopEdgeChance,
                GridWidth = gridWidth,
                GridHeight = gridHeight,
                EllipseChance = ellipseChance,
                SpawnMaxDoors = spawnMaxDoors,
                Templates = new List<RoomTemplateSpec>()
            };

            RebuildLookups();
            foreach (var kv in templateByName)
                s.Templates.Add(kv.Value.ToSpec());

            return s;
        }

        // ------------------------------------------------------------------ lookups
        //
        // Rebuilt on every ToSettings() (i.e. once per Build) so inspector edits take effect on the next
        // rebuild without any change-tracking. Not serialized.

        [NonSerialized] Dictionary<string, RoomTemplateAsset> templateByName;
        [NonSerialized] Dictionary<PropType, PropPrefabEntry> propByType;
        [NonSerialized] Dictionary<CoreLightType, LightPrefabEntry> lightByType;

        /// <summary>Rebuild the name/type lookups. Safe to call repeatedly; called for you by <see cref="ToSettings"/>.</summary>
        public void RebuildLookups()
        {
            templateByName = new Dictionary<string, RoomTemplateAsset>();
            propByType = new Dictionary<PropType, PropPrefabEntry>();
            lightByType = new Dictionary<CoreLightType, LightPrefabEntry>();

            if (templates != null)
            {
                for (int i = 0; i < templates.Count; i++)
                {
                    RoomTemplateAsset t = templates[i];
                    if (t == null) continue;
                    if (templateByName.ContainsKey(t.SpecName))
                    {
                        Debug.LogWarning($"[LevelGenProfile] duplicate room template name '{t.SpecName}' " +
                                         $"on '{name}'; the later entry is ignored.");
                        continue;
                    }
                    templateByName[t.SpecName] = t;
                }
            }

            if (propPrefabs != null)
                for (int i = 0; i < propPrefabs.Length; i++)
                {
                    var e = propPrefabs[i];
                    if (e == null || e.prefab == null) continue;
                    propByType[e.type] = e;
                }

            if (lightPrefabs != null)
                for (int i = 0; i < lightPrefabs.Length; i++)
                {
                    var e = lightPrefabs[i];
                    if (e == null || e.prefab == null) continue;
                    lightByType[e.type] = e;
                }
        }

        /// <summary>The asset behind a <see cref="Room.TemplateName"/>, or null for "Procedural" / an unknown name.</summary>
        public RoomTemplateAsset FindTemplate(string specName)
        {
            if (string.IsNullOrEmpty(specName)) return null;
            if (templateByName == null) RebuildLookups();
            RoomTemplateAsset t;
            return templateByName.TryGetValue(specName, out t) ? t : null;
        }

        /// <summary>The prefab mapping for a furniture archetype, or null to build it from primitives.</summary>
        public PropPrefabEntry FindPropPrefab(PropType type)
        {
            if (propByType == null) RebuildLookups();
            PropPrefabEntry e;
            return propByType.TryGetValue(type, out e) ? e : null;
        }

        /// <summary>The prefab mapping for a light fixture kind, or null to build it from primitives.</summary>
        public LightPrefabEntry FindLightPrefab(CoreLightType type)
        {
            if (lightByType == null) RebuildLookups();
            LightPrefabEntry e;
            return lightByType.TryGetValue(type, out e) ? e : null;
        }

        /// <summary>
        /// A profile with the stock values, not saved to disk. <see cref="LevelBuilder"/> uses this when no
        /// profile asset is assigned, so a bare component in a scene still builds the default level.
        /// </summary>
        public static LevelGenProfile CreateTransientDefault()
        {
            var p = CreateInstance<LevelGenProfile>();
            p.name = "LevelGenProfile (transient default)";
            return p;
        }
    }
}
