// v2 contract additions: hospital room archetypes, furniture props, and light sources.
// Pure C# (no UnityEngine). All positions are WORLD-space XZ (same frame as Room.Center), heights in metres from floor.
// Owned by LevelGen.Core; consumed by LevelGen.Unity (meshes/lights) and later by gameplay (key spawn on furniture).
using System.Collections.Generic;

namespace LevelGen.Core
{
    /// <summary>Hospital archetype. Drives furniture and light selection; does not change geometry.</summary>
    public enum RoomType
    {
        PatientRoom,   // 1-2 beds against walls, bedside cabinet, maybe IV stand
        Ward,          // many beds in rows along the long walls
        Office,        // desk + chair, filing cabinet, desk lamp
        NurseStation,  // long counter, chairs, cabinets
        Storage,       // shelves/cabinets along walls, dim
        Lobby          // sparse: benches, reception counter (usually the spawn room)
    }

    public enum PropType
    {
        HospitalBed,    // ~0.9 x 2.1 m footprint, 0.6 m high
        BedsideCabinet, // ~0.5 x 0.5, 0.8 high
        Desk,           // ~1.6 x 0.8, 0.75 high
        Chair,          // ~0.5 x 0.5, 0.9 high
        FilingCabinet,  // ~0.5 x 0.6, 1.4 high
        Shelf,          // ~1.2 x 0.4, 1.8 high
        Counter,        // ~2.4 x 0.7, 1.0 high
        Bench,          // ~1.8 x 0.5, 0.45 high
        IvStand,        // ~0.4 x 0.4, 1.8 high (thin pole)
        Wheelchair,     // ~0.7 x 1.0, 1.0 high
        Gurney          // ~0.8 x 2.0, 0.9 high (corridors)
    }

    /// <summary>
    /// A furniture piece. Footprint is an axis-aligned box in the prop's LOCAL frame (SizeX along its local X),
    /// then rotated by RotationDeg about Y (0 = local X along world +X; 90 = local X along world +Z) and placed at Position.
    /// Props never overlap each other, never intersect walls, and keep every corridor mouth clear (see LevelGenSettings.MouthClearance).
    /// </summary>
    public sealed class Prop
    {
        public PropType Type;
        public Vec2 Position;     // world XZ of footprint centre
        public double RotationDeg; // multiples of 90 in v2
        public double SizeX;      // local footprint along local X
        public double SizeZ;      // local footprint along local Z
        public double Height;     // metres from floor

        /// <summary>World-space axis-aligned bounds (valid because rotations are multiples of 90).</summary>
        public void WorldHalfExtents(out double halfX, out double halfZ)
        {
            bool swap = ((int)System.Math.Round(RotationDeg / 90.0)) % 2 != 0;
            halfX = (swap ? SizeZ : SizeX) * 0.5;
            halfZ = (swap ? SizeX : SizeZ) * 0.5;
        }
    }

    public enum LightType
    {
        CeilingLamp,  // hangs from ceiling; omni point light; the main room light
        DeskLamp,     // sits on a Desk/Counter/BedsideCabinet prop; small warm point light
        WallSconce,   // mounted on a wall at ~2.2 m; low intensity
        CorridorStrip // ceiling fixture in corridors; cool, often flickering or dead
    }

    public enum LightTint
    {
        WarmIncandescent, // ~2700K
        CoolFluorescent,  // ~5000K, slightly green
        SicklyGreen,      // horror accent
        Red               // emergency / exit
    }

    public sealed class LightSource
    {
        public LightType Type;
        public LightTint Tint;
        public Vec2 Position;   // world XZ
        public double Height;   // metres from floor (ceiling lamps: ceiling height - fixture drop)
        public double Intensity; // Unity point-light intensity, roughly 0.6 .. 2.5
        public double Range;     // metres, roughly 5 .. 14
        public bool Flicker;     // builder animates intensity
        public bool Enabled;     // false = fixture exists but is dead (dark room horror beat)
        public int AttachedPropIndex = -1; // for DeskLamp: index into Room.Props it sits on, else -1
    }
}
