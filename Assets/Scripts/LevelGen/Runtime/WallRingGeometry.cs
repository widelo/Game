// Continuous closed-ring wall geometry. INTENTIONALLY UnityEngine-free so the maths that decides whether
// walls have gaps can be compiled and asserted headlessly (see Logs/runtimecheck + the geometry self-check).
//
// Model: a room wall is ONE mesh built from a closed INNER polyline (the floor edge) and an OUTER polyline
// produced by a proper mitred offset of every vertex along its angle bisector. Corridor mouths cut the ring
// into open arcs; each arc emits inner face, outer face, top cap, and two vertical jamb quads at the cuts,
// so there is nothing to see through and rectangle corners close exactly (the mitre puts the outer corner at
// inner + (t, t), not at two overlapping boxes with a sliver between them).
//
// Conventions: X east, Z north, Y up. Outlines are wound COUNTER-CLOCKWISE in XZ, so for an edge direction d
// the outward normal is RightOf(d) = (d.Z, -d.X) and the room interior is to the left.
using System;
using System.Collections.Generic;
using LevelGen.Core;

namespace LevelGen.Unity
{
    /// <summary>Double-precision 3D point. Converted to UnityEngine.Vector3 by the mesh builder.</summary>
    public struct RingVert
    {
        public double X, Y, Z;
        public RingVert(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    /// <summary>A corridor mouth punched through the ring. HalfWidth already includes the wall thickness.</summary>
    public struct RingOpening
    {
        public Cardinal Dir;
        public Vec2 Point;       // room.BoundaryPoint(Dir): the axis tip the corridor leaves through
        public Vec2 RoomCenter;  // used for the "this half of the room" half-plane
        public double HalfWidth; // (corridorWidth + 2 * wallThickness) * 0.5
    }

    /// <summary>Raw triangle soup plus diagnostics the self-check reads.</summary>
    public sealed class RingMesh
    {
        public readonly List<RingVert> Verts = new List<RingVert>();
        public readonly List<int> Tris = new List<int>();

        /// <summary>Inner-face positions of every arc end (two per opening). Used to measure realised opening width.</summary>
        public readonly List<Vec2> CutPoints = new List<Vec2>();

        /// <summary>Number of solid arcs emitted. 1 and no cut points means a fully closed ring.</summary>
        public int ArcCount;

        /// <summary>Inner polyline of each solid arc, in order. Diagnostics only (the geometry self-check
        /// sums these to prove the ring covers the whole perimeter minus the openings).</summary>
        public readonly List<List<Vec2>> ArcPolylines = new List<List<Vec2>>();

        public void Quad(RingVert a, RingVert b, RingVert c, RingVert d)
        {
            int i = Verts.Count;
            Verts.Add(a); Verts.Add(b); Verts.Add(c); Verts.Add(d);
            Tris.Add(i); Tris.Add(i + 1); Tris.Add(i + 2);
            Tris.Add(i); Tris.Add(i + 2); Tris.Add(i + 3);
        }
    }

    public static class WallRingGeometry
    {
        const double Eps = 1e-7;

        // ------------------------------------------------------------------ outlines

        /// <summary>The 4 corners of a rectangle room, CCW. Mitre on these gives clean square corners.</summary>
        public static List<Vec2> RectangleOutline(Vec2 c, double halfX, double halfZ)
        {
            return new List<Vec2>
            {
                new Vec2(c.X - halfX, c.Z - halfZ),
                new Vec2(c.X + halfX, c.Z - halfZ),
                new Vec2(c.X + halfX, c.Z + halfZ),
                new Vec2(c.X - halfX, c.Z + halfZ),
            };
        }

        /// <summary>Ellipse room outline, CCW, <paramref name="segments"/> points (vertex 0 sits on the +X axis tip).</summary>
        public static List<Vec2> EllipseOutline(Vec2 c, double halfX, double halfZ, int segments)
        {
            if (segments < 8) segments = 8;
            var pts = new List<Vec2>(segments);
            for (int i = 0; i < segments; i++)
            {
                double t = 2.0 * Math.PI * i / segments;
                pts.Add(new Vec2(c.X + halfX * Math.Cos(t), c.Z + halfZ * Math.Sin(t)));
            }
            return pts;
        }

        // ------------------------------------------------------------------ ring

        struct Node
        {
            public Vec2 Inner;
            public Vec2 Outer;
        }

        /// <summary>
        /// Build the wall ring. <paramref name="height"/> is floor-to-ceiling; the ring has no bottom face
        /// (never visible) but every other face including the jambs is emitted.
        /// </summary>
        public static RingMesh BuildRing(IList<Vec2> inner, IList<RingOpening> openings,
                                         double thickness, double height)
        {
            var mesh = new RingMesh();
            int n = inner == null ? 0 : inner.Count;
            if (n < 3 || thickness <= 0.0 || height <= 0.0) return mesh;

            // Edge directions / outward normals, and the mitred outer vertex for every polyline vertex.
            var dir = new Vec2[n];
            var nrm = new Vec2[n];
            for (int i = 0; i < n; i++)
            {
                Vec2 a = inner[i], b = inner[(i + 1) % n];
                dir[i] = Norm(b - a);
                nrm[i] = RightOf(dir[i]);
            }
            var outerV = new Vec2[n];
            for (int i = 0; i < n; i++)
            {
                Vec2 n0 = nrm[(i - 1 + n) % n], n1 = nrm[i];
                Vec2 m = Norm(n0 + n1);
                if (m.Length < 0.5) m = n1;                 // 180 deg reversal guard
                double den = Dot(m, n1);
                if (den < 0.15) den = 0.15;                 // clamp the mitre spike on near-reflex corners
                outerV[i] = inner[i] + m * (thickness / den);
            }

            // Holes: per edge, the parameter interval covered by each opening, in global param s = edge + t.
            var holes = new List<double[]>();
            int openCount = openings == null ? 0 : openings.Count;
            for (int i = 0; i < n; i++)
            {
                Vec2 a = inner[i], b = inner[(i + 1) % n];
                for (int o = 0; o < openCount; o++)
                {
                    double t0, t1;
                    if (!ClipToOpening(a, b, openings[o], out t0, out t1)) continue;
                    if (t0 < Eps) t0 = 0.0;
                    if (t1 > 1.0 - Eps) t1 = 1.0;
                    if (t1 - t0 <= Eps) continue;
                    holes.Add(new[] { i + t0, i + t1 });
                }
            }
            holes.Sort((p, q) => p[0].CompareTo(q[0]));

            var merged = new List<double[]>();
            for (int i = 0; i < holes.Count; i++)
            {
                if (merged.Count > 0 && holes[i][0] <= merged[merged.Count - 1][1] + Eps)
                {
                    if (holes[i][1] > merged[merged.Count - 1][1]) merged[merged.Count - 1][1] = holes[i][1];
                }
                else merged.Add(new[] { holes[i][0], holes[i][1] });
            }

            // Fully consumed ring: no wall at all (cannot happen with sane sizes, but do not emit junk).
            if (merged.Count == 1 && merged[0][0] <= Eps && merged[0][1] >= n - Eps) return mesh;

            if (merged.Count == 0)
            {
                // Closed ring: every vertex, no jambs.
                var nodes = new Node[n];
                for (int i = 0; i < n; i++) nodes[i] = new Node { Inner = inner[i], Outer = outerV[i] };
                EmitArc(mesh, nodes, n, true, height);
                var closedPoly = new List<Vec2>(n + 1);
                for (int i = 0; i < n; i++) closedPoly.Add(nodes[i].Inner);
                closedPoly.Add(nodes[0].Inner);
                mesh.ArcPolylines.Add(closedPoly);
                mesh.ArcCount = 1;
                return mesh;
            }

            // Solid arcs = circular complement of the holes.
            var arcs = new List<double[]>();
            for (int i = 0; i + 1 < merged.Count; i++) arcs.Add(new[] { merged[i][1], merged[i + 1][0] });

            bool touchesZero = merged[0][0] <= Eps;
            bool touchesEnd = merged[merged.Count - 1][1] >= n - Eps;
            if (!touchesZero && !touchesEnd)
                arcs.Add(new[] { merged[merged.Count - 1][1], n + merged[0][0] }); // the arc across the seam
            else if (!touchesZero)
                arcs.Add(new[] { 0.0, merged[0][0] });
            else if (!touchesEnd)
                arcs.Add(new[] { merged[merged.Count - 1][1], (double)n });

            var buf = new List<Node>();
            for (int i = 0; i < arcs.Count; i++)
            {
                double sA = arcs[i][0], sB = arcs[i][1];
                if (sB - sA <= Eps) continue;

                buf.Clear();
                buf.Add(NodeAt(sA, true, inner, outerV, nrm, thickness, n));
                for (int k = (int)Math.Floor(sA) + 1; k < sB - Eps; k++)
                {
                    int v = ((k % n) + n) % n;
                    buf.Add(new Node { Inner = inner[v], Outer = outerV[v] });
                }
                buf.Add(NodeAt(sB, false, inner, outerV, nrm, thickness, n));

                // Drop duplicate consecutive nodes so no degenerate triangle is ever emitted.
                for (int k = buf.Count - 1; k > 0; k--)
                    if ((buf[k].Inner - buf[k - 1].Inner).Length < 1e-6) buf.RemoveAt(k);
                if (buf.Count < 2) continue;

                mesh.CutPoints.Add(buf[0].Inner);
                mesh.CutPoints.Add(buf[buf.Count - 1].Inner);
                var poly = new List<Vec2>(buf.Count);
                for (int k = 0; k < buf.Count; k++) poly.Add(buf[k].Inner);
                mesh.ArcPolylines.Add(poly);
                EmitArc(mesh, buf.ToArray(), buf.Count, false, height);
                mesh.ArcCount++;
            }

            return mesh;
        }

        static Node NodeAt(double s, bool isArcStart, IList<Vec2> inner, Vec2[] outerV,
                           Vec2[] nrm, double thickness, int n)
        {
            double f = s - Math.Floor(s);
            if (f <= Eps || f >= 1.0 - Eps)
            {
                // Cut exactly on a polyline vertex: use the normal of the edge the arc actually runs along,
                // not the bisector, so the jamb sits square to that edge.
                int v = (int)Math.Round(s);
                int edge = isArcStart ? v : v - 1;
                int vi = ((v % n) + n) % n;
                int ei = ((edge % n) + n) % n;
                return new Node { Inner = inner[vi], Outer = inner[vi] + nrm[ei] * thickness };
            }
            int e = (int)Math.Floor(s);
            int ea = ((e % n) + n) % n;
            Vec2 a = inner[ea], b = inner[((e + 1) % n + n) % n];
            Vec2 p = a + (b - a) * f;
            return new Node { Inner = p, Outer = p + nrm[ea] * thickness };
        }

        /// <summary>
        /// Inner face (normal = -outward), outer face (+outward), top cap (+Y), and for open arcs the two
        /// jamb quads. Windings are hand-derived for CCW outlines; see the header.
        /// </summary>
        static void EmitArc(RingMesh mesh, Node[] nodes, int count, bool closed, double h)
        {
            int segs = closed ? count : count - 1;
            for (int i = 0; i < segs; i++)
            {
                Node A = nodes[i], B = nodes[(i + 1) % count];
                if ((B.Inner - A.Inner).Length < 1e-6) continue;

                RingVert iA = V(A.Inner, 0), iB = V(B.Inner, 0);
                RingVert iAt = V(A.Inner, h), iBt = V(B.Inner, h);
                RingVert oA = V(A.Outer, 0), oB = V(B.Outer, 0);
                RingVert oAt = V(A.Outer, h), oBt = V(B.Outer, h);

                mesh.Quad(iA, iB, iBt, iAt);     // inner face, faces the room
                mesh.Quad(oA, oAt, oBt, oB);     // outer face
                mesh.Quad(iAt, iBt, oBt, oAt);   // top cap
            }

            if (closed) return;

            Node s = nodes[0], e = nodes[count - 1];
            mesh.Quad(V(s.Inner, 0), V(s.Inner, h), V(s.Outer, h), V(s.Outer, 0)); // jamb at arc start
            mesh.Quad(V(e.Inner, 0), V(e.Outer, 0), V(e.Outer, h), V(e.Inner, h)); // jamb at arc end
        }

        // ------------------------------------------------------------------ opening clip

        /// <summary>
        /// Clip segment a..b to the opening region: an axis-aligned slab of width 2*HalfWidth around the
        /// mouth, intersected with the half-plane that keeps it on the mouth's side of the room. Three
        /// half-planes, so Liang-Barsky gives one interval.
        /// </summary>
        static bool ClipToOpening(Vec2 a, Vec2 b, RingOpening op, out double t0, out double t1)
        {
            t0 = 0.0; t1 = 1.0;
            double hw = op.HalfWidth;
            bool ok = true;
            switch (op.Dir)
            {
                case Cardinal.North:
                    ok &= Clip(a.X, b.X, op.Point.X + hw, ref t0, ref t1);
                    ok &= Clip(-a.X, -b.X, -(op.Point.X - hw), ref t0, ref t1);
                    ok &= Clip(-a.Z, -b.Z, -op.RoomCenter.Z, ref t0, ref t1);
                    break;
                case Cardinal.South:
                    ok &= Clip(a.X, b.X, op.Point.X + hw, ref t0, ref t1);
                    ok &= Clip(-a.X, -b.X, -(op.Point.X - hw), ref t0, ref t1);
                    ok &= Clip(a.Z, b.Z, op.RoomCenter.Z, ref t0, ref t1);
                    break;
                case Cardinal.East:
                    ok &= Clip(a.Z, b.Z, op.Point.Z + hw, ref t0, ref t1);
                    ok &= Clip(-a.Z, -b.Z, -(op.Point.Z - hw), ref t0, ref t1);
                    ok &= Clip(-a.X, -b.X, -op.RoomCenter.X, ref t0, ref t1);
                    break;
                default:
                    ok &= Clip(a.Z, b.Z, op.Point.Z + hw, ref t0, ref t1);
                    ok &= Clip(-a.Z, -b.Z, -(op.Point.Z - hw), ref t0, ref t1);
                    ok &= Clip(a.X, b.X, op.RoomCenter.X, ref t0, ref t1);
                    break;
            }
            return ok && t1 - t0 > Eps;
        }

        /// <summary>One half-plane f(p) = value(p) - limit &lt;= 0 against the parametrised segment.</summary>
        static bool Clip(double va, double vb, double limit, ref double t0, ref double t1)
        {
            double fa = va - limit, fb = vb - limit;
            double den = fb - fa;
            if (Math.Abs(den) < 1e-12) return fa <= 0.0;
            double tc = -fa / den;
            if (den > 0.0) { if (tc < t1) t1 = tc; }
            else { if (tc > t0) t0 = tc; }
            return t0 <= t1;
        }

        // ------------------------------------------------------------------ small maths

        static RingVert V(Vec2 p, double y) => new RingVert(p.X, y, p.Z);
        static Vec2 Norm(Vec2 v) { double l = v.Length; return l < 1e-12 ? new Vec2(0, 0) : new Vec2(v.X / l, v.Z / l); }
        static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Z * b.Z;
        /// <summary>Rotate -90 deg: for CCW outlines this is the outward normal of an edge heading <paramref name="d"/>.</summary>
        static Vec2 RightOf(Vec2 d) => new Vec2(d.Z, -d.X);

        /// <summary>
        /// Ellipse mouths need a straight "collar": how far inside the axis tip the ring cut reaches, so the
        /// collar can be made long enough to bridge from the curve to a straight jamb.
        /// </summary>
        public static double EllipseMouthDepth(Cardinal dir, double halfX, double halfZ, double halfWidth)
        {
            bool spanIsX = dir == Cardinal.North || dir == Cardinal.South;
            double spanHalf = spanIsX ? halfX : halfZ;   // extent along the tangent axis
            double axisHalf = spanIsX ? halfZ : halfX;   // extent along the corridor axis
            if (spanHalf <= 1e-9) return axisHalf;
            double r = halfWidth / spanHalf;
            if (r >= 1.0) return axisHalf;
            return axisHalf - axisHalf * Math.Sqrt(1.0 - r * r);
        }
    }
}
