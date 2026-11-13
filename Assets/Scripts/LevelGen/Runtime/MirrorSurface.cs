// A planar wall mirror: a glass quad over a dark frame, showing a RenderTexture drawn by a child camera that
// sits at the viewer's position reflected across the mirror plane.
//
// Projection: the mirror camera looks straight out of the wall (along the room-facing normal) and uses an
// OFF-AXIS frustum whose edges pass through the four corners of the glass and whose near plane is the glass
// plane itself. That does two jobs at once: the RenderTexture maps 1:1 onto the quad (no screen-space
// projection needed), and everything on the wall side of the glass - the wall, the frame, the room behind -
// is clipped exactly as an oblique near plane would clip it. Because the camera's right axis is the viewer's
// left, the quad's U coordinate is flipped.
//
// Budget: at most MaxActiveMirrors render per frame, nearest first, and only when the viewer is within
// mirrorActiveDistance, in front of the glass, and the glass is inside the viewer's frustum. Every other
// mirror disables its camera and shows a dark tinted glass material.
//
// Layers (explicit on purpose): the mirror camera draws every layer except UI, and always includes the
// PlayerBody layer (see PlayerBodyLayer), which the player's own first-person camera removes from its mask.
// That is what makes the player's body visible in a mirror and nowhere else.
//
// Fog and ambient are global RenderSettings, so the mirror camera renders with the same fog as the main view.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LevelGen.Unity
{
    [DisallowMultipleComponent]
    public sealed class MirrorSurface : MonoBehaviour
    {
        // ------------------------------------------------------------------ layer convention

        /// <summary>Unity's built-in UI layer.</summary>
        public const int UILayer = 5;

        /// <summary>
        /// Layer index reserved for the player's body. Layers cannot be created by name at runtime, so the
        /// convention is index 30; name it "PlayerBody" in Project Settings &gt; Tags and Layers. If a layer
        /// named "PlayerBody" exists at another index, that index wins (see <see cref="ResolvePlayerBodyLayer"/>).
        /// </summary>
        public const int PlayerBodyLayer = 30;
        public const string PlayerBodyLayerName = "PlayerBody";

        /// <summary>The layer the player body actually uses: the named layer if the project defines it, else 30.</summary>
        public static int ResolvePlayerBodyLayer()
        {
            int named = LayerMask.NameToLayer(PlayerBodyLayerName);
            return named >= 0 ? named : PlayerBodyLayer;
        }

        /// <summary>Everything except UI, and explicitly including the player-body layer.</summary>
        public static int MirrorCullingMask => (~(1 << UILayer)) | (1 << ResolvePlayerBodyLayer());

        // ------------------------------------------------------------------ settings

        /// <summary>Hard cap on mirrors rendering in one frame (nearest to the viewer first).</summary>
        public static int MaxActiveMirrors = 3;

        /// <summary>Optional viewer override; when null the mirror follows Camera.main.</summary>
        public static Camera Viewer;

        public float mirrorActiveDistance = 18f;
        public int textureWidth = 512;
        public int textureHeight = 768;
        public float width = 1.2f;
        public float height = 1.8f;

        // ------------------------------------------------------------------ state

        static readonly List<MirrorSurface> registry = new List<MirrorSurface>();
        static readonly List<MirrorSurface> candidates = new List<MirrorSurface>();
        static readonly HashSet<MirrorSurface> activeSet = new HashSet<MirrorSurface>();
        static readonly Plane[] frustumPlanes = new Plane[6];
        static int selectedFrame = -1;

        Transform glass;          // the quad; its +Z points into the wall, -Z into the room
        MeshRenderer glassRenderer;
        Camera mirrorCam;
        RenderTexture rt;
        Material liveMat;         // samples rt; created on first activation (play mode only)
        bool isLive;
        float sortDistance;

        /// <summary>Number of mirrors currently registered (for debug HUDs and tests).</summary>
        public static int RegisteredCount => registry.Count;

        /// <summary>True while this mirror's camera is rendering this frame.</summary>
        public bool IsLive => isLive;

        // ------------------------------------------------------------------ construction

        /// <summary>
        /// Build a mirror under <paramref name="parent"/>. <paramref name="wallPoint"/> is a WORLD point on the wall
        /// surface at FLOOR height, <paramref name="wallNormalIntoRoom"/> the wall's normal pointing into the room
        /// (horizontal). The glass is centred <paramref name="centreHeight"/> above the wall point.
        /// </summary>
        public static GameObject Build(Transform parent, Vector3 wallPoint, Vector3 wallNormalIntoRoom,
                                       float width = 1.2f, float height = 1.8f, float centreHeight = 1.5f)
        {
            Vector3 n = wallNormalIntoRoom;
            n.y = 0f;
            n = n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.back;

            var root = new GameObject("Mirror");
            root.layer = 0;
            if (parent != null) root.transform.SetParent(parent, false);
            // +Z into the wall, so the glass (which faces local -Z) faces into the room.
            root.transform.SetPositionAndRotation(wallPoint + Vector3.up * centreHeight,
                                                  Quaternion.LookRotation(-n, Vector3.up));

            const float frameDepth = 0.05f;
            const float frameBorder = 0.06f;
            const float glassGap = 0.006f;

            // Frame: a dark box between the wall and the glass, slightly larger than the glass.
            var frame = GenPrimitives.Part(root.transform, "Frame", GenPrimitives.Box, FrameMaterial,
                new Vector3(0f, 0f, -frameDepth * 0.5f),
                new Vector3(width + frameBorder * 2f, height + frameBorder * 2f, frameDepth),
                Quaternion.identity);
            frame.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

            // Glass quad, just proud of the frame's front face.
            var glassGo = GenPrimitives.Part(root.transform, "Glass", GlassMesh, null,
                new Vector3(0f, 0f, -frameDepth - glassGap), new Vector3(width, height, 1f), Quaternion.identity);
            var mr = glassGo.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            var m = root.AddComponent<MirrorSurface>();
            m.width = width;
            m.height = height;
            m.glass = glassGo.transform;
            m.glassRenderer = mr;
            mr.sharedMaterial = IdleMaterial;
            return root;
        }

        // ------------------------------------------------------------------ lifecycle

        void OnEnable() { if (!registry.Contains(this)) registry.Add(this); }

        void OnDisable()
        {
            registry.Remove(this);
            activeSet.Remove(this);
            SetLive(false);
        }

        void OnDestroy()
        {
            registry.Remove(this);
            activeSet.Remove(this);
            if (mirrorCam != null) mirrorCam.targetTexture = null;
            if (rt != null) { rt.Release(); DestroyObj(rt); rt = null; }
            if (liveMat != null) { DestroyObj(liveMat); liveMat = null; }
        }

        static void DestroyObj(Object o)
        {
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        void LateUpdate()
        {
            Camera viewer = Viewer != null ? Viewer : Camera.main;
            if (selectedFrame != Time.frameCount) Select(viewer);

            bool want = viewer != null && activeSet.Contains(this);
            if (want && EnsureCamera()) { Place(viewer); SetLive(true); }
            else SetLive(false);
        }

        // ------------------------------------------------------------------ selection (once per frame)

        static void Select(Camera viewer)
        {
            selectedFrame = Time.frameCount;
            activeSet.Clear();
            candidates.Clear();
            if (viewer == null || MaxActiveMirrors <= 0) return;

            GeometryUtility.CalculateFrustumPlanes(viewer, frustumPlanes);
            Vector3 eye = viewer.transform.position;
            for (int i = 0; i < registry.Count; i++)
            {
                var m = registry[i];
                if (m == null || m.glass == null || !m.isActiveAndEnabled) continue;
                Vector3 c = m.glass.position;
                Vector3 n = m.RoomNormal;
                float dist = Vector3.Distance(eye, c);
                if (dist > m.mirrorActiveDistance) continue;
                if (Vector3.Dot(eye - c, n) <= 0.02f) continue;               // behind or edge-on
                if (m.glassRenderer != null && !GeometryUtility.TestPlanesAABB(frustumPlanes, m.glassRenderer.bounds))
                    continue;                                                  // not on screen
                m.sortDistance = dist;
                candidates.Add(m);
            }
            candidates.Sort((a, b) => a.sortDistance.CompareTo(b.sortDistance));
            for (int i = 0; i < candidates.Count && i < MaxActiveMirrors; i++) activeSet.Add(candidates[i]);
        }

        /// <summary>World-space normal of the glass, pointing into the room.</summary>
        public Vector3 RoomNormal => glass != null ? -glass.forward : -transform.forward;

        // ------------------------------------------------------------------ camera

        bool EnsureCamera()
        {
            if (!Application.isPlaying || glass == null) return false;
            if (mirrorCam != null) return true;

            rt = new RenderTexture(Mathf.Max(16, textureWidth), Mathf.Max(16, textureHeight), 24,
                                   RenderTextureFormat.ARGB32)
            {
                name = "MirrorRT",
                antiAliasing = 1,
                useMipMap = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            rt.Create();

            var camGo = new GameObject("MirrorCam");
            camGo.layer = 0;
            camGo.transform.SetParent(transform, false);
            mirrorCam = camGo.AddComponent<Camera>();
            mirrorCam.enabled = false;
            mirrorCam.targetTexture = rt;
            mirrorCam.cullingMask = MirrorCullingMask;   // everything but UI, plus the PlayerBody layer
            mirrorCam.useOcclusionCulling = false;        // the custom frustum confuses occlusion data
            mirrorCam.allowHDR = false;
            mirrorCam.allowMSAA = false;

            var data = mirrorCam.GetUniversalAdditionalCameraData();
            data.renderType = CameraRenderType.Base;
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            data.requiresColorTexture = false;
            data.requiresDepthTexture = false;
            data.renderShadows = false;                   // cost: up to three extra views per frame

            liveMat = BuildLiveMaterial(rt);
            return true;
        }

        /// <summary>
        /// URP Unlit sampling the RT. A player build strips shaders no material asset references, so when
        /// "Universal Render Pipeline/Unlit" is missing, fall back to URP Lit with a black base and the RT as
        /// emission (Lit + emission is already in use for light fixtures, so it survives stripping).
        /// </summary>
        static Material BuildLiveMaterial(RenderTexture tex)
        {
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            Material mat;
            if (unlit != null)
            {
                mat = new Material(unlit) { name = "MirrorGlassLive" };
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                mat.mainTexture = tex;
            }
            else
            {
                mat = LevelMaterials.Make("MirrorGlassLive", Color.black, 0.9f);
                mat.SetTexture("_EmissionMap", tex);
                mat.SetColor("_EmissionColor", Color.white);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            return mat;
        }

        void SetLive(bool live)
        {
            isLive = live;
            if (mirrorCam != null && mirrorCam.enabled != live) mirrorCam.enabled = live;
            if (glassRenderer != null)
            {
                Material want = live && liveMat != null ? liveMat : IdleMaterial;
                if (glassRenderer.sharedMaterial != want) glassRenderer.sharedMaterial = want;
            }
        }

        /// <summary>Put the camera at the viewer reflected across the glass, with an off-axis frustum through the glass.</summary>
        void Place(Camera viewer)
        {
            Vector3 c = glass.position;
            Vector3 n = RoomNormal;
            Vector3 eye = viewer.transform.position;

            float d = Vector3.Dot(eye - c, n);                 // viewer's distance in front of the glass
            d = Mathf.Max(d, 0.02f);
            Vector3 reflected = eye - 2f * d * n;              // reflection of the eye across the plane

            // Looking straight out of the wall. Camera right = cross(up, n) = the room viewer's LEFT.
            Quaternion rot = Quaternion.LookRotation(n, Vector3.up);
            mirrorCam.transform.SetPositionAndRotation(reflected, rot);

            Vector3 camRight = rot * Vector3.right;
            Vector3 toCentre = c - reflected;                  // depth along n is exactly d
            float cx = Vector3.Dot(toCentre, camRight);
            float cy = Vector3.Dot(toCentre, Vector3.up);

            // Near plane a hair in front of the glass so the glass quad itself never renders into its own RT.
            float near = d + 0.01f;
            float far = Mathf.Max(near + 1f, viewer.farClipPlane);
            float k = near / d;
            float l = (cx - width * 0.5f) * k, r = (cx + width * 0.5f) * k;
            float b = (cy - height * 0.5f) * k, t = (cy + height * 0.5f) * k;

            mirrorCam.nearClipPlane = near;
            mirrorCam.farClipPlane = far;
            mirrorCam.aspect = width / height;
            mirrorCam.projectionMatrix = Matrix4x4.Frustum(l, r, b, t, near, far);
            mirrorCam.depth = viewer.depth - 1f;               // draw before the main view uses the RT
            mirrorCam.clearFlags = viewer.clearFlags;
            mirrorCam.backgroundColor = viewer.backgroundColor;
            mirrorCam.cullingMask = MirrorCullingMask;
        }

        // ------------------------------------------------------------------ shared assets

        // Process-wide like GenPrimitives' meshes: built once, never destroyed by a level rebuild, so an
        // edit-mode build (where OnDestroy never runs) cannot leak them per mirror. Only the per-mirror
        // RenderTexture and live material are owned by the instance, and those exist only in play mode.
        static Mesh glassMesh;
        static Material frameMaterial;
        static Material idleMaterial;

        /// <summary>Dark, glossy glass shown while a mirror is not rendering (too far, behind it, or over budget).</summary>
        static Material IdleMaterial =>
            idleMaterial != null ? idleMaterial
                                 : (idleMaterial = LevelMaterials.Make("MirrorGlassIdle", new Color(0.06f, 0.07f, 0.075f), 0.95f));

        /// <summary>
        /// Unit quad in local XY (centred), facing local -Z (visible from the room). U is flipped: the
        /// mirror camera's right is the viewer's left, so u = 0 sits on the local +X edge.
        /// </summary>
        static Mesh GlassMesh
        {
            get
            {
                if (glassMesh != null) return glassMesh;
                glassMesh = new Mesh { name = "MirrorGlassQuad" };
                glassMesh.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
                    new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f)
                };
                glassMesh.uv = new[]
                {
                    new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f)
                };
                glassMesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
                glassMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };   // clockwise seen from -Z
                glassMesh.RecalculateBounds();
                glassMesh.RecalculateTangents();
                return glassMesh;
            }
        }

        static Material FrameMaterial =>
            frameMaterial != null ? frameMaterial
                                  : (frameMaterial = LevelMaterials.Make("MirrorFrame", new Color(0.09f, 0.08f, 0.07f), 0.3f));
    }
}
