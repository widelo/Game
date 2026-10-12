// v3 contract: room templates and door constraints. Pure C#.
// A template describes a KIND of room the generator may place: either fully procedural (walls/furniture/lights
// generated from the archetype) or prefab-backed (a teammate's hand-built room prefab; the generator still
// owns the room's footprint, its position on the lattice, which sides get doors, and the floor under it).
using System;
using System.Collections.Generic;

namespace LevelGen.Core
{
    [Flags]
    public enum DoorSides
    {
        None  = 0,
        North = 1 << 0,
        East  = 1 << 1,
        South = 1 << 2,
        West  = 1 << 3,
        All   = North | East | South | West
    }

    public static class DoorSidesExtensions
    {
        public static DoorSides ToFlag(this Cardinal c)
        {
            switch (c)
            {
                case Cardinal.North: return DoorSides.North;
                case Cardinal.East:  return DoorSides.East;
                case Cardinal.South: return DoorSides.South;
                default:             return DoorSides.West;
            }
        }
        public static bool Allows(this DoorSides sides, Cardinal c) => (sides & c.ToFlag()) != 0;
    }

    /// <summary>Which roles a template may be used for.</summary>
    public enum RoleAffinity
    {
        Any,
        SpawnOnly,
        KeyOnly,
        NormalOnly,
        NeverSpawn
    }

    public sealed class RoomTemplateSpec
    {
        /// <summary>Unique name; Room.TemplateName refers to it. The Unity layer maps it to a prefab/asset.</summary>
        public string Name = "Procedural";

        /// <summary>Relative selection weight among templates eligible for a cell. 0 = never picked at random.</summary>
        public double Weight = 1.0;

        public RoleAffinity Affinity = RoleAffinity.Any;

        /// <summary>Fixed footprint when > 0; otherwise the generator rolls SizeX/SizeZ from the settings range.</summary>
        public double FixedSizeX = 0;
        public double FixedSizeZ = 0;

        /// <summary>Shape forced on the room when <see cref="OverrideShape"/> is true. Fixed-footprint (prefab) rooms are always Rectangle.</summary>
        public RoomShape Shape = RoomShape.Rectangle;

        /// <summary>False = keep the shape the generator rolled (rectangle or ellipse); true = force <see cref="Shape"/>.</summary>
        public bool OverrideShape = false;

        /// <summary>Which sides of this room may receive a corridor. A hand-built prefab with one doorway sets exactly one flag.</summary>
        public DoorSides AllowedDoors = DoorSides.All;

        /// <summary>Door-count bounds. The generator only places this template on a lattice cell whose corridors fit.</summary>
        public int MinDoors = 1;
        public int MaxDoors = 4;

        /// <summary>Archetype forced when set; otherwise rolled by the furnisher.</summary>
        public RoomType? ForcedType = null;

        /// <summary>What the procedural pipeline still generates for this room. Prefab rooms usually keep the floor only.</summary>
        public bool GenerateFloor = true;
        public bool GenerateWalls = true;
        public bool GenerateCeiling = true;
        public bool GenerateProps = true;
        public bool GenerateLights = true;

        public static RoomTemplateSpec Procedural() => new RoomTemplateSpec();

        /// <summary>Convenience for a hand-built room prefab: fixed footprint, floor under it, nothing else generated.</summary>
        public static RoomTemplateSpec Prefab(string name, double sizeX, double sizeZ, DoorSides doors, int maxDoors = 4, RoleAffinity affinity = RoleAffinity.Any)
        {
            return new RoomTemplateSpec
            {
                Name = name, FixedSizeX = sizeX, FixedSizeZ = sizeZ, Shape = RoomShape.Rectangle,
                AllowedDoors = doors, MinDoors = 1, MaxDoors = maxDoors, Affinity = affinity,
                GenerateFloor = true, GenerateWalls = false, GenerateCeiling = false, GenerateProps = false, GenerateLights = false
            };
        }
    }

    /// <summary>Pluggable furnishing stage. The default is RoomFurnisher; teammates can replace or wrap it.</summary>
    public interface ILevelFurnisher
    {
        void Furnish(LevelLayout layout, Random rnd);
    }
}
