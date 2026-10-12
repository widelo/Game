// Shared unit meshes for props and light fixtures. No prefabs, no textures, no GameObject.CreatePrimitive
// (that would attach a collider to every one of the ~8 parts per prop, which we would then have to destroy
// and which the NavMesh would collect). Everything is a unit mesh scaled by the part's transform, so the
// whole level's furniture uses three shared meshes.
//
// LIFETIME: these meshes are PROCESS-WIDE and built once on first use. They are deliberately never destroyed
// by LevelBuilder.ClearLevel - a rebuild must not pull the mesh out from under the next build, or under a
// second LevelBuilder in the same scene. That is a bounded, one-time cost (one box, one cylinder, a handful
// of frustums) and contributes nothing to per-rebuild growth. The `!= null` guards use Unity's Object
// comparison, so after an editor domain reload wipes them the next access simply rebuilds them.
using System.Collections.Generic;
using UnityEngine;

namespace LevelGen.Unity
{
    public static class GenPrimitives
    {
        static Mesh box;
        static Mesh cylinder;
        static readonly Dictionary<int, Mesh> frustums = new Dictionary<int, Mesh>();

        /// <summary>1x1x1 cube centred on the origin.</summary>
        public static Mesh Box => box != null ? box : (box = BuildBox());

        /// <summary>Diameter 1, height 1, centred on the origin, axis +Y, 16 sides.</summary>
        public static Mesh Cylinder => cylinder != null ? cylinder : (cylinder = BuildFrustum(0.5f, 0.5f, 16, "GenCylinder"));

        /// <summary>
        /// Height 1, centred on the origin, axis +Y, bottom radius <paramref name="rBottom"/> and top radius
        /// <paramref name="rTop"/> (0 = cone). Lamp shades are wide-bottom frustums.
        /// </summary>
        public static Mesh Frustum(float rBottom, float rTop)
        {
            int key = Mathf.RoundToInt(rBottom * 1000f) * 100003 + Mathf.RoundToInt(rTop * 1000f);
            Mesh m;
            if (frustums.TryGetValue(key, out m) && m != null) return m;
            m = BuildFrustum(rBottom, rTop, 16, $"GenFrustum_{rBottom:F2}_{rTop:F2}");
            frustums[key] = m;
            return m;
        }

        /// <summary>A mesh-only child part: no collider, no shadow-casting cost beyond the renderer.</summary>
        public static GameObject Part(Transform parent, string name, Mesh mesh, Material material,
                                      Vector3 localPos, Vector3 localScale, Quaternion localRot)
        {
            var go = new GameObject(name);
            go.layer = 0;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = localScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            return go;
        }

        public static GameObject BoxPart(Transform parent, string name, Material mat, Vector3 pos, Vector3 size)
            => Part(parent, name, Box, mat, pos, size, Quaternion.identity);

        /// <summary>Upright cylinder of the given diameter and height.</summary>
        public static GameObject CylPart(Transform parent, string name, Material mat, Vector3 pos,
                                         float diameter, float height)
            => Part(parent, name, Cylinder, mat, pos, new Vector3(diameter, height, diameter), Quaternion.identity);

        /// <summary>Thin disc whose axis runs along local X - i.e. a wheel.</summary>
        public static GameObject WheelPart(Transform parent, string name, Material mat, Vector3 pos,
                                           float diameter, float width)
            => Part(parent, name, Cylinder, mat, pos, new Vector3(diameter, width, diameter),
                    Quaternion.Euler(0f, 0f, 90f));

        // ---------------------------------------------------------------- mesh construction

        static Mesh BuildBox()
        {
            var mb = new RoomMeshBuilder.MeshBuf();
            const float h = 0.5f;
            Vector3 p000 = new Vector3(-h, -h, -h), p100 = new Vector3(h, -h, -h);
            Vector3 p110 = new Vector3(h, h, -h), p010 = new Vector3(-h, h, -h);
            Vector3 p001 = new Vector3(-h, -h, h), p101 = new Vector3(h, -h, h);
            Vector3 p111 = new Vector3(h, h, h), p011 = new Vector3(-h, h, h);

            mb.Quad(p001, p101, p111, p011); // +Z
            mb.Quad(p100, p000, p010, p110); // -Z
            mb.Quad(p101, p100, p110, p111); // +X
            mb.Quad(p000, p001, p011, p010); // -X
            mb.Quad(p011, p111, p110, p010); // +Y
            mb.Quad(p000, p100, p101, p001); // -Y
            return mb.ToMesh("GenBox");
        }

        static Mesh BuildFrustum(float rBottom, float rTop, int sides, string name)
        {
            var mb = new RoomMeshBuilder.MeshBuf();
            const float hy = 0.5f;
            bool coneTop = rTop <= 1e-5f;

            for (int i = 0; i < sides; i++)
            {
                float t0 = 2f * Mathf.PI * i / sides;
                float t1 = 2f * Mathf.PI * (i + 1) / sides;
                var b0 = new Vector3(rBottom * Mathf.Cos(t0), -hy, rBottom * Mathf.Sin(t0));
                var b1 = new Vector3(rBottom * Mathf.Cos(t1), -hy, rBottom * Mathf.Sin(t1));
                var u0 = new Vector3(rTop * Mathf.Cos(t0), hy, rTop * Mathf.Sin(t0));
                var u1 = new Vector3(rTop * Mathf.Cos(t1), hy, rTop * Mathf.Sin(t1));

                // Side: (b0, u0, u1, b1) winds outward (normal = +radial).
                if (coneTop) mb.Tri(mb.Add(b0), mb.Add(new Vector3(0f, hy, 0f)), mb.Add(b1));
                else mb.Quad(b0, u0, u1, b1);

                // Top cap faces +Y as (centre, u1, u0); bottom cap faces -Y as (centre, b0, b1).
                if (!coneTop) mb.Tri(mb.Add(new Vector3(0f, hy, 0f)), mb.Add(u1), mb.Add(u0));
                if (rBottom > 1e-5f) mb.Tri(mb.Add(new Vector3(0f, -hy, 0f)), mb.Add(b0), mb.Add(b1));
            }
            return mb.ToMesh(name);
        }
    }
}
