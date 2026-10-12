// Authoring asset for one LevelGen.Core.RoomTemplateSpec, plus the Unity-side things a spec cannot hold:
// the room prefab, its offset, and a floor material override.
//
// WHY AN ASSET: RoomTemplateSpec is pure C# (LevelGen.Core has noEngineReferences), so it cannot carry a
// GameObject reference and cannot be a serialized field. A teammate who wants their hand-built room in the
// rotation creates one of these, points `prefab` at it, ticks the sides that have a doorway, and drops the
// asset into the LevelGenProfile's template list. No code changes.
//
// PREFAB AUTHORING CONTRACT (what the generator assumes about `prefab`):
//  * floor at y = 0 and the room CENTRED on the prefab's origin - the builder places the root at the room
//    centre with identity rotation, so an off-centre prefab lands off-centre in the level;
//  * `fixedSizeX` / `fixedSizeZ` must match the prefab's real footprint in metres, because the generator
//    reserves exactly that footprint on the lattice and cuts the corridor mouths against it;
//  * doorways must sit on the CENTRE of the sides ticked in `allowedDoors`: that is where the generator puts
//    the corridor mouth (Room.BoundaryPoint), and the corridor is `LevelGenSettings.CorridorWidth` wide;
//  * colliders in the prefab are collected by the NavMesh bake (the prefab ends up under "Level", and the
//    NavMeshSurface collects its children's physics colliders), so walls want colliders and decoration does not.
using LevelGen.Core;
using UnityEngine;

namespace LevelGen.Unity
{
    [CreateAssetMenu(menuName = "CS462/Room Template", fileName = "RoomTemplate")]
    public sealed class RoomTemplateAsset : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Unique name. Room.TemplateName refers to it; the builder maps it back to this asset. " +
                 "Leave empty to use the asset's file name.")]
        public string templateName = "";

        [Tooltip("Relative selection weight among the templates eligible for a cell. 0 = never picked.")]
        [Min(0f)] public float weight = 1f;

        [Tooltip("Which room roles this template may be used for.")]
        public RoleAffinity affinity = RoleAffinity.Any;

        [Header("Footprint")]
        [Tooltip("Fixed footprint along X in metres. 0 = let the generator roll a procedural size. " +
                 "A prefab room MUST set this to the prefab's real width.")]
        [Min(0f)] public float fixedSizeX = 0f;

        [Tooltip("Fixed footprint along Z in metres. 0 = procedural.")]
        [Min(0f)] public float fixedSizeZ = 0f;

        [Tooltip("Shape used when the footprint is procedural. Prefab rooms are always rectangles.")]
        public RoomShape shape = RoomShape.Rectangle;
        [Tooltip("Off = keep whatever shape the generator rolled (rectangle or ellipse). On = force the shape above.")]
        public bool overrideShape = false;

        [Header("Doors")]
        [Tooltip("North (+Z) side may receive a corridor.")] public bool doorNorth = true;
        [Tooltip("East (+X) side may receive a corridor.")]  public bool doorEast  = true;
        [Tooltip("South (-Z) side may receive a corridor.")] public bool doorSouth = true;
        [Tooltip("West (-X) side may receive a corridor.")]  public bool doorWest  = true;

        [Tooltip("Fewest corridors this template tolerates. A one-doorway prefab sets min = max = 1.")]
        [Min(0)] public int minDoors = 1;
        [Tooltip("Most corridors this template tolerates.")]
        [Min(0)] public int maxDoors = 4;

        [Header("Archetype")]
        [Tooltip("Force the hospital archetype instead of letting the furnisher roll one.")]
        public bool useForcedType = false;
        public RoomType forcedType = RoomType.PatientRoom;

        [Header("What the procedural pipeline still generates")]
        [Tooltip("Floor mesh + collider. Keep this on even for prefab rooms: it is the floor the NavMesh bakes on.")]
        public bool generateFloor = true;
        [Tooltip("Mitred wall ring with corridor openings. Off for a prefab that brings its own walls.")]
        public bool generateWalls = true;
        public bool generateCeiling = true;
        [Tooltip("Furniture from Room.Props. Off for a prefab that is already dressed.")]
        public bool generateProps = true;
        [Tooltip("Light fixtures from Room.Lights. Off for a prefab that brings its own lights.")]
        public bool generateLights = true;

        [Header("Unity side")]
        [Tooltip("Hand-built room prefab. Instantiated at the room centre with identity rotation and y = 0. " +
                 "Leave empty for a purely procedural template.")]
        public GameObject prefab;

        [Tooltip("Added to the room centre when placing the prefab. Use it to nudge a prefab whose origin is " +
                 "not at its floor centre, instead of re-authoring the prefab.")]
        public Vector3 prefabOffset = Vector3.zero;

        [Tooltip("Material for the generated floor under this template. Empty = the profile's floor material.")]
        public Material floorMaterialOverride;

        /// <summary>The name the generator and Room.TemplateName use: <see cref="templateName"/> or the asset name.</summary>
        public string SpecName => string.IsNullOrEmpty(templateName) ? name : templateName;

        /// <summary>Which sides may receive a corridor, composed from the four bools.</summary>
        public DoorSides AllowedDoors
        {
            get
            {
                DoorSides d = DoorSides.None;
                if (doorNorth) d |= DoorSides.North;
                if (doorEast)  d |= DoorSides.East;
                if (doorSouth) d |= DoorSides.South;
                if (doorWest)  d |= DoorSides.West;
                return d;
            }
        }

        /// <summary>Project this asset onto the pure-C# contract LevelGen.Core consumes.</summary>
        public RoomTemplateSpec ToSpec()
        {
            return new RoomTemplateSpec
            {
                Name = SpecName,
                Weight = weight,
                Affinity = affinity,
                FixedSizeX = fixedSizeX,
                FixedSizeZ = fixedSizeZ,
                Shape = prefab != null ? RoomShape.Rectangle : shape,
                OverrideShape = overrideShape,
                AllowedDoors = AllowedDoors,
                MinDoors = Mathf.Max(0, minDoors),
                MaxDoors = Mathf.Max(Mathf.Max(0, minDoors), maxDoors),
                ForcedType = useForcedType ? forcedType : (RoomType?)null,
                GenerateFloor = generateFloor,
                GenerateWalls = generateWalls,
                GenerateCeiling = generateCeiling,
                GenerateProps = generateProps,
                GenerateLights = generateLights
            };
        }

        /// <summary>
        /// One-click setup for the common case: a hand-built prefab room with a single doorway. Called by the
        /// custom inspector's "Configure as prefab room" button.
        /// </summary>
        public void ConfigureAsPrefabRoom()
        {
            generateFloor = true;
            generateWalls = false;
            generateCeiling = false;
            generateProps = false;
            generateLights = false;
            shape = RoomShape.Rectangle;
        }
    }
}
