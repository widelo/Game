// v2 defect fix: furniture must never cut a room into disconnected pockets.
// Pure C# (no UnityEngine). A coarse occupancy grid stands in for the NavMesh: a real Unity bake found
// seeds where beds/shelves ringed the room centre into an island, so RoomFurnisher now runs this check
// DURING placement (a candidate that would disconnect the room counts as a failed attempt) and
// LevelValidator re-runs it on the finished layout.
//
// Model: cell (0.25 m) is walkable if its centre lies inside the room shrunk by the agent radius (0.5 m)
// and outside every placed prop AABB inflated by 0.6 m. Anchors are the room centre plus each corridor
// mouth moved 1.5 m inward; all anchors must sit in one flood-fill component and the centre must keep a
// 1.2 m radius clear disc.
using System;
using System.Collections.Generic;

namespace LevelGen.Core
{
    public static class RoomWalkability
    {
        public const double CellSize = 0.25;
        public const double AgentRadius = 0.5;   // Unity NavMesh agent radius (docs/ARCHITECTURE.md)
        public const double PropInflate = 0.6;   // props carve this much more than their footprint
        public const double CentreDiscRadius = 1.2;
        public const double MouthInset = 1.5;    // where the NavMesh probe starts inside a mouth

        /// <summary>Per-room rasterisation. The shape mask is built once and reused for every candidate.</summary>
        public sealed class Grid
        {
            public int NX, NZ;
            public double MinX, MinZ;
            public byte[] Base;   // 1 = inside the shrunk room shape
            public byte[] Work;   // scratch: 0 = blocked, 1 = free, 2 = reached by the flood fill
            public int[] Stack;
            public bool Degenerate; // room smaller than the agent: nothing to reason about
        }

        public static Grid Build(Room room)
        {
            var g = new Grid();
            double hx = room.HalfX - AgentRadius;
            double hz = room.HalfZ - AgentRadius;
            if (hx <= CellSize * 0.5 || hz <= CellSize * 0.5) { g.Degenerate = true; return g; }

            g.NX = (int)Math.Ceiling(2.0 * hx / CellSize);
            g.NZ = (int)Math.Ceiling(2.0 * hz / CellSize);
            if (g.NX < 1) g.NX = 1;
            if (g.NZ < 1) g.NZ = 1;
            g.MinX = room.Center.X - hx;
            g.MinZ = room.Center.Z - hz;
            g.Base = new byte[g.NX * g.NZ];
            g.Work = new byte[g.NX * g.NZ];
            g.Stack = new int[g.NX * g.NZ];

            bool rect = room.Shape == RoomShape.Rectangle;
            for (int iz = 0; iz < g.NZ; iz++)
            {
                double z = g.MinZ + (iz + 0.5) * CellSize - room.Center.Z;
                for (int ix = 0; ix < g.NX; ix++)
                {
                    double x = g.MinX + (ix + 0.5) * CellSize - room.Center.X;
                    bool inside = rect
                        ? Math.Abs(x) <= hx && Math.Abs(z) <= hz
                        : (x / hx) * (x / hx) + (z / hz) * (z / hz) <= 1.0;
                    g.Base[iz * g.NX + ix] = inside ? (byte)1 : (byte)0;
                }
            }
            return g;
        }

        /// <summary>The point a NavMesh probe would start from, 1.5 m inside a corridor mouth.</summary>
        public static Vec2 MouthInsidePoint(Room room, Vec2 mouth)
        {
            var d = room.Center - mouth;
            double len = d.Length;
            if (len <= 1e-9) return room.Center;
            double t = Math.Min(MouthInset, len);
            return new Vec2(mouth.X + d.X / len * t, mouth.Z + d.Z / len * t);
        }

        /// <summary>Room centre first, then one inside-point per corridor mouth.</summary>
        public static List<Vec2> Anchors(Room room, List<Vec2> mouths)
        {
            var list = new List<Vec2> { room.Center };
            if (mouths != null)
                for (int i = 0; i < mouths.Count; i++) list.Add(MouthInsidePoint(room, mouths[i]));
            return list;
        }

        /// <summary>Convenience overload: check a finished room.</summary>
        public static bool IsWalkable(Room room, List<Vec2> mouths)
        {
            return Check(Build(room), room, room.Props, null, mouths);
        }

        /// <summary>
        /// True when every anchor lies in one flood-fill component and the centre keeps its clear disc,
        /// with <paramref name="props"/> plus the optional <paramref name="candidate"/> in place.
        /// </summary>
        public static bool Check(Grid g, Room room, List<Prop> props, Prop candidate, List<Vec2> mouths)
        {
            if (g == null || g.Degenerate) return true;

            Array.Copy(g.Base, g.Work, g.Base.Length);
            if (props != null)
                for (int i = 0; i < props.Count; i++) Stamp(g, props[i]);
            Stamp(g, candidate);

            // --- centre keeps a clear disc ------------------------------------------------
            int discCells = 0;
            int r = (int)Math.Ceiling(CentreDiscRadius / CellSize);
            int cx = CellOf(room.Center.X, g.MinX);
            int cz = CellOf(room.Center.Z, g.MinZ);
            for (int iz = cz - r; iz <= cz + r; iz++)
            {
                if (iz < 0 || iz >= g.NZ) continue;
                double z = g.MinZ + (iz + 0.5) * CellSize - room.Center.Z;
                for (int ix = cx - r; ix <= cx + r; ix++)
                {
                    if (ix < 0 || ix >= g.NX) continue;
                    double x = g.MinX + (ix + 0.5) * CellSize - room.Center.X;
                    if (x * x + z * z > CentreDiscRadius * CentreDiscRadius) continue;
                    int idx = iz * g.NX + ix;
                    if (g.Base[idx] == 0) continue; // outside the room anyway: not our business
                    discCells++;
                    if (g.Work[idx] == 0) return false; // a prop eats into the central disc
                }
            }
            if (discCells == 0) return true; // room too small to host a disc at all

            // --- flood fill from the centre -----------------------------------------------
            // An anchor sitting on a blocked cell is a FAILURE: nothing is snapped anywhere.
            int start = CellIndex(g, room.Center);
            if (start < 0 || g.Work[start] == 0) return false;
            int top = 0;
            g.Stack[top++] = start;
            g.Work[start] = 2;
            while (top > 0)
            {
                int idx = g.Stack[--top];
                int ix = idx % g.NX, iz = idx / g.NX;
                if (ix > 0) Push(g, idx - 1, ref top);
                if (ix < g.NX - 1) Push(g, idx + 1, ref top);
                if (iz > 0) Push(g, idx - g.NX, ref top);
                if (iz < g.NZ - 1) Push(g, idx + g.NX, ref top);
            }

            // --- every mouth anchor must have been reached ---------------------------------
            if (mouths != null)
            {
                for (int i = 0; i < mouths.Count; i++)
                {
                    int a = CellIndex(g, MouthInsidePoint(room, mouths[i]));
                    if (a < 0 || g.Work[a] != 2) return false;
                }
            }
            return true;
        }

        private static void Push(Grid g, int idx, ref int top)
        {
            if (g.Work[idx] != 1) return;
            g.Work[idx] = 2;
            g.Stack[top++] = idx;
        }

        private static void Stamp(Grid g, Prop p)
        {
            if (p == null) return;
            var b = RoomFurnisher.PropBox(p);
            int ix0 = CellOf(b.MinX - PropInflate, g.MinX);
            int ix1 = CellOf(b.MaxX + PropInflate, g.MinX);
            int iz0 = CellOf(b.MinZ - PropInflate, g.MinZ);
            int iz1 = CellOf(b.MaxZ + PropInflate, g.MinZ);
            if (ix0 < 0) ix0 = 0;
            if (iz0 < 0) iz0 = 0;
            if (ix1 >= g.NX) ix1 = g.NX - 1;
            if (iz1 >= g.NZ) iz1 = g.NZ - 1;
            double minX = b.MinX - PropInflate, maxX = b.MaxX + PropInflate;
            double minZ = b.MinZ - PropInflate, maxZ = b.MaxZ + PropInflate;
            for (int iz = iz0; iz <= iz1; iz++)
            {
                double z = g.MinZ + (iz + 0.5) * CellSize;
                if (z < minZ || z > maxZ) continue;
                int row = iz * g.NX;
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    double x = g.MinX + (ix + 0.5) * CellSize;
                    if (x < minX || x > maxX) continue;
                    g.Work[row + ix] = 0;
                }
            }
        }

        private static int CellOf(double world, double min)
        {
            return (int)Math.Floor((world - min) / CellSize);
        }

        /// <summary>Index of the cell containing <paramref name="p"/>, or -1 if it is off the grid.</summary>
        public static int CellIndex(Grid g, Vec2 p)
        {
            int ix = CellOf(p.X, g.MinX);
            int iz = CellOf(p.Z, g.MinZ);
            if (ix < 0 || iz < 0 || ix >= g.NX || iz >= g.NZ) return -1;
            return iz * g.NX + ix;
        }

        /// <summary>True when every anchor's own cell is free (no snapping to a neighbour).</summary>
        public static bool AnchorsWalkable(Room room, List<Prop> props, List<Vec2> mouths)
        {
            var g = Build(room);
            if (g.Degenerate) return true;
            Array.Copy(g.Base, g.Work, g.Base.Length);
            if (props != null) for (int i = 0; i < props.Count; i++) Stamp(g, props[i]);
            var anchors = Anchors(room, mouths);
            for (int i = 0; i < anchors.Count; i++)
            {
                int idx = CellIndex(g, anchors[i]);
                if (idx < 0 || g.Work[idx] == 0) return false;
            }
            return true;
        }
    }
}
