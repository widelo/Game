// Pure C# data contract for a generated level. NO UnityEngine dependency.
// Units: metres. X = east, Z = north (Unity world XZ plane, Y is up and always 0 for this flat layout).
// Owned by LevelGen.Core. Consumers: LevelGen.Unity (mesh building), enemy AI (room graph, spawn points).
using System;
using System.Collections.Generic;

namespace LevelGen.Core
{
    public enum RoomShape
    {
        Rectangle,
        Ellipse
    }

    public enum RoomRole
    {
        Normal,
        Spawn,   // player start AND exit point (GDD: return to spawn with all keys to escape)
        Key      // one of the 3 rooms containing a key
    }

    /// <summary>Simple 2D vector in the XZ plane. Kept Unity-free so Core is unit-testable without the editor.</summary>
    public struct Vec2
    {
        public double X;
        public double Z;
        public Vec2(double x, double z) { X = x; Z = z; }
        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Z + b.Z);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Z - b.Z);
        public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Z * s);
        public double Length => Math.Sqrt(X * X + Z * Z);
        public override string ToString() => $"({X:F2}, {Z:F2})";
    }

    /// <summary>Axis-aligned direction of a corridor leaving a room. Only orthogonal directions exist in v1.</summary>
    public enum Cardinal
    {
        North, // +Z
        East,  // +X
        South, // -Z
        West   // -X
    }

    public sealed class Room
    {
        public int Id;
        public RoomRole Role;
        public RoomShape Shape;

        /// <summary>v2: hospital archetype driving furniture and lights.</summary>
        public RoomType Type = RoomType.PatientRoom;

        /// <summary>v3: name of the RoomTemplateSpec this room was built from ("Procedural" when none).</summary>
        public string TemplateName = "Procedural";

        /// <summary>v3: resolved template (never null after generation).</summary>
        public RoomTemplateSpec Template = RoomTemplateSpec.Procedural();

        /// <summary>Number of corridors attached.</summary>
        public int DoorCount => CorridorIds.Count;

        /// <summary>v2: furniture placed in this room (world XZ). May be empty.</summary>
        public List<Prop> Props = new List<Prop>();

        /// <summary>v2: light fixtures in this room. At least 1 per room (may be disabled/dead).</summary>
        public List<LightSource> Lights = new List<LightSource>();

        /// <summary>Lattice cell this room occupies (integer grid). Adjacent cells differ by exactly 1 in one axis.</summary>
        public int CellX;
        public int CellY;

        /// <summary>World-space centre of the room on the XZ plane.</summary>
        public Vec2 Center;

        /// <summary>Full extent along X and Z. For Rectangle: width/depth. For Ellipse: full axis lengths (2a, 2b).</summary>
        public double SizeX;
        public double SizeZ;

        /// <summary>Half-extent helpers.</summary>
        public double HalfX => SizeX * 0.5;
        public double HalfZ => SizeZ * 0.5;

        /// <summary>Ids of corridors attached to this room.</summary>
        public List<int> CorridorIds = new List<int>();

        /// <summary>
        /// Point on the room boundary where a corridor heading in <paramref name="dir"/> from the centre exits.
        /// Rectangle: centre offset by half-extent on that axis. Ellipse: tip of the semi-axis on that axis.
        /// Identical for both shapes in v1 because corridors always leave through the axis tips.
        /// </summary>
        public Vec2 BoundaryPoint(Cardinal dir)
        {
            switch (dir)
            {
                case Cardinal.North: return new Vec2(Center.X, Center.Z + HalfZ);
                case Cardinal.South: return new Vec2(Center.X, Center.Z - HalfZ);
                case Cardinal.East:  return new Vec2(Center.X + HalfX, Center.Z);
                default:             return new Vec2(Center.X - HalfX, Center.Z);
            }
        }
    }

    /// <summary>
    /// A walkable passage between two rooms. Represented as a polyline so curved/bent corridors can be added later
    /// without changing consumers. In v1 every corridor is exactly 2 points, axis-aligned (both share X or both share Z).
    /// </summary>
    public sealed class Corridor
    {
        public int Id;
        public int RoomA;
        public int RoomB;

        /// <summary>Direction the corridor leaves RoomA in (RoomB is the opposite side).</summary>
        public Cardinal DirectionFromA;

        /// <summary>Centre-line of the corridor floor, from RoomA's boundary to RoomB's boundary.</summary>
        public List<Vec2> Path = new List<Vec2>();

        /// <summary>Walkable width in metres.</summary>
        public double Width;

        /// <summary>v2: ceiling strip lights along the corridor.</summary>
        public List<LightSource> Lights = new List<LightSource>();

        /// <summary>v2: props pushed against corridor walls (gurneys, wheelchairs). Never block the walkway.</summary>
        public List<Prop> Props = new List<Prop>();

        public double Length
        {
            get
            {
                double len = 0;
                for (int i = 1; i < Path.Count; i++) len += (Path[i] - Path[i - 1]).Length;
                return len;
            }
        }
    }

    /// <summary>Tunable knobs. Defaults are the v1 contract; tests pin to these.</summary>
    public sealed class LevelGenSettings
    {
        public int MinRooms = 5;
        public int MaxRooms = 9;
        public int KeyRooms = 3;

        /// <summary>Lattice pitch: distance between adjacent cell centres. Rooms must fit inside a cell with corridor room to spare.</summary>
        public double CellSize = 30.0;

        /// <summary>Room extent range per axis. Max must be < CellSize so corridors have positive length.</summary>
        public double MinRoomSize = 7.0;
        public double MaxRoomSize = 20.0;

        /// <summary>Hospital corridors are wide: 3.2 m clear.</summary>
        public double CorridorWidth = 3.2;

        /// <summary>Floor-to-ceiling height. Hospital standard ~3.0 m.</summary>
        public double CeilingHeight = 3.0;

        /// <summary>
        /// Extra width added to CorridorWidth for the clear LANE that runs from each corridor mouth to the room centre.
        /// No prop may intrude into a lane; this is what guarantees every mouth can reach the centre on the NavMesh.
        /// </summary>
        public double MouthClearance = 1.0;

        /// <summary>Minimum walkable gap kept between any two props and between a prop and the room centre.</summary>
        public double PropSpacing = 0.6;

        /// <summary>Ceiling lamps per room: 1 .. max(1, floor(area / LightAreaPerLamp)) chosen randomly, capped at MaxLightsPerRoom.</summary>
        public double LightAreaPerLamp = 45.0;
        public int MaxLightsPerRoom = 4;

        /// <summary>Probability a room's main lights are all dead (dark room).</summary>
        public double DarkRoomChance = 0.15;
        public double FlickerChance = 0.25;

        /// <summary>Spacing of corridor ceiling strips along the path.</summary>
        public double CorridorLightSpacing = 7.0;

        /// <summary>Probability an extra (non-tree) edge between two adjacent chosen cells is kept, to form loops.</summary>
        public double LoopEdgeChance = 0.35;

        /// <summary>Lattice bounds the random walk is confined to.</summary>
        public int GridWidth = 4;
        public int GridHeight = 4;

        public double EllipseChance = 0.4;

        /// <summary>
        /// v3: maximum corridors on the spawn room. 1 = the spawn/exit room is a dead end with a single doorway
        /// (the generator makes it a leaf of the growth tree and never adds loop edges to it).
        /// </summary>
        public int SpawnMaxDoors = 1;

        /// <summary>
        /// v3: room templates the generator may place. Empty = everything procedural. A template is only placed on a
        /// cell whose corridor count and directions satisfy its MinDoors/MaxDoors/AllowedDoors; cells no template fits
        /// fall back to Procedural.
        /// </summary>
        public List<RoomTemplateSpec> Templates = new List<RoomTemplateSpec>();
    }

    public sealed class LevelLayout
    {
        public int Seed;
        public LevelGenSettings Settings;
        public List<Room> Rooms = new List<Room>();
        public List<Corridor> Corridors = new List<Corridor>();

        /// <summary>v3: how many rooms got each template name ("Procedural" included). Debug/tests only.</summary>
        public Dictionary<string, int> TemplateUsage = new Dictionary<string, int>();

        public Room SpawnRoom => Rooms.Find(r => r.Role == RoomRole.Spawn);
        public IEnumerable<Room> KeyRooms => Rooms.FindAll(r => r.Role == RoomRole.Key);

        public Room GetRoom(int id) => Rooms[id];

        /// <summary>Rooms directly connected to <paramref name="roomId"/> by a corridor.</summary>
        public IEnumerable<int> Neighbours(int roomId)
        {
            foreach (var c in Corridors)
            {
                if (c.RoomA == roomId) yield return c.RoomB;
                else if (c.RoomB == roomId) yield return c.RoomA;
            }
        }
    }
}
