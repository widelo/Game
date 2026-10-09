// Mesh generation for LevelGen.Core layouts. No scene state here: pure layout -> Mesh functions.
//
// Conventions (must match LevelGen.Core): X = east, Z = north, floors at y = 0, walls rise to CeilingHeight.
// Winding: Unity's triangle normal for (a,b,c) is Cross(b-a, c-a). RecalculateNormals is still called.
//
// WALLS (v2, the gap fix): a room's wall is ONE continuous closed ring mesh - see WallRingGeometry. The old
// scheme (one independent outward-extruded box per side / per ellipse chord) left slivers at rectangle corners
// and between ellipse chords, and daylight at corridor mouths. Now:
//   * inner polyline = the floor edge; outer polyline = mitred offset by wallThickness along each bisector;
//   * corridor mouths cut the ring and emit vertical JAMB quads, so nothing is see-through;
//   * the cut is exactly Corridor.Width + 2*wallThickness wide along the tangent axis, which is precisely the
//     outer width of the corridor's two side slabs, so they slide in and butt flush against the jambs;
//   * ellipse mouths additionally get a straight MOUTH COLLAR (two solid slabs) bridging the curve to a
//     straight throat, so the corridor's straight walls never meet a curved jamb;
//   * corridor side walls are closed solid boxes extended FloorOverlap into BOTH rooms, so they penetrate the
//     room ring by far more than wallThickness. Z-fighting inside solid overlap is invisible; gaps are not.
using System;
using System.Collections.Generic;
using LevelGen.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace LevelGen.Unity
{
    public static class RoomMeshBuilder
    {
        /// <summary>Ellipse tessellation. 64 reads as smooth at 7-20 m room sizes.</summary>
        public const int EllipseSegments = 64;

        /// <summary>
        /// Metres a corridor floor / ceiling / wall is extended INTO each room past the room boundary. Must be
        /// comfortably larger than wallThickness so corridor slabs overlap the room ring instead of touching it.
        /// </summary>
        public const float FloorOverlap = 0.6f;

        // ---------------------------------------------------------------- openings

        /// <summary>A corridor mouth in a room wall: which side it is on, where on that side, how wide.</summary>
        public struct Opening
        {
            public Cardinal Dir;
            public Vec2 Point;   // room.BoundaryPoint(Dir)
            public double Width; // the corridor's clear width (thickness is added when the ring is cut)
        }

        public static Cardinal Opposite(Cardinal d)
        {
            switch (d)
            {
                case Cardinal.North: return Cardinal.South;
                case Cardinal.South: return Cardinal.North;
                case Cardinal.East: return Cardinal.West;
                default: return Cardinal.East;
            }
        }

        /// <summary>
        /// Every corridor mouth in <paramref name="room"/>. A corridor leaves RoomA towards DirectionFromA, so
        /// when the room is RoomB the attachment side is the opposite direction.
        /// </summary>
        public static List<Opening> OpeningsFor(Room room, LevelLayout layout)
        {
            var result = new List<Opening>();
            if (room == null || layout == null || room.CorridorIds == null) return result;

            for (int i = 0; i < room.CorridorIds.Count; i++)
            {
                int cid = room.CorridorIds[i];
                if (cid < 0 || cid >= layout.Corridors.Count) continue;
                Corridor c = layout.Corridors[cid];
                if (c == null) continue;

                Cardinal dir = c.RoomA == room.Id ? c.DirectionFromA : Opposite(c.DirectionFromA);
                result.Add(new Opening { Dir = dir, Point = room.BoundaryPoint(dir), Width = c.Width });
            }
            return result;
        }

        /// <summary>The room's floor-edge polyline, CCW. Shared by floor, ceiling and wall ring.</summary>
        public static List<Vec2> RoomOutline(Room room)
        {
            return room.Shape == RoomShape.Ellipse
                ? WallRingGeometry.EllipseOutline(room.Center, room.HalfX, room.HalfZ, EllipseSegments)
                : WallRingGeometry.RectangleOutline(room.Center, room.HalfX, room.HalfZ);
        }

        // ---------------------------------------------------------------- floors / ceilings

        public static Mesh BuildRoomFloor(Room room) => BuildRoomSlab(room, 0f, true, $"RoomFloor_{room.Id}");

        /// <summary>Same outline as the floor, at <paramref name="ceilingHeight"/>, normals facing DOWN.</summary>
        public static Mesh BuildRoomCeiling(Room room, float ceilingHeight)
            => BuildRoomSlab(room, ceilingHeight, false, $"RoomCeiling_{room.Id}");

        static Mesh BuildRoomSlab(Room room, float y, bool faceUp, string name)
        {
            var mb = new MeshBuf();
            List<Vec2> outline = RoomOutline(room);
            int n = outline.Count;

            int center = mb.Add(new Vector3((float)room.Center.X, y, (float)room.Center.Z));
            var ring = new int[n];
            for (int i = 0; i < n; i++)
                ring[i] = mb.Add(new Vector3((float)outline[i].X, y, (float)outline[i].Z));

            // Outline is CCW in XZ, so (center, p[i+1], p[i]) faces +Y; reverse for a downward ceiling.
            for (int i = 0; i < n; i++)
            {
                int a = ring[i], b = ring[(i + 1) % n];
                if (faceUp) mb.Tri(center, b, a);
                else mb.Tri(center, a, b);
            }
            return mb.ToMesh(name);
        }

        // ---------------------------------------------------------------- room walls

        public static Mesh BuildRoomWalls(Room room, LevelLayout layout, float ceilingHeight, float wallThickness)
        {
            List<Opening> openings = OpeningsFor(room, layout);
            var ringOpenings = new List<RingOpening>(openings.Count);
            for (int i = 0; i < openings.Count; i++)
            {
                ringOpenings.Add(new RingOpening
                {
                    Dir = openings[i].Dir,
                    Point = openings[i].Point,
                    RoomCenter = room.Center,
                    // The cut spans the corridor's FULL OUTER width so the side slabs butt flush against the jambs.
                    HalfWidth = openings[i].Width * 0.5 + wallThickness
                });
            }

            RingMesh ring = WallRingGeometry.BuildRing(RoomOutline(room), ringOpenings, wallThickness, ceilingHeight);

            var mb = new MeshBuf();
            mb.Append(ring);

            // Ellipse mouths: a straight collar so the corridor meets a straight jamb, not a curve.
            if (room.Shape == RoomShape.Ellipse)
                for (int i = 0; i < openings.Count; i++)
                    AppendMouthCollar(mb, room, openings[i], ceilingHeight, wallThickness);

            return mb.ToMesh($"RoomWalls_{room.Id}");
        }

        static void AppendMouthCollar(MeshBuf mb, Room room, Opening op, float height, float thickness)
        {
            float halfW = (float)op.Width * 0.5f;
            double cutHalf = op.Width * 0.5 + thickness;
            float depth = (float)WallRingGeometry.EllipseMouthDepth(op.Dir, room.HalfX, room.HalfZ, cutHalf);

            Vector3 axis = AxisOf(op.Dir);                                  // outward along the corridor
            var tangent = new Vector3(-axis.z, 0f, axis.x);                 // along the mouth width
            Vector3 b = new Vector3((float)op.Point.X, 0f, (float)op.Point.Z);

            float inset = depth + thickness + 0.05f;
            float outset = thickness + 0.05f;
            Vector3 p0 = b - axis * inset;                                  // deep enough to swallow the ring cut
            Vector3 p1 = b + axis * outset;

            // Two solid slabs with inner faces exactly on the corridor's clear width.
            AppendSlab(mb, p0 + tangent * halfW, p1 + tangent * halfW, tangent, height, thickness);
            AppendSlab(mb, p0 - tangent * halfW, p1 - tangent * halfW, -tangent, height, thickness);
        }

        static Vector3 AxisOf(Cardinal d)
        {
            switch (d)
            {
                case Cardinal.North: return Vector3.forward;
                case Cardinal.South: return Vector3.back;
                case Cardinal.East: return Vector3.right;
                default: return Vector3.left;
            }
        }

        // ---------------------------------------------------------------- corridors

        /// <summary>
        /// Corridor floor: one quad per path segment, first and last extended by <see cref="FloorOverlap"/>
        /// into the rooms so the floors overlap rather than merely touch.
        /// </summary>
        public static Mesh BuildCorridorFloor(Corridor corridor)
            => BuildCorridorSlab(corridor, 0f, true, $"CorridorFloor_{corridor.Id}");

        /// <summary>Corridor ceiling at the room ceiling height, normals DOWN, same overlap as the floor.</summary>
        public static Mesh BuildCorridorCeiling(Corridor corridor, float ceilingHeight)
            => BuildCorridorSlab(corridor, ceilingHeight, false, $"CorridorCeiling_{corridor.Id}");

        static Mesh BuildCorridorSlab(Corridor corridor, float y, bool faceUp, string name)
        {
            var mb = new MeshBuf();
            List<Vector3> path = PathPoints(corridor);
            float hw = (float)corridor.Width * 0.5f;

            for (int i = 0; i + 1 < path.Count; i++)
            {
                Vector3 a = path[i], b = path[i + 1];
                Vector3 dir = b - a;
                if (dir.sqrMagnitude < 1e-8f) continue;
                dir.Normalize();
                if (i == 0) a -= dir * FloorOverlap;
                if (i + 2 == path.Count) b += dir * FloorOverlap;

                Vector3 perp = new Vector3(-dir.z, 0f, dir.x) * hw;
                a.y = y; b.y = y;
                if (faceUp) mb.Quad(a - perp, a + perp, b + perp, b - perp);
                else mb.Quad(a - perp, b - perp, b + perp, a + perp);
            }
            return mb.ToMesh(name);
        }

        /// <summary>
        /// Two closed solid side slabs per path segment. Inner faces sit on the corridor's clear width, outer
        /// faces at width/2 + thickness - exactly the opening the room ring leaves - and the end segments are
        /// extended FloorOverlap into each room so the slabs penetrate the room ring.
        /// </summary>
        public static Mesh BuildCorridorWalls(Corridor corridor, float ceilingHeight, float wallThickness)
        {
            var mb = new MeshBuf();
            List<Vector3> path = PathPoints(corridor);
            float hw = (float)corridor.Width * 0.5f;

            for (int i = 0; i + 1 < path.Count; i++)
            {
                Vector3 a = path[i], b = path[i + 1];
                Vector3 dir = b - a;
                if (dir.sqrMagnitude < 1e-8f) continue;
                dir.Normalize();
                if (i == 0) a -= dir * FloorOverlap;
                if (i + 2 == path.Count) b += dir * FloorOverlap;

                var side = new Vector3(-dir.z, 0f, dir.x);
                AppendSlab(mb, a + side * hw, b + side * hw, side, ceilingHeight, wallThickness);
                AppendSlab(mb, a - side * hw, b - side * hw, -side, ceilingHeight, wallThickness);
            }
            return mb.ToMesh($"CorridorWalls_{corridor.Id}");
        }

        public static List<Vector3> PathPoints(Corridor corridor)
        {
            var pts = new List<Vector3>();
            if (corridor == null || corridor.Path == null) return pts;
            for (int i = 0; i < corridor.Path.Count; i++)
                pts.Add(new Vector3((float)corridor.Path[i].X, 0f, (float)corridor.Path[i].Z));
            return pts;
        }

        // ---------------------------------------------------------------- primitives

        /// <summary>
        /// One CLOSED solid wall slab (all six faces): a box from <paramref name="a"/> to <paramref name="b"/>
        /// at y=0 rising <paramref name="height"/>, extruded <paramref name="thickness"/> along
        /// <paramref name="outward"/>. Closed because an open end is a hole the player can see through.
        /// </summary>
        public static void AppendSlab(MeshBuf mb, Vector3 a, Vector3 b, Vector3 outward,
                                      float height, float thickness)
        {
            Vector3 dir = b - a;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f || height <= 0f) return;
            dir.Normalize();

            outward.y = 0f;
            if (outward.sqrMagnitude < 1e-8f) return;
            outward.Normalize();

            // Windings below assume outward == RightOf(dir) == (dir.z, 0, -dir.x); flip the segment otherwise.
            var right = new Vector3(dir.z, 0f, -dir.x);
            if (Vector3.Dot(right, outward) < 0f) { Vector3 tmp = a; a = b; b = tmp; dir = -dir; }

            Vector3 off = outward * Mathf.Max(thickness, 0.001f);
            Vector3 up = Vector3.up * height;
            a.y = 0f; b.y = 0f;

            Vector3 iA = a, iB = b, oA = a + off, oB = b + off;
            Vector3 iAt = iA + up, iBt = iB + up, oAt = oA + up, oBt = oB + up;

            mb.Quad(iA, iB, iBt, iAt);   // inner face (-outward)
            mb.Quad(oA, oAt, oBt, oB);   // outer face (+outward)
            mb.Quad(iAt, iBt, oBt, oAt); // top (+Y)
            mb.Quad(iA, oA, oB, iB);     // bottom (-Y)
            mb.Quad(iA, iAt, oAt, oA);   // cap at a (-dir)
            mb.Quad(iB, oB, oBt, iBt);   // cap at b (+dir)
        }

        /// <summary>Tiny growable vertex/index buffer.</summary>
        public sealed class MeshBuf
        {
            public readonly List<Vector3> Verts = new List<Vector3>();
            public readonly List<int> Tris = new List<int>();

            public int Add(Vector3 v) { Verts.Add(v); return Verts.Count - 1; }

            public void Tri(int a, int b, int c) { Tris.Add(a); Tris.Add(b); Tris.Add(c); }

            /// <summary>Two triangles (a,b,c) and (a,c,d); order the corners so the normal faces out.</summary>
            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int i = Verts.Count;
                Verts.Add(a); Verts.Add(b); Verts.Add(c); Verts.Add(d);
                Tris.Add(i); Tris.Add(i + 1); Tris.Add(i + 2);
                Tris.Add(i); Tris.Add(i + 2); Tris.Add(i + 3);
            }

            /// <summary>Copy a UnityEngine-free RingMesh in.</summary>
            public void Append(RingMesh ring)
            {
                if (ring == null) return;
                int baseIndex = Verts.Count;
                for (int i = 0; i < ring.Verts.Count; i++)
                {
                    RingVert v = ring.Verts[i];
                    Verts.Add(new Vector3((float)v.X, (float)v.Y, (float)v.Z));
                }
                for (int i = 0; i < ring.Tris.Count; i++) Tris.Add(baseIndex + ring.Tris[i]);
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name };
                // Defensive: 16-bit indices top out at 65535 vertices. These meshes are far smaller, but a
                // 20 m ellipse room with many mouths is the kind of thing that creeps up.
                mesh.indexFormat = Verts.Count > 60000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.SetVertices(Verts);
                mesh.SetTriangles(Tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
