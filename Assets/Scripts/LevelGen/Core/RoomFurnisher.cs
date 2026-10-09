// v2: hospital archetypes, furniture and lighting for a generated LevelLayout.
// Pure C# (no UnityEngine). Called by LevelGenerator.Generate AFTER geometry, on the SAME System.Random
// stream, so determinism from (seed, settings) is preserved.
//
// Every placement is attempt-based: a prop is only committed if it satisfies all the hard rules that
// LevelValidator re-checks (inside the room floor, PropSpacing gap to other props, MouthClearance from
// every corridor mouth of that room, clearance around the room centre). If no attempt fits, the prop is
// silently skipped - small or heavily-connected rooms are simply sparser.
using System;
using System.Collections.Generic;

namespace LevelGen.Core
{
    /// <summary>
    /// The default furnishing stage. v3: a plain (non-static) class so it can implement ILevelFurnisher;
    /// every helper and the entry point stay static for back-compat, and the interface member just forwards.
    /// </summary>
    public sealed class RoomFurnisher : ILevelFurnisher
    {
        void ILevelFurnisher.Furnish(LevelLayout layout, Random rnd) { Furnish(layout, rnd); }


        /// <summary>Footprint of each prop: SizeX x SizeZ metres in the prop's LOCAL frame, plus height.</summary>
        public struct Dims
        {
            public double SizeX;
            public double SizeZ;
            public double Height;
            public Dims(double sx, double sz, double h) { SizeX = sx; SizeZ = sz; Height = h; }
        }

        /// <summary>The one source of truth for prop footprints (mirrors the PropType comments).</summary>
        public static readonly Dictionary<PropType, Dims> PropDims = new Dictionary<PropType, Dims>
        {
            { PropType.HospitalBed,    new Dims(0.9, 2.1, 0.60) },
            { PropType.BedsideCabinet, new Dims(0.5, 0.5, 0.80) },
            { PropType.Desk,           new Dims(1.6, 0.8, 0.75) },
            { PropType.Chair,          new Dims(0.5, 0.5, 0.90) },
            { PropType.FilingCabinet,  new Dims(0.5, 0.6, 1.40) },
            { PropType.Shelf,          new Dims(1.2, 0.4, 1.80) },
            { PropType.Counter,        new Dims(2.4, 0.7, 1.00) },
            { PropType.Bench,          new Dims(1.8, 0.5, 0.45) },
            { PropType.IvStand,        new Dims(0.4, 0.4, 1.80) },
            { PropType.Wheelchair,     new Dims(0.7, 1.0, 1.00) },
            { PropType.Gurney,         new Dims(0.8, 2.0, 0.90) }
        };

        // ---- shared constants, also used by LevelValidator so the two never disagree ----------------
        public const double CentreClearance = 1.5;      // no prop within 1.5 m of the room centre ...
        public const double SpawnCentreClearance = 3.0; // ... 3 m in the spawn room (player spawns there)
        public const double LaneInflate = 0.6;          // props are inflated by this much against a lane
        public const double CorridorWalkway = 1.6;      // clear width that must remain on one side
        public const double CorridorPropEndGap = 1.5;   // corridor props stay this far from both ends
        public const double WallTouchEps = 0.05;        // "against a wall" tolerance
        public const double GeomEps = 1e-6;
        public const int MaxAttempts = 30;

        private const double CeilingDrop = 0.40;
        private const double SconceHeight = 2.20;
        private const double DeskLampRise = 0.35;

        /// <summary>Lighting budget: built-in forward rendering gets expensive fast, so cap per room.</summary>
        public const int MaxDeskLampsPerRoom = 3;
        public const int MaxTotalLightsPerRoom = 7;

        // =============================================================================================
        // public entry point
        // =============================================================================================

        /// <summary>Fills Room.Type/Props/Lights and Corridor.Props/Lights. Consumes <paramref name="rnd"/>.</summary>
        public static void Furnish(LevelLayout layout, Random rnd)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (rnd == null) throw new ArgumentNullException(nameof(rnd));
            var s = layout.Settings ?? new LevelGenSettings();

            AssignTypes(layout, rnd);

            // v3: a room's template decides whether the procedural pipeline furnishes and lights it.
            // A prefab room (GenerateProps/GenerateLights false) is skipped entirely - no draws are made
            // for it, which is why identical settings still give identical layouts.
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                var room = layout.Rooms[i];
                if (room.Template != null && !room.Template.GenerateProps) continue;
                FurnishRoom(layout, room, s, rnd);
            }

            int keyDark = 0;
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                var room = layout.Rooms[i];
                if (room.Template != null && !room.Template.GenerateLights) continue;
                LightRoom(room, s, rnd, ref keyDark);
            }

            for (int i = 0; i < layout.Corridors.Count; i++)
            {
                FurnishCorridor(layout.Corridors[i], s, rnd);
                LightCorridor(layout.Corridors[i], s, rnd);
            }
        }

        // =============================================================================================
        // room types
        // =============================================================================================

        private static void AssignTypes(LevelLayout layout, Random rnd)
        {
            int n = layout.Rooms.Count;
            bool hasAdmin = false;
            for (int i = 0; i < n; i++)
            {
                var r = layout.Rooms[i];
                // v3: a template's ForcedType wins over both the spawn Lobby rule and the random roll.
                if (r.Template != null && r.Template.ForcedType.HasValue)
                {
                    r.Type = r.Template.ForcedType.Value;
                    if (r.Type == RoomType.Office || r.Type == RoomType.NurseStation) hasAdmin = true;
                    continue;
                }
                if (r.Role == RoomRole.Spawn) { r.Type = RoomType.Lobby; continue; }

                bool wardOk = r.SizeX >= 10.0 && r.SizeZ >= 10.0;
                // weights: PatientRoom 35, Ward 20 (only if it fits), Office 20, NurseStation 10, Storage 15
                double wPatient = 35, wWard = wardOk ? 20 : 0, wOffice = 20, wNurse = 10, wStore = 15;
                double total = wPatient + wWard + wOffice + wNurse + wStore;
                double pick = rnd.NextDouble() * total;
                if (pick < wPatient) r.Type = RoomType.PatientRoom;
                else if (pick < wPatient + wWard) r.Type = RoomType.Ward;
                else if (pick < wPatient + wWard + wOffice) r.Type = RoomType.Office;
                else if (pick < wPatient + wWard + wOffice + wNurse) r.Type = RoomType.NurseStation;
                else r.Type = RoomType.Storage;

                if (r.Type == RoomType.Office || r.Type == RoomType.NurseStation) hasAdmin = true;
            }

            // Hospital feel: a level of any size worth the name has at least one admin space.
            if (n >= 6 && !hasAdmin)
            {
                int target = 1 + rnd.Next(n - 1);
                bool forced = layout.Rooms[target].Template != null && layout.Rooms[target].Template.ForcedType.HasValue;
                var pickAdmin = rnd.NextDouble() < 0.5 ? RoomType.Office : RoomType.NurseStation;
                if (!forced) layout.Rooms[target].Type = pickAdmin;
            }
        }

        // =============================================================================================
        // geometry helpers (shared with LevelValidator)
        // =============================================================================================

        public struct Box
        {
            public double MinX, MaxX, MinZ, MaxZ;
            public Box(double minX, double maxX, double minZ, double maxZ)
            { MinX = minX; MaxX = maxX; MinZ = minZ; MaxZ = maxZ; }
        }

        public static Box PropBox(Prop p)
        {
            double hx, hz;
            p.WorldHalfExtents(out hx, out hz);
            return new Box(p.Position.X - hx, p.Position.X + hx, p.Position.Z - hz, p.Position.Z + hz);
        }

        /// <summary>Largest axis gap between two boxes. &gt;= spacing means they are far enough apart.</summary>
        public static double BoxGap(Box a, Box b)
        {
            double gx = Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX);
            double gz = Math.Max(a.MinZ - b.MaxZ, b.MinZ - a.MaxZ);
            return Math.Max(gx, gz);
        }

        /// <summary>Euclidean distance between two axis-aligned boxes (0 if they intersect).</summary>
        public static double BoxDistance(Box a, Box b)
        {
            double gx = Math.Max(0.0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
            double gz = Math.Max(0.0, Math.Max(a.MinZ - b.MaxZ, b.MinZ - a.MaxZ));
            return Math.Sqrt(gx * gx + gz * gz);
        }

        public static double PointBoxDistance(Vec2 p, Box b)
        {
            return BoxDistance(new Box(p.X, p.X, p.Z, p.Z), b);
        }

        /// <summary>Degenerate box spanning an axis-aligned segment (all our mouth lanes are axis-aligned).</summary>
        public static Box SegmentBox(Vec2 a, Vec2 b)
        {
            return new Box(Math.Min(a.X, b.X), Math.Max(a.X, b.X), Math.Min(a.Z, b.Z), Math.Max(a.Z, b.Z));
        }

        public static bool PointInRoom(Room r, Vec2 p, double eps)
        {
            if (r.Shape == RoomShape.Rectangle)
            {
                return p.X >= r.Center.X - r.HalfX - eps && p.X <= r.Center.X + r.HalfX + eps
                    && p.Z >= r.Center.Z - r.HalfZ - eps && p.Z <= r.Center.Z + r.HalfZ + eps;
            }
            double nx = (p.X - r.Center.X) / r.HalfX;
            double nz = (p.Z - r.Center.Z) / r.HalfZ;
            return nx * nx + nz * nz <= 1.0 + eps;
        }

        /// <summary>Rectangle: box inside the floor rect. Ellipse: all four box corners inside the ellipse.</summary>
        public static bool BoxInRoom(Room r, Box b, double eps)
        {
            if (r.Shape == RoomShape.Rectangle)
            {
                return b.MinX >= r.Center.X - r.HalfX - eps && b.MaxX <= r.Center.X + r.HalfX + eps
                    && b.MinZ >= r.Center.Z - r.HalfZ - eps && b.MaxZ <= r.Center.Z + r.HalfZ + eps;
            }
            return PointInRoom(r, new Vec2(b.MinX, b.MinZ), eps)
                && PointInRoom(r, new Vec2(b.MinX, b.MaxZ), eps)
                && PointInRoom(r, new Vec2(b.MaxX, b.MinZ), eps)
                && PointInRoom(r, new Vec2(b.MaxX, b.MaxZ), eps);
        }

        /// <summary>World-space corridor floor rectangle.</summary>
        public static Box CorridorBox(Corridor c)
        {
            var p0 = c.Path[0];
            var p1 = c.Path[c.Path.Count - 1];
            double half = c.Width * 0.5;
            double minX = Math.Min(p0.X, p1.X), maxX = Math.Max(p0.X, p1.X);
            double minZ = Math.Min(p0.Z, p1.Z), maxZ = Math.Max(p0.Z, p1.Z);
            bool alongZ = Math.Abs(p0.X - p1.X) <= 1e-9;
            if (alongZ) { minX -= half; maxX += half; }
            else { minZ -= half; maxZ += half; }
            return new Box(minX, maxX, minZ, maxZ);
        }

        /// <summary>
        /// Width of the walking lane kept clear from a corridor mouth to the room centre:
        /// the corridor's own width plus LevelGenSettings.MouthClearance, which in v2 means
        /// "extra lane width" rather than a radial keep-out ball. (A radial rule let furniture
        /// stagger across the lane and islanded the room centre - see RoomWalkability.)
        /// </summary>
        public static double LaneWidth(LevelGenSettings s) => s.CorridorWidth + s.MouthClearance;

        /// <summary>
        /// The axis-aligned rectangle from a mouth's BoundaryPoint to the room centre, LaneWidth wide
        /// and centred on the mouth axis. No prop may intrude on it.
        /// </summary>
        public static Box MouthLane(Room room, Vec2 mouth, double laneWidth)
        {
            double half = laneWidth * 0.5;
            bool horizontal = Math.Abs(mouth.Z - room.Center.Z) <= Math.Abs(mouth.X - room.Center.X);
            if (horizontal)
            {
                return new Box(Math.Min(mouth.X, room.Center.X), Math.Max(mouth.X, room.Center.X),
                               room.Center.Z - half, room.Center.Z + half);
            }
            return new Box(room.Center.X - half, room.Center.X + half,
                           Math.Min(mouth.Z, room.Center.Z), Math.Max(mouth.Z, room.Center.Z));
        }

        /// <summary>True when the prop (inflated by LaneInflate) stays out of every mouth lane.</summary>
        public static bool ClearsAllLanes(Room room, List<Vec2> mouths, Prop p, double laneWidth)
        {
            if (mouths == null) return true;
            var b = PropBox(p);
            var inflated = new Box(b.MinX - LaneInflate, b.MaxX + LaneInflate,
                                   b.MinZ - LaneInflate, b.MaxZ + LaneInflate);
            for (int i = 0; i < mouths.Count; i++)
            {
                var lane = MouthLane(room, mouths[i], laneWidth);
                if (BoxGap(inflated, lane) < -GeomEps) return false; // overlapping on both axes
            }
            return true;
        }

        /// <summary>Props whose footprint must read as pushed flush against a wall.</summary>
        public static bool MustBeFlush(Room room, PropType type)
        {
            return room.Shape == RoomShape.Rectangle
                   && (type == PropType.HospitalBed || type == PropType.Desk);
        }

        /// <summary>True when the box touches one of the four wall lines within WallTouchEps.</summary>
        public static bool TouchesAWall(Room room, Box b)
        {
            return Math.Abs(b.MinX - (room.Center.X - room.HalfX)) <= WallTouchEps
                || Math.Abs(b.MaxX - (room.Center.X + room.HalfX)) <= WallTouchEps
                || Math.Abs(b.MinZ - (room.Center.Z - room.HalfZ)) <= WallTouchEps
                || Math.Abs(b.MaxZ - (room.Center.Z + room.HalfZ)) <= WallTouchEps;
        }

        /// <summary>Mouth points (room-side corridor entrances) belonging to a room.</summary>
        public static List<Vec2> RoomMouths(LevelLayout layout, Room r)
        {
            var list = new List<Vec2>();
            foreach (int cid in r.CorridorIds)
            {
                if (cid < 0 || cid >= layout.Corridors.Count) continue;
                var c = layout.Corridors[cid];
                if (c.Path == null || c.Path.Count < 2) continue;
                list.Add(c.RoomA == r.Id ? c.Path[0] : c.Path[c.Path.Count - 1]);
            }
            return list;
        }

        // =============================================================================================
        // placement engine
        // =============================================================================================

        private sealed class Ctx
        {
            public Room Room;
            public LevelGenSettings S;
            public Random Rnd;
            public List<Vec2> Mouths;
            public int LastWall;
            public RoomWalkability.Grid Grid;

            public double CentreClear =>
                Room.Role == RoomRole.Spawn ? SpawnCentreClearance : CentreClearance;

            public bool Fits(Prop p, bool centreException)
            {
                var b = PropBox(p);
                if (!BoxInRoom(Room, b, GeomEps)) return false;
                for (int i = 0; i < Room.Props.Count; i++)
                    if (BoxGap(b, PropBox(Room.Props[i])) < S.PropSpacing - GeomEps) return false;
                if (!ClearsAllLanes(Room, Mouths, p, LaneWidth(S))) return false;
                if (MustBeFlush(Room, p.Type) && !TouchesAWall(Room, b)) return false;
                if (!centreException && PointBoxDistance(Room.Center, b) < CentreClear - GeomEps) return false;
                // Hard gate: the prop must not cut the room into pockets (real NavMesh regression).
                if (!RoomWalkability.Check(Grid, Room, Room.Props, p, Mouths)) return false;
                return true;
            }

            public Prop Commit(Prop p) { Room.Props.Add(p); return p; }
        }

        private static Prop MakeProp(PropType type, Vec2 pos, double rotationDeg)
        {
            var d = PropDims[type];
            return new Prop
            {
                Type = type,
                Position = pos,
                RotationDeg = rotationDeg,
                SizeX = d.SizeX,
                SizeZ = d.SizeZ,
                Height = d.Height
            };
        }

        // wall 0 = North(+Z), 1 = East(+X), 2 = South(-Z), 3 = West(-X)
        private static double WallRotation(int wall)
        {
            switch (wall) { case 0: return 0; case 1: return 90; case 2: return 180; default: return 270; }
        }

        private static bool WallIsAlongX(int wall) => wall == 0 || wall == 2;

        /// <summary>Push a wall-anchored prop out to the wall, stepping back inward until it fits the floor.</summary>
        private static Prop TryWallProp(Ctx ctx, PropType type, int wall, double along, bool centreException = false)
        {
            var d = PropDims[type];
            var room = ctx.Room;
            bool alongX = WallIsAlongX(wall);
            double depthHalf = d.SizeZ * 0.5;                       // depth is always the local Z axis
            double limit = alongX ? room.HalfZ : room.HalfX;        // half-extent on the perpendicular axis
            double sign = (wall == 0 || wall == 1) ? 1.0 : -1.0;
            double rot = WallRotation(wall);

            int maxSteps = MustBeFlush(room, type) ? 0 : 40;
            for (int step = 0; step <= maxSteps; step++)
            {
                double offset = limit - depthHalf - step * 0.1;
                if (offset < 0) break;
                Vec2 pos = alongX
                    ? new Vec2(along, room.Center.Z + sign * offset)
                    : new Vec2(room.Center.X + sign * offset, along);
                var p = MakeProp(type, pos, rot);
                if (ctx.Fits(p, centreException)) { ctx.LastWall = wall; return ctx.Commit(p); }
            }
            return null;
        }

        /// <summary>Random "along the wall" coordinate leaving the prop's own footprint inside the room bounds.</summary>
        private static double RandomAlong(Ctx ctx, PropType type, int wall)
        {
            var d = PropDims[type];
            var room = ctx.Room;
            bool alongX = WallIsAlongX(wall);
            double span = alongX ? room.HalfX : room.HalfZ;
            double halfRun = d.SizeX * 0.5;
            double free = span - halfRun;
            double centre = alongX ? room.Center.X : room.Center.Z;
            if (free <= 0) return centre;
            return centre + (ctx.Rnd.NextDouble() * 2.0 - 1.0) * free;
        }

        private static Prop PlaceAgainstWall(Ctx ctx, PropType type, int wall = -1, int attempts = MaxAttempts)
        {
            for (int a = 0; a < attempts; a++)
            {
                int w = wall >= 0 ? wall : ctx.Rnd.Next(4);
                var p = TryWallProp(ctx, type, w, RandomAlong(ctx, type, w));
                if (p != null) return p;
            }
            return null;
        }

        private static Prop PlaceFree(Ctx ctx, PropType type, int attempts = MaxAttempts)
        {
            var d = PropDims[type];
            var room = ctx.Room;
            for (int a = 0; a < attempts; a++)
            {
                double rot = ctx.Rnd.Next(4) * 90.0;
                bool swap = ((int)Math.Round(rot / 90.0)) % 2 != 0;
                double hx = (swap ? d.SizeZ : d.SizeX) * 0.5;
                double hz = (swap ? d.SizeX : d.SizeZ) * 0.5;
                double fx = room.HalfX - hx, fz = room.HalfZ - hz;
                if (fx <= 0 || fz <= 0) continue;
                var pos = new Vec2(room.Center.X + (ctx.Rnd.NextDouble() * 2 - 1) * fx,
                                   room.Center.Z + (ctx.Rnd.NextDouble() * 2 - 1) * fz);
                var p = MakeProp(type, pos, rot);
                if (ctx.Fits(p, false)) return ctx.Commit(p);
            }
            return null;
        }

        private static readonly int[,] Compass = { {1,0},{1,1},{0,1},{-1,1},{-1,0},{-1,-1},{0,-1},{1,-1} };

        /// <summary>Place <paramref name="type"/> immediately beside <paramref name="anchor"/>.</summary>
        private static Prop PlaceNear(Ctx ctx, PropType type, Prop anchor, int attempts = MaxAttempts)
        {
            if (anchor == null) return null;
            var d = PropDims[type];
            var ab = PropBox(anchor);
            double anchorHalf = Math.Max(ab.MaxX - ab.MinX, ab.MaxZ - ab.MinZ) * 0.5;
            double ownHalf = Math.Max(d.SizeX, d.SizeZ) * 0.5;
            for (int a = 0; a < attempts; a++)
            {
                int dir = ctx.Rnd.Next(8);
                double radius = anchorHalf + ownHalf + ctx.S.PropSpacing + 0.05 + ctx.Rnd.NextDouble() * 0.4;
                double len = Math.Sqrt(Compass[dir, 0] * Compass[dir, 0] + Compass[dir, 1] * Compass[dir, 1]);
                var pos = new Vec2(anchor.Position.X + Compass[dir, 0] / len * radius,
                                   anchor.Position.Z + Compass[dir, 1] / len * radius);
                var p = MakeProp(type, pos, ctx.Rnd.Next(2) * 90.0);
                if (ctx.Fits(p, false)) return ctx.Commit(p);
            }
            return null;
        }

        private static Prop PlaceInCorner(Ctx ctx, PropType type)
        {
            var room = ctx.Room;
            if (room.Shape != RoomShape.Rectangle) return PlaceAgainstWall(ctx, type);
            var d = PropDims[type];
            int start = ctx.Rnd.Next(4);
            for (int k = 0; k < 4; k++)
            {
                int c = (start + k) % 4;
                double sx = (c == 0 || c == 3) ? -1 : 1;
                double sz = (c == 0 || c == 1) ? 1 : -1;
                var pos = new Vec2(room.Center.X + sx * (room.HalfX - d.SizeX * 0.5),
                                   room.Center.Z + sz * (room.HalfZ - d.SizeZ * 0.5));
                var p = MakeProp(type, pos, 0);
                if (ctx.Fits(p, false)) return ctx.Commit(p);
            }
            return null;
        }

        // =============================================================================================
        // furniture per archetype
        // =============================================================================================

        private static void FurnishRoom(LevelLayout layout, Room room, LevelGenSettings s, Random rnd)
        {
            var ctx = new Ctx
            {
                Room = room,
                S = s,
                Rnd = rnd,
                Mouths = RoomMouths(layout, room),
                Grid = RoomWalkability.Build(room)
            };
            switch (room.Type)
            {
                case RoomType.PatientRoom: FurnishPatientRoom(ctx); break;
                case RoomType.Ward:        FurnishWard(ctx); break;
                case RoomType.Office:      FurnishOffice(ctx); break;
                case RoomType.NurseStation:FurnishNurseStation(ctx); break;
                case RoomType.Storage:     FurnishStorage(ctx); break;
                default:                   FurnishLobby(ctx); break;
            }
        }

        private static void FurnishPatientRoom(Ctx ctx)
        {
            int beds = 1 + ctx.Rnd.Next(2);
            var placed = new List<Prop>();
            for (int i = 0; i < beds; i++)
            {
                var bed = PlaceAgainstWall(ctx, PropType.HospitalBed);
                if (bed == null) continue;
                placed.Add(bed);
                int wall = ctx.LastWall;
                // cabinet beside the bed, flush to the same wall
                double bedRun = PropDims[PropType.HospitalBed].SizeX;
                double cabRun = PropDims[PropType.BedsideCabinet].SizeX;
                double step = bedRun * 0.5 + ctx.S.PropSpacing + cabRun * 0.5 + 0.02;
                double baseAlong = WallIsAlongX(wall) ? bed.Position.X : bed.Position.Z;
                int firstSide = ctx.Rnd.Next(2) == 0 ? -1 : 1;
                if (TryWallProp(ctx, PropType.BedsideCabinet, wall, baseAlong + firstSide * step) == null)
                    TryWallProp(ctx, PropType.BedsideCabinet, wall, baseAlong - firstSide * step);
            }
            if (placed.Count > 0 && ctx.Rnd.NextDouble() < 0.40)
                PlaceNear(ctx, PropType.IvStand, placed[ctx.Rnd.Next(placed.Count)]);
            if (ctx.Rnd.NextDouble() < 0.30)
                PlaceFree(ctx, PropType.Chair);
        }

        private static void FurnishWard(Ctx ctx)
        {
            var room = ctx.Room;
            bool longAxisX = room.SizeX >= room.SizeZ;

            // Beds go on the wall pair with the FEWEST corridor mouths (a bed row across a doorway is
            // both wrong and unplaceable once the mouth lane is enforced). Ties go to the longer pair.
            int mouthsNS = 0, mouthsEW = 0;
            for (int i = 0; i < ctx.Mouths.Count; i++)
            {
                var m = ctx.Mouths[i];
                if (Math.Abs(m.X - room.Center.X) > Math.Abs(m.Z - room.Center.Z)) mouthsEW++;
                else mouthsNS++;
            }
            bool useNS = mouthsNS < mouthsEW || (mouthsNS == mouthsEW && longAxisX);
            int[] walls = useNS ? new[] { 0, 2 } : new[] { 1, 3 };
            bool rowAlongX = useNS;

            double bedRun = PropDims[PropType.HospitalBed].SizeX;
            double cabRun = PropDims[PropType.BedsideCabinet].SizeX;
            // Row gap is at least 1.3 m (agent diameter plus margin) and wide enough for the
            // cabinet that goes between a pair, so rows stay uniform instead of staggering.
            double gap = Math.Max(1.3, cabRun + 2.0 * ctx.S.PropSpacing + 0.1);
            double pitch = bedRun + gap;
            double span = rowAlongX ? room.HalfX : room.HalfZ;
            double centre = rowAlongX ? room.Center.X : room.Center.Z;
            int slots = (int)Math.Floor((2 * span - bedRun) / pitch) + 1;
            if (slots < 1) slots = 1;

            foreach (int wall in walls)
            {
                var row = new List<double>();
                double rowWidth = (slots - 1) * pitch;
                for (int i = 0; i < slots; i++)
                {
                    double along = centre - rowWidth * 0.5 + i * pitch;
                    if (TryWallProp(ctx, PropType.HospitalBed, wall, along) != null) row.Add(along);
                }
                // a cabinet between each adjacent pair of beds
                for (int i = 1; i < row.Count; i++)
                    TryWallProp(ctx, PropType.BedsideCabinet, wall, (row[i - 1] + row[i]) * 0.5);
            }
        }

        private static void FurnishOffice(Ctx ctx)
        {
            var desk = PlaceAgainstWall(ctx, PropType.Desk);
            if (desk != null)
            {
                int wall = ctx.LastWall;
                double inward = (wall == 0 || wall == 1) ? -1.0 : 1.0;
                double gap = PropDims[PropType.Desk].SizeZ * 0.5 + ctx.S.PropSpacing
                           + PropDims[PropType.Chair].SizeZ * 0.5 + 0.05;
                var pos = WallIsAlongX(wall)
                    ? new Vec2(desk.Position.X, desk.Position.Z + inward * gap)
                    : new Vec2(desk.Position.X + inward * gap, desk.Position.Z);
                var chair = MakeProp(PropType.Chair, pos, WallRotation(wall));
                if (ctx.Fits(chair, false)) ctx.Commit(chair);
                else PlaceNear(ctx, PropType.Chair, desk);
            }
            else PlaceFree(ctx, PropType.Chair);

            PlaceInCorner(ctx, PropType.FilingCabinet);
            if (ctx.Rnd.NextDouble() < 0.50)
            {
                int other = ctx.Rnd.Next(4);
                if (desk != null && other == ctx.LastWall) other = (other + 1) % 4;
                PlaceAgainstWall(ctx, PropType.Shelf, other);
            }
        }

        private static void FurnishNurseStation(Ctx ctx)
        {
            var room = ctx.Room;
            // Counter near the centre but offset, keeping a 3 m lane from every mouth to the centre.
            for (int a = 0; a < MaxAttempts; a++)
            {
                double rot = ctx.Rnd.Next(2) * 90.0;
                double reach = 0.8 + ctx.Rnd.NextDouble() * 2.6;
                double ang = ctx.Rnd.NextDouble() * Math.PI * 2.0;
                var pos = new Vec2(room.Center.X + Math.Cos(ang) * reach, room.Center.Z + Math.Sin(ang) * reach);
                var counter = MakeProp(PropType.Counter, pos, rot);
                if (ctx.Fits(counter, true)) { ctx.Commit(counter); break; }
            }
            for (int i = 0; i < 2; i++)
                if (PlaceFree(ctx, PropType.Chair) == null) PlaceAgainstWall(ctx, PropType.Chair);
            int cabinets = 1 + ctx.Rnd.Next(2);
            for (int i = 0; i < cabinets; i++) PlaceAgainstWall(ctx, PropType.FilingCabinet);
        }

        private static void FurnishStorage(Ctx ctx)
        {
            double run = PropDims[PropType.Shelf].SizeX;
            double pitch = run + ctx.S.PropSpacing + 0.1;
            for (int wall = 0; wall < 4; wall++)
            {
                double span = WallIsAlongX(wall) ? ctx.Room.HalfX : ctx.Room.HalfZ;
                double centre = WallIsAlongX(wall) ? ctx.Room.Center.X : ctx.Room.Center.Z;
                int slots = (int)Math.Floor((2 * span - run) / pitch) + 1;
                if (slots < 1) slots = 1;
                double rowWidth = (slots - 1) * pitch;
                for (int i = 0; i < slots; i++)
                    TryWallProp(ctx, PropType.Shelf, wall, centre - rowWidth * 0.5 + i * pitch);
            }
            PlaceFree(ctx, PropType.Wheelchair);
            if (ctx.Rnd.NextDouble() < 0.5) PlaceAgainstWall(ctx, PropType.Gurney);
        }

        private static void FurnishLobby(Ctx ctx)
        {
            int benches = 2 + ctx.Rnd.Next(3);
            for (int i = 0; i < benches; i++) PlaceAgainstWall(ctx, PropType.Bench);
            PlaceAgainstWall(ctx, PropType.Counter);
            if (PlaceFree(ctx, PropType.Wheelchair) == null) PlaceAgainstWall(ctx, PropType.Wheelchair);
        }

        // =============================================================================================
        // lights
        // =============================================================================================

        private static double RoomArea(Room r)
        {
            return r.Shape == RoomShape.Rectangle
                ? r.SizeX * r.SizeZ
                : Math.PI * r.HalfX * r.HalfZ;
        }

        private static LightTint CeilingTint(Room room, Random rnd)
        {
            double green = rnd.NextDouble();
            double warm = rnd.NextDouble();
            if (green < 0.05) return LightTint.SicklyGreen;
            switch (room.Type)
            {
                case RoomType.Office:
                case RoomType.PatientRoom:
                    return warm < 0.70 ? LightTint.WarmIncandescent : LightTint.CoolFluorescent;
                case RoomType.Lobby:
                    return warm < 0.50 ? LightTint.WarmIncandescent : LightTint.CoolFluorescent;
                default:
                    return LightTint.CoolFluorescent;
            }
        }

        private static void LightRoom(Room room, LevelGenSettings s, Random rnd, ref int keyDark)
        {
            double area = RoomArea(room);
            int cap = (int)Math.Floor(area / s.LightAreaPerLamp);
            if (cap > s.MaxLightsPerRoom) cap = s.MaxLightsPerRoom;
            if (cap < 1) cap = 1;
            int count = 1 + rnd.Next(cap);

            double darkRoll = rnd.NextDouble();
            bool dark = darkRoll < s.DarkRoomChance
                        && room.Role != RoomRole.Spawn
                        && (room.Role != RoomRole.Key || keyDark < 1);
            if (dark && room.Role == RoomRole.Key) keyDark++;

            var offsets = CeilingOffsets(room, count);
            for (int i = 0; i < offsets.Count; i++)
            {
                var pos = new Vec2(room.Center.X + offsets[i].X * room.HalfX,
                                   room.Center.Z + offsets[i].Z * room.HalfZ);
                var tint = CeilingTint(room, rnd);
                double range = 7.0 + rnd.NextDouble() * 4.0;
                double intensity = 1.2 + rnd.NextDouble() * 1.0;
                bool flicker = !dark && rnd.NextDouble() < s.FlickerChance;
                room.Lights.Add(new LightSource
                {
                    Type = LightType.CeilingLamp,
                    Tint = tint,
                    Position = pos,
                    Height = s.CeilingHeight - CeilingDrop,
                    Intensity = intensity,
                    Range = range,
                    Flicker = flicker,
                    Enabled = !dark
                });
            }

            // Desk lamps: every Desk, and half the Counters / BedsideCabinets, walking the props in the
            // order they were placed (already random) and stopping at MaxDeskLampsPerRoom.
            int deskLamps = 0;
            for (int i = 0; i < room.Props.Count && deskLamps < MaxDeskLampsPerRoom; i++)
            {
                var p = room.Props[i];
                bool always = p.Type == PropType.Desk;
                bool maybe = p.Type == PropType.Counter || p.Type == PropType.BedsideCabinet;
                if (!always && !maybe) continue;
                if (!always && rnd.NextDouble() >= 0.50) continue;
                deskLamps++;
                room.Lights.Add(new LightSource
                {
                    Type = LightType.DeskLamp,
                    Tint = LightTint.WarmIncandescent,
                    Position = p.Position,
                    Height = p.Height + DeskLampRise,
                    Intensity = 0.8 + rnd.NextDouble() * 0.4,
                    Range = 4.0 + rnd.NextDouble(),
                    Flicker = rnd.NextDouble() < s.FlickerChance,
                    Enabled = true,
                    AttachedPropIndex = i
                });
            }

            // 0-1 wall sconce on a wall midpoint, only if the room is still under its light budget
            bool wantSconce = rnd.NextDouble() < 0.5;
            if (wantSconce && room.Lights.Count < MaxTotalLightsPerRoom)
            {
                int wall = rnd.Next(4);
                bool alongX = WallIsAlongX(wall);
                double sign = (wall == 0 || wall == 1) ? 1.0 : -1.0;
                var pos = alongX
                    ? new Vec2(room.Center.X, room.Center.Z + sign * Math.Max(0.0, room.HalfZ - 0.10))
                    : new Vec2(room.Center.X + sign * Math.Max(0.0, room.HalfX - 0.10), room.Center.Z);
                bool red = rnd.NextDouble() < 0.10;
                room.Lights.Add(new LightSource
                {
                    Type = LightType.WallSconce,
                    Tint = red ? LightTint.Red : LightTint.WarmIncandescent,
                    Position = pos,
                    Height = Math.Min(SconceHeight, s.CeilingHeight),
                    Intensity = 0.6 + rnd.NextDouble() * 0.4,
                    Range = 5.0 + rnd.NextDouble() * 2.0,
                    Flicker = rnd.NextDouble() < s.FlickerChance,
                    Enabled = true
                });
            }
        }

        /// <summary>Normalised (-1..1 of half-extent) ceiling-lamp offsets: centre, thirds, triangle, 2x2.</summary>
        private static List<Vec2> CeilingOffsets(Room room, int count)
        {
            var list = new List<Vec2>(count);
            const double k = 0.45;
            bool longX = room.SizeX >= room.SizeZ;
            if (count <= 1) { list.Add(new Vec2(0, 0)); return list; }
            if (count == 2)
            {
                double t = 1.0 / 3.0; // thirds of the extent == 1/3 of the half-extent from centre
                if (longX) { list.Add(new Vec2(-t, 0)); list.Add(new Vec2(t, 0)); }
                else { list.Add(new Vec2(0, -t)); list.Add(new Vec2(0, t)); }
                return list;
            }
            if (count == 3)
            {
                list.Add(new Vec2(0, k));
                list.Add(new Vec2(-k, -k));
                list.Add(new Vec2(k, -k));
                return list;
            }
            list.Add(new Vec2(-k, -k));
            list.Add(new Vec2(k, -k));
            list.Add(new Vec2(-k, k));
            list.Add(new Vec2(k, k));
            return list;
        }

        // =============================================================================================
        // corridors
        // =============================================================================================

        private static void FurnishCorridor(Corridor c, LevelGenSettings s, Random rnd)
        {
            int want = rnd.Next(3); // 0-2
            if (c.Path == null || c.Path.Count < 2) return;
            var p0 = c.Path[0];
            var p1 = c.Path[c.Path.Count - 1];
            bool alongZ = Math.Abs(p0.X - p1.X) <= 1e-9;
            double len = c.Length;
            double half = c.Width * 0.5;

            for (int i = 0; i < want; i++)
            {
                PropType type = rnd.Next(2) == 0 ? PropType.Gurney : PropType.Wheelchair;
                var d = PropDims[type];
                // Rotation chosen so the prop's short side spans the corridor width.
                double rot = alongZ ? 0.0 : 90.0;
                double spanAcross = d.SizeX; // local X is the narrow side for both gurney and wheelchair
                double spanAlong = d.SizeZ;
                if (c.Width - spanAcross < CorridorWalkway) continue;
                double lo = CorridorPropEndGap + spanAlong * 0.5;
                double hi = len - CorridorPropEndGap - spanAlong * 0.5;
                if (hi <= lo) continue;

                bool placed = false;
                for (int a = 0; a < MaxAttempts && !placed; a++)
                {
                    double t = lo + rnd.NextDouble() * (hi - lo);
                    double side = rnd.Next(2) == 0 ? -1.0 : 1.0;
                    double across = side * (half - spanAcross * 0.5);
                    Vec2 pos;
                    if (alongZ)
                    {
                        double dirZ = p1.Z >= p0.Z ? 1.0 : -1.0;
                        pos = new Vec2(p0.X + across, p0.Z + dirZ * t);
                    }
                    else
                    {
                        double dirX = p1.X >= p0.X ? 1.0 : -1.0;
                        pos = new Vec2(p0.X + dirX * t, p0.Z + across);
                    }
                    var prop = MakeProp(type, pos, rot);
                    var b = PropBox(prop);
                    bool ok = true;
                    for (int j = 0; j < c.Props.Count && ok; j++)
                        if (BoxGap(b, PropBox(c.Props[j])) < s.PropSpacing - GeomEps) ok = false;
                    if (!ok) continue;
                    var cb = CorridorBox(c);
                    if (b.MinX < cb.MinX - GeomEps || b.MaxX > cb.MaxX + GeomEps
                        || b.MinZ < cb.MinZ - GeomEps || b.MaxZ > cb.MaxZ + GeomEps) continue;
                    c.Props.Add(prop);
                    placed = true;
                }
            }
        }

        private static void LightCorridor(Corridor c, LevelGenSettings s, Random rnd)
        {
            if (c.Path == null || c.Path.Count < 2) return;
            var p0 = c.Path[0];
            var p1 = c.Path[c.Path.Count - 1];
            double len = c.Length;
            double spacing = s.CorridorLightSpacing > 0 ? s.CorridorLightSpacing : 7.0;

            var ts = new List<double>();
            for (double t = spacing * 0.5; t < len; t += spacing) ts.Add(t);
            if (ts.Count == 0) ts.Add(len * 0.5); // every corridor gets at least one strip

            foreach (double t in ts)
            {
                double u = len <= 1e-12 ? 0.0 : t / len;
                var pos = new Vec2(p0.X + (p1.X - p0.X) * u, p0.Z + (p1.Z - p0.Z) * u);
                bool dead = rnd.NextDouble() < 0.20;
                c.Lights.Add(new LightSource
                {
                    Type = LightType.CorridorStrip,
                    Tint = LightTint.CoolFluorescent,
                    Position = pos,
                    Height = s.CeilingHeight - 0.15,
                    Intensity = 0.9 + rnd.NextDouble() * 0.5,
                    Range = 6.0 + rnd.NextDouble() * 2.0,
                    Flicker = !dead && rnd.NextDouble() < 0.35,
                    Enabled = !dead
                });
            }
        }
    }
}
