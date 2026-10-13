// Fit teammate art (prefabs, imported FBX / OBJ models) to the footprints LevelGen.Core reserves.
//
// WHY: models arrive at whatever scale and pivot the author exported. The hospital bed FBX and the chair have
// unknown sizes, and Assets/Models/basicmonster.obj is 3.6 x 9.4 x 9.9 m with its bounds centre far from its
// origin. Placing them "as authored" puts a 10 m monster through the ceiling and a bed half inside a wall.
//
// HOW: every measurement is taken from the instance's MESH data (MeshFilter.sharedMesh bounds, collider
// shapes) pushed through the transform hierarchy, NOT from Renderer.bounds alone, because Renderer.bounds is
// empty for inactive objects and can be stale until the next render. Measuring in the instance's own local
// frame makes the result independent of the instance's current scale, so fitting is one exact step, not an
// iteration, and it works whatever the child pivots are.
using UnityEngine;

namespace LevelGen.Unity
{
    /// <summary>
    /// Bounds measurement and scale-to-fit for arbitrary prefab instances.
    ///
    /// <example>
    /// Scale a monster OBJ to 2.2 m tall, standing on the floor, centred where it was placed:
    /// <code>
    /// GameObject m = Object.Instantiate(monsterPrefab, spawnPoint, Quaternion.identity);
    /// ModelFit.FitToHeight(m, 2.2f);                       // uniform, grounded, centred on spawnPoint.xz
    /// </code>
    /// Squash a bed model into an exact 1.0 x 0.9 x 2.1 m box (per axis, along the instance's local axes):
    /// <code>
    /// ModelFit.FitToBox(bed, new Vector3(1.0f, 0.9f, 2.1f), uniform: false, groundToFloor: true, centreXZ: true);
    /// </code>
    /// Fit a lamp inside 0.4 x 0.4 m horizontally without constraining its height (infinite = "no limit"):
    /// <code>
    /// ModelFit.FitToBox(lamp, new Vector3(0.4f, float.PositiveInfinity, 0.4f), true, true, true);
    /// </code>
    /// </example>
    /// </summary>
    public static class ModelFit
    {
        const float Epsilon = 1e-5f;

        /// <summary>
        /// World-space AABB of every Renderer under <paramref name="root"/> (inactive ones included; particle,
        /// line and trail renderers skipped). Falls back to the Colliders when there are no renderers, and to a
        /// zero-size bounds at the root's position when there is nothing at all.
        /// </summary>
        public static Bounds RendererBounds(GameObject root)
        {
            if (root == null) return new Bounds(Vector3.zero, Vector3.zero);
            Bounds b;
            return Collect(root, Matrix4x4.identity, out b) ? b : new Bounds(root.transform.position, Vector3.zero);
        }

        /// <summary>
        /// Same content as <see cref="RendererBounds"/>, but as an AABB in <paramref name="root"/>'s own local
        /// frame (so it does not change when the root is scaled, rotated or moved). Zero bounds at the local
        /// origin when there is nothing to measure.
        /// </summary>
        public static Bounds LocalBounds(GameObject root)
        {
            if (root == null) return new Bounds(Vector3.zero, Vector3.zero);
            Bounds b;
            return Collect(root, root.transform.worldToLocalMatrix, out b) ? b : new Bounds(Vector3.zero, Vector3.zero);
        }

        /// <summary>
        /// Scale <paramref name="instance"/>'s root localScale so its renderer bounds measure
        /// <paramref name="targetSize"/> metres along the instance's OWN local axes (x, y, z), then translate it.
        ///
        /// <para><paramref name="uniform"/> = false: each axis is scaled independently. true: one factor for all
        /// three axes - the smallest target/current ratio, i.e. the factor that makes the most over-sized axis
        /// fit, so the model ends up inside the box. The prefab's own scale ratios are preserved.</para>
        ///
        /// <para>A target axis that is &lt;= 0, NaN or infinite means "no constraint on this axis": it is left
        /// unscaled per-axis and ignored when choosing the uniform factor. So a height-driven fit is
        /// <c>new Vector3(float.PositiveInfinity, height, float.PositiveInfinity)</c> with uniform = true.</para>
        ///
        /// <para>Placement uses the root's world position BEFORE the call as the intended anchor:
        /// <paramref name="groundToFloor"/> moves the root so the bounds' lowest point sits at that y;
        /// <paramref name="centreXZ"/> moves it so the bounds' xz centre sits at that xz. Works with arbitrary
        /// child pivots and an off-centre model, because only the root is moved.</para>
        ///
        /// <para>Scaling is along the instance's own local axes, so its rotation never distorts the result;
        /// only a skewed ancestry (a non-uniformly scaled parent above a rotated child) is approximated.</para>
        /// </summary>
        public static void FitToBox(GameObject instance, Vector3 targetSize, bool uniform, bool groundToFloor,
                                    bool centreXZ)
        {
            if (instance == null) return;
            Transform t = instance.transform;
            Vector3 anchor = t.position;

            Bounds local;
            if (!Collect(instance, t.worldToLocalMatrix, out local)) return; // nothing to measure

            Vector3 scale = t.localScale;
            Vector3 factor = new Vector3(float.NaN, float.NaN, float.NaN);
            float uniformK = float.PositiveInfinity;

            for (int i = 0; i < 3; i++)
            {
                float target = targetSize[i];
                if (!(target > 0f) || float.IsInfinity(target)) continue;

                // World metres per local unit along this axis (all ancestor + own scale), times the local extent.
                Vector3 axis = Vector3.zero;
                axis[i] = 1f;
                float current = local.size[i] * t.TransformVector(axis).magnitude;
                if (current < Epsilon) continue;

                factor[i] = target / current;
                uniformK = Mathf.Min(uniformK, factor[i]);
            }

            if (uniform)
            {
                if (!float.IsInfinity(uniformK)) scale *= uniformK;
            }
            else
            {
                for (int i = 0; i < 3; i++)
                    if (!float.IsNaN(factor[i])) scale[i] *= factor[i];
            }

            t.localScale = scale;
            Align(t, local, anchor, groundToFloor, centreXZ);
        }

        /// <summary>
        /// Uniformly scale <paramref name="instance"/> to <paramref name="height"/> metres tall, measured along
        /// whichever of its local axes currently points most nearly up (so a Z-up model that has been rotated
        /// upright still fits by its real height). Then ground / centre as in <see cref="FitToBox"/>.
        /// </summary>
        public static void FitToHeight(GameObject instance, float height, bool groundToFloor = true,
                                       bool centreXZ = true)
        {
            if (instance == null) return;
            Transform t = instance.transform;

            int up = 1;
            float best = -1f;
            for (int i = 0; i < 3; i++)
            {
                Vector3 axis = Vector3.zero;
                axis[i] = 1f;
                float y = Mathf.Abs(t.TransformDirection(axis).y);
                if (y > best) { best = y; up = i; }
            }

            Vector3 target = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            target[up] = height;
            FitToBox(instance, target, true, groundToFloor, centreXZ);
        }

        // ---------------------------------------------------------------- internals

        /// <summary>Move the root so the (already scaled) local bounds sit on / centre over the anchor.</summary>
        static void Align(Transform t, Bounds local, Vector3 anchor, bool groundToFloor, bool centreXZ)
        {
            if (!groundToFloor && !centreXZ) return;

            bool any = false;
            Bounds world = new Bounds();
            Encapsulate(ref world, ref any, local, t.localToWorldMatrix);
            if (!any) return;

            Vector3 delta = Vector3.zero;
            if (groundToFloor) delta.y = anchor.y - world.min.y;
            if (centreXZ)
            {
                delta.x = anchor.x - world.center.x;
                delta.z = anchor.z - world.center.z;
            }
            t.position += delta;
        }

        /// <summary>
        /// Union of all renderer (else collider) bounds under <paramref name="root"/>, expressed in the frame
        /// that <paramref name="worldToTarget"/> maps world space into. False when nothing was measurable.
        /// </summary>
        static bool Collect(GameObject root, Matrix4x4 worldToTarget, out Bounds result)
        {
            bool any = false;
            result = new Bounds();

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || IsIgnored(r)) continue;

                Mesh mesh = null;
                if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
                else
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }

                if (mesh != null && mesh.bounds.size.sqrMagnitude > 0f)
                {
                    Encapsulate(ref result, ref any, mesh.bounds, worldToTarget * r.transform.localToWorldMatrix);
                }
                else
                {
                    Bounds wb = r.bounds; // sprites and other non-mesh renderers; empty when inactive
                    if (wb.size.sqrMagnitude > 0f) Encapsulate(ref result, ref any, wb, worldToTarget);
                }
            }
            if (any) return true;

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null) continue;
                Bounds lb;
                if (TryColliderLocalBounds(c, out lb))
                    Encapsulate(ref result, ref any, lb, worldToTarget * c.transform.localToWorldMatrix);
                else if (c.bounds.size.sqrMagnitude > 0f)
                    Encapsulate(ref result, ref any, c.bounds, worldToTarget);
            }
            return any;
        }

        static bool TryColliderLocalBounds(Collider c, out Bounds b)
        {
            switch (c)
            {
                case BoxCollider box:
                    b = new Bounds(box.center, box.size);
                    return true;
                case SphereCollider s:
                    b = new Bounds(s.center, Vector3.one * (s.radius * 2f));
                    return true;
                case CapsuleCollider cap:
                {
                    Vector3 size = Vector3.one * (cap.radius * 2f);
                    size[Mathf.Clamp(cap.direction, 0, 2)] = Mathf.Max(cap.height, cap.radius * 2f);
                    b = new Bounds(cap.center, size);
                    return true;
                }
                case MeshCollider mc when mc.sharedMesh != null:
                    b = mc.sharedMesh.bounds;
                    return true;
                default:
                    b = default;
                    return false;
            }
        }

        /// <summary>Effects renderers whose bounds are not the model's silhouette.</summary>
        static bool IsIgnored(Renderer r)
        {
            string n = r.GetType().Name;
            return n == "ParticleSystemRenderer" || n == "LineRenderer" || n == "TrailRenderer";
        }

        /// <summary>Grow <paramref name="acc"/> by the 8 corners of <paramref name="b"/> mapped through <paramref name="m"/>.</summary>
        static void Encapsulate(ref Bounds acc, ref bool any, Bounds b, Matrix4x4 m)
        {
            Vector3 mn = b.min, mx = b.max;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? mn.x : mx.x,
                                                           (i & 2) == 0 ? mn.y : mx.y,
                                                           (i & 4) == 0 ? mn.z : mx.z));
                if (!any) { acc = new Bounds(p, Vector3.zero); any = true; }
                else acc.Encapsulate(p);
            }
        }
    }
}
