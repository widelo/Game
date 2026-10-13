// Furniture from layout data. Every piece is assembled from the shared unit meshes in GenPrimitives, in the
// prop's LOCAL frame (local X = Prop.SizeX, local Z = Prop.SizeZ, y = 0 at the floor), then the root is
// rotated by Prop.RotationDeg about Y and placed at the prop's world position.
//
// Collision / NavMesh contract: exactly ONE BoxCollider on the root covering footprint x height (so the
// player cannot walk through it), plus a NavMeshModifier with overrideArea = true, area = 1 (Not Walkable).
// The child meshes carry no colliders, so the NavMesh bake never finds a surface on top of a desk or a bed.
using LevelGen.Core;
using Unity.AI.Navigation;
using UnityEngine;

namespace LevelGen.Unity
{
    public static class PropBuilder
    {
        /// <summary>NavMesh area index 1 is Unity's built-in "Not Walkable".</summary>
        public const int NotWalkableArea = 1;

        public static GameObject Build(Transform parent, Prop prop, int index, LevelMaterials mats)
        {
            if (prop == null) return null;

            float sx = Mathf.Max(0.05f, (float)prop.SizeX);
            float sz = Mathf.Max(0.05f, (float)prop.SizeZ);
            float h = Mathf.Max(0.05f, (float)prop.Height);

            var root = new GameObject($"Prop_{prop.Type}_{index}");
            root.layer = 0;
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3((float)prop.Position.X, 0f, (float)prop.Position.Z);
            root.transform.localRotation = Quaternion.Euler(0f, (float)prop.RotationDeg, 0f);

            switch (prop.Type)
            {
                case PropType.HospitalBed: Bed(root.transform, sx, sz, h, mats, false); break;
                case PropType.Gurney:      Bed(root.transform, sx, sz, h, mats, true); break;
                case PropType.Desk:        Desk(root.transform, sx, sz, h, mats); break;
                case PropType.Chair:       Chair(root.transform, sx, sz, h, mats); break;
                case PropType.Bench:       Bench(root.transform, sx, sz, h, mats); break;
                case PropType.IvStand:     IvStand(root.transform, sx, sz, h, mats); break;
                case PropType.Wheelchair:  Wheelchair(root.transform, sx, sz, h, mats); break;
                default:                   Carcass(root.transform, prop.Type, sx, sz, h, mats); break;
            }

            var bc = root.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, h * 0.5f, 0f);
            bc.size = new Vector3(sx, h, sz);

            AddFootprintCollider(root, sx, sz, h);
            MarkNotWalkable(root);

            return root;
        }

        /// <summary>
        /// A teammate's furniture prefab in place of the generated primitives, used whenever the active
        /// <see cref="LevelGenProfile"/> maps <see cref="Prop.Type"/> to a prefab.
        ///
        /// The prefab is instantiated at the prop's world position with the prop's Y rotation, so it must be
        /// authored with its footprint centred on its origin and its base at y = 0. Scale is the prefab's own:
        /// this legacy overload never stretches art to fit. LevelBuilder uses the PropPrefabEntry overload,
        /// which fits the model to the footprint via ModelFit.
        ///
        /// Collision: a root BoxCollider covering footprint x height is added only when
        /// <paramref name="addColliderIfMissing"/> is set AND the prefab contains no Collider anywhere - so a
        /// prefab with hand-authored colliders keeps exactly those. A NavMeshModifier marking the root Not
        /// Walkable is added either way, which is what stops the bake walking over a bed.
        /// </summary>
        public static GameObject BuildFromPrefab(Transform parent, Prop prop, int index, GameObject prefab,
                                                 bool addColliderIfMissing)
        {
            if (prop == null || prefab == null) return null;

            float sx = Mathf.Max(0.05f, (float)prop.SizeX);
            float sz = Mathf.Max(0.05f, (float)prop.SizeZ);
            float h = Mathf.Max(0.05f, (float)prop.Height);

            GameObject root = Object.Instantiate(prefab, parent);
            root.name = $"Prop_{prop.Type}_{index}_{prefab.name}";
            root.transform.localPosition = new Vector3((float)prop.Position.X, 0f, (float)prop.Position.Z);
            root.transform.localRotation = Quaternion.Euler(0f, (float)prop.RotationDeg, 0f);

            if (addColliderIfMissing && root.GetComponentInChildren<Collider>(true) == null)
                AddFootprintCollider(root, sx, sz, h);

            MarkNotWalkable(root);

            return root;
        }

        /// <summary>
        /// A teammate's furniture prefab FITTED to the footprint Core reserved, driven by a
        /// <see cref="PropPrefabEntry"/>. This is what <see cref="LevelBuilder"/> uses.
        ///
        /// Hierarchy: an unscaled root "Prop_&lt;type&gt;_&lt;i&gt;_&lt;prefab&gt;" at the prop position with the
        /// prop's Y rotation (exactly like the primitive path), with the prefab instance as its only child. The
        /// child gets <see cref="PropPrefabEntry.extraRotationEuler"/>, then is scaled / grounded / centred per
        /// <see cref="PropPrefabEntry.fit"/> via <see cref="ModelFit"/>. Because the root is never scaled, the
        /// root BoxCollider is sized from the Core footprint exactly as for primitives.
        ///
        /// Collision: with <see cref="PropPrefabEntry.keepPrefabColliders"/> off every Collider in the instance
        /// is destroyed first. A root footprint BoxCollider is then added when
        /// <see cref="PropPrefabEntry.addColliderIfMissing"/> is set and no Collider remains. The root is
        /// marked Not Walkable either way (the modifier applies to children, so prefab colliders are covered).
        /// </summary>
        public static GameObject BuildFromPrefab(Transform parent, Prop prop, int index, PropPrefabEntry entry)
        {
            if (prop == null || entry == null || entry.prefab == null) return null;

            float sx = Mathf.Max(0.05f, (float)prop.SizeX);
            float sz = Mathf.Max(0.05f, (float)prop.SizeZ);
            float h = Mathf.Max(0.05f, (float)prop.Height);

            var root = new GameObject($"Prop_{prop.Type}_{index}_{entry.prefab.name}");
            root.layer = 0;
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3((float)prop.Position.X, 0f, (float)prop.Position.Z);
            root.transform.localRotation = Quaternion.Euler(0f, (float)prop.RotationDeg, 0f);

            GameObject model = Object.Instantiate(entry.prefab, root.transform);
            model.name = entry.prefab.name;
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(entry.extraRotationEuler);

            FitModel(model, entry, sx, sz, h);

            if (!entry.keepPrefabColliders)
            {
                // DestroyImmediate, not Destroy: a deferred destroy would still be seen by the collider check
                // below and by the NavMesh bake later this same frame. Safe - these are instance components.
                Collider[] cols = model.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < cols.Length; i++)
                    if (cols[i] != null) Object.DestroyImmediate(cols[i]);
            }

            if (entry.addColliderIfMissing && root.GetComponentInChildren<Collider>(true) == null)
                AddFootprintCollider(root, sx, sz, h);

            MarkNotWalkable(root);
            return root;
        }

        /// <summary>Scale / ground / centre <paramref name="model"/> (a child of the prop root) per the entry.</summary>
        static void FitModel(GameObject model, PropPrefabEntry entry, float sx, float sz, float h)
        {
            const float Inf = float.PositiveInfinity;
            Transform t = model.transform;

            switch (entry.fit)
            {
                case PropFitMode.UniformToHeight:
                    ModelFit.FitToHeight(model, h, entry.groundToFloor, entry.centreXZ);
                    return;

                case PropFitMode.FitFootprint:
                case PropFitMode.UniformToFootprint:
                {
                    AlignLongestHorizontal(model, sx, sz);
                    Vector3 propTarget = entry.fit == PropFitMode.FitFootprint
                        ? new Vector3(sx, h, sz)
                        : new Vector3(sx, Inf, sz);
                    ModelFit.FitToBox(model, ToModelAxes(t.localRotation, propTarget),
                                      entry.fit == PropFitMode.UniformToFootprint,
                                      entry.groundToFloor, entry.centreXZ);
                    return;
                }

                default: // None: no scaling, alignment only
                    ModelFit.FitToBox(model, new Vector3(Inf, Inf, Inf), true, entry.groundToFloor, entry.centreXZ);
                    return;
            }
        }

        /// <summary>
        /// If the model's longest horizontal extent (in the prop frame) runs across the footprint's longest
        /// horizontal axis, turn the model 90 degrees about the prop's Y so a bed modelled along X fits a
        /// footprint along Z. Nearly square models / footprints (within 5%) are left alone.
        /// </summary>
        static void AlignLongestHorizontal(GameObject model, float sx, float sz)
        {
            Transform t = model.transform;
            Bounds local = ModelFit.LocalBounds(model);
            Vector3 size = Vector3.Scale(local.size, Abs(t.localScale));

            Vector3 inProp = Vector3.zero;
            for (int i = 0; i < 3; i++)
                inProp[DominantAxis(t.localRotation * Unit(i))] = size[i];

            const float Square = 1.05f;
            bool modelSquare = Mathf.Max(inProp.x, inProp.z) <= Mathf.Min(inProp.x, inProp.z) * Square;
            bool footSquare = Mathf.Max(sx, sz) <= Mathf.Min(sx, sz) * Square;
            if (modelSquare || footSquare) return;

            if ((inProp.x > inProp.z) != (sx > sz))
                t.localRotation = Quaternion.Euler(0f, 90f, 0f) * t.localRotation;
        }

        /// <summary>A size in the prop frame re-expressed along the model's local axes (rotation in 90s).</summary>
        static Vector3 ToModelAxes(Quaternion modelRotation, Vector3 propSize)
        {
            Vector3 r = Vector3.zero;
            for (int i = 0; i < 3; i++) r[i] = propSize[DominantAxis(modelRotation * Unit(i))];
            return r;
        }

        static Vector3 Unit(int i) { Vector3 v = Vector3.zero; v[i] = 1f; return v; }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static int DominantAxis(Vector3 v)
        {
            float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
            return ax >= ay && ax >= az ? 0 : (ay >= az ? 1 : 2);
        }

        // ---------------------------------------------------------------- shared collision / nav

        static void AddFootprintCollider(GameObject root, float sx, float sz, float h)
        {
            var bc = root.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, h * 0.5f, 0f);
            bc.size = new Vector3(sx, h, sz);
        }

        /// <summary>Root NavMeshModifier with overrideArea, area 1 (Not Walkable). Idempotent.</summary>
        static void MarkNotWalkable(GameObject root)
        {
            var mod = root.GetComponent<NavMeshModifier>();
            if (mod == null) mod = root.AddComponent<NavMeshModifier>();
            mod.overrideArea = true;
            mod.area = NotWalkableArea;
        }

        // ---------------------------------------------------------------- pieces

        /// <summary>
        /// Low box frame + mattress box + thin headboard panel on the local -X end + 4 leg cylinders.
        /// <paramref name="wheels"/> raises the frame and swaps the legs for castors (that is a gurney).
        /// </summary>
        static void Bed(Transform t, float sx, float sz, float h, LevelMaterials m, bool wheels)
        {
            float legH = h * (wheels ? 0.55f : 0.42f);
            float frameH = h * 0.18f;
            float mattH = h - legH - frameH;
            float legD = 0.06f;

            GenPrimitives.BoxPart(t, "Frame", m.MetalGreyBlue,
                new Vector3(0f, legH + frameH * 0.5f, 0f), new Vector3(sx * 0.94f, frameH, sz * 0.96f));
            GenPrimitives.BoxPart(t, "Mattress", m.MattressWhite,
                new Vector3(0f, legH + frameH + mattH * 0.5f, 0f), new Vector3(sx * 0.88f, mattH, sz * 0.92f));
            GenPrimitives.BoxPart(t, "Headboard", m.MetalGreyBlue,
                new Vector3(-sx * 0.5f + 0.03f, h * 0.62f, 0f), new Vector3(0.06f, h * 1.24f, sz * 0.9f));

            float lx = sx * 0.5f - legD, lz = sz * 0.5f - legD;
            for (int i = 0; i < 4; i++)
            {
                float ox = (i < 2 ? -1f : 1f) * lx;
                float oz = (i % 2 == 0 ? -1f : 1f) * lz;
                if (wheels)
                {
                    GenPrimitives.CylPart(t, $"Leg{i}", m.MetalGreyBlue,
                        new Vector3(ox, legH * 0.5f + 0.06f, oz), legD, legH - 0.12f);
                    GenPrimitives.WheelPart(t, $"Castor{i}", m.DarkDetail,
                        new Vector3(ox, 0.06f, oz), 0.12f, 0.04f);
                }
                else
                {
                    GenPrimitives.CylPart(t, $"Leg{i}", m.MetalGreyBlue,
                        new Vector3(ox, legH * 0.5f, oz), legD, legH);
                }
            }
        }

        /// <summary>Top slab + 2 side panels (local -X and +X ends).</summary>
        static void Desk(Transform t, float sx, float sz, float h, LevelMaterials m)
        {
            float top = 0.06f;
            GenPrimitives.BoxPart(t, "Top", m.DarkWood, new Vector3(0f, h - top * 0.5f, 0f),
                new Vector3(sx, top, sz));
            float panel = 0.05f;
            GenPrimitives.BoxPart(t, "SideL", m.DarkWood,
                new Vector3(-sx * 0.5f + panel * 0.5f, (h - top) * 0.5f, 0f),
                new Vector3(panel, h - top, sz * 0.92f));
            GenPrimitives.BoxPart(t, "SideR", m.DarkWood,
                new Vector3(sx * 0.5f - panel * 0.5f, (h - top) * 0.5f, 0f),
                new Vector3(panel, h - top, sz * 0.92f));
        }

        /// <summary>Seat + back (local -X end) + 4 legs.</summary>
        static void Chair(Transform t, float sx, float sz, float h, LevelMaterials m)
        {
            float seatY = h * 0.48f, seatT = 0.05f, legD = 0.04f;
            GenPrimitives.BoxPart(t, "Seat", m.Laminate, new Vector3(0f, seatY, 0f),
                new Vector3(sx * 0.92f, seatT, sz * 0.92f));
            GenPrimitives.BoxPart(t, "Back", m.Laminate,
                new Vector3(-sx * 0.5f + 0.03f, seatY + (h - seatY) * 0.5f, 0f),
                new Vector3(0.05f, h - seatY, sz * 0.86f));

            float lx = sx * 0.5f - legD * 1.6f, lz = sz * 0.5f - legD * 1.6f;
            for (int i = 0; i < 4; i++)
                GenPrimitives.CylPart(t, $"Leg{i}", m.MetalGreyBlue,
                    new Vector3((i < 2 ? -1f : 1f) * lx, (seatY - seatT * 0.5f) * 0.5f,
                                (i % 2 == 0 ? -1f : 1f) * lz),
                    legD, seatY - seatT * 0.5f);
        }

        /// <summary>Slab + 2 legs.</summary>
        static void Bench(Transform t, float sx, float sz, float h, LevelMaterials m)
        {
            float top = Mathf.Min(0.08f, h * 0.3f);
            GenPrimitives.BoxPart(t, "Slab", m.DarkWood, new Vector3(0f, h - top * 0.5f, 0f),
                new Vector3(sx, top, sz));
            float legT = 0.06f;
            for (int i = 0; i < 2; i++)
                GenPrimitives.BoxPart(t, $"Leg{i}", m.MetalGreyBlue,
                    new Vector3((i == 0 ? -1f : 1f) * (sx * 0.5f - 0.12f), (h - top) * 0.5f, 0f),
                    new Vector3(legT, h - top, sz * 0.8f));
        }

        /// <summary>Thin cylinder pole + small box base + a small bag box near the top.</summary>
        static void IvStand(Transform t, float sx, float sz, float h, LevelMaterials m)
        {
            float baseH = 0.06f;
            GenPrimitives.BoxPart(t, "Base", m.DarkDetail, new Vector3(0f, baseH * 0.5f, 0f),
                new Vector3(sx * 0.8f, baseH, sz * 0.8f));
            GenPrimitives.CylPart(t, "Pole", m.MetalGreyBlue, new Vector3(0f, h * 0.5f, 0f), 0.04f, h);
            GenPrimitives.BoxPart(t, "Bag", m.MattressWhite, new Vector3(0.07f, h * 0.86f, 0f),
                new Vector3(0.10f, h * 0.17f, 0.18f));
        }

        /// <summary>Seat box + back + two large thin cylinder wheels.</summary>
        static void Wheelchair(Transform t, float sx, float sz, float h, LevelMaterials m)
        {
            float seatY = h * 0.48f;
            GenPrimitives.BoxPart(t, "Seat", m.DarkDetail, new Vector3(0f, seatY, 0f),
                new Vector3(sx * 0.7f, 0.07f, sz * 0.62f));
            GenPrimitives.BoxPart(t, "Back", m.DarkDetail,
                new Vector3(-sx * 0.5f + 0.05f, seatY + (h - seatY) * 0.5f, 0f),
                new Vector3(0.06f, h - seatY, sz * 0.6f));

            float wheelD = Mathf.Min(h * 0.86f, sz * 0.9f);
            for (int i = 0; i < 2; i++)
                GenPrimitives.WheelPart(t, $"Wheel{i}", m.MetalGreyBlue,
                    new Vector3((i == 0 ? -1f : 1f) * (sx * 0.5f - 0.03f), wheelD * 0.5f, -sz * 0.08f),
                    wheelD, 0.05f);
            for (int i = 0; i < 2; i++)
                GenPrimitives.WheelPart(t, $"Castor{i}", m.DarkDetail,
                    new Vector3((i == 0 ? -1f : 1f) * (sx * 0.5f - 0.06f), 0.08f, sz * 0.4f), 0.16f, 0.04f);
        }

        /// <summary>
        /// FilingCabinet / Shelf / Counter / BedsideCabinet: a box with 2-3 inset horizontal lines faked by
        /// thin darker slabs (drawer gaps / shelf edges). No textures, so the silhouette does the work.
        /// </summary>
        static void Carcass(Transform t, PropType type, float sx, float sz, float h, LevelMaterials m)
        {
            Material body = type == PropType.Counter ? m.Laminate
                          : type == PropType.Shelf ? m.MetalGreyBlue
                          : m.Laminate;

            GenPrimitives.BoxPart(t, "Body", body, new Vector3(0f, h * 0.5f, 0f), new Vector3(sx, h, sz));

            int lines = type == PropType.FilingCabinet || type == PropType.Shelf ? 3 : 2;
            for (int i = 1; i <= lines; i++)
            {
                float y = h * i / (lines + 1f);
                GenPrimitives.BoxPart(t, $"Line{i}", m.DarkDetail, new Vector3(0f, y, 0f),
                    new Vector3(sx * 1.01f, 0.025f, sz * 1.01f));
            }
        }
    }
}
