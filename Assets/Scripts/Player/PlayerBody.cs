// A visible body for the first-person player, so mirrors (LevelGen.Unity.MirrorSurface) have someone to show.
//
// Placeholder: torso capsule, head sphere, two arms, two legs, ~1.75 m, dark hospital gown. The head follows the
// camera pivot (pitch and any pivot yaw, both limited), the body yaws with the player root, legs swing with
// horizontal speed and arms swing opposite, and crouching squashes legs + torso so the head lands near the
// CharacterController's current height.
//
// customModelPrefab (a teammate's player model): instantiated instead of the primitives, fitted to 1.75 m via
// ModelFit.FitToHeight, same layer, colliders stripped; it only yaw-follows (no procedural limbs).
//
// LAYERS (explicit on purpose):
//  * Every body renderer goes on the PlayerBody layer: index 30 by convention, or whatever index a layer named
//    "PlayerBody" has in Tags and Layers (MirrorSurface.ResolvePlayerBodyLayer). Name layer 30 "PlayerBody" in
//    the TagManager; the code works without the name.
//  * At Start that layer is REMOVED from the player's own first-person camera, so the player never sees the
//    inside of their own head. Mirror cameras use MirrorSurface.MirrorCullingMask, which always INCLUDES it.
using LevelGen.Unity;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Player
{
    [DisallowMultipleComponent]
    public sealed class PlayerBody : MonoBehaviour
    {
        [Tooltip("Optional player model. When set it replaces the primitive body (fitted to bodyHeight).")]
        public GameObject customModelPrefab;

        [Tooltip("First-person camera to hide the body from. Defaults to the controller's camera pivot camera, " +
                 "else the first child Camera.")]
        public Camera firstPersonCamera;

        public float bodyHeight = 1.75f;
        public Color gownColour = new Color(0.17f, 0.24f, 0.25f);
        public Color legColour = new Color(0.12f, 0.14f, 0.15f);
        public Color skinColour = new Color(0.62f, 0.55f, 0.50f);

        [Header("Animation")]
        public float maxLegSwingDeg = 32f;
        public float maxArmSwingDeg = 24f;
        [Tooltip("Metres covered per full leg cycle at walking pace.")]
        public float strideLength = 1.5f;
        public float headPitchMin = -45f;
        public float headPitchMax = 55f;
        public float headYawLimit = 70f;

        // Proportions (metres) of the primitive body before crouch squash; sum = bodyHeight.
        const float LegLen = 0.88f;
        const float TorsoLen = 0.64f;
        const float HeadDia = 0.23f;

        FirstPersonController fpc;
        CharacterController cc;
        Transform cameraPivot;
        Transform bodyRoot, hips, torso, head, legL, legR, armL, armR;
        Material gownMat, legMat, skinMat;
        float phase;
        int bodyLayer;
        bool built;

        /// <summary>The layer the body renders on (30 unless a layer named "PlayerBody" exists elsewhere).</summary>
        public int BodyLayer => bodyLayer;

        /// <summary>
        /// Add (or fetch) a PlayerBody on the player root. In play mode the body is built immediately; in edit
        /// mode (scene setup code) only the component is added and the body is built at Start, so no generated
        /// parts are ever saved into a scene.
        /// </summary>
        public static PlayerBody Install(GameObject playerRoot) => Install(playerRoot, null);

        /// <summary>As <see cref="Install(GameObject)"/>, with a player model to use instead of the primitives.</summary>
        public static PlayerBody Install(GameObject playerRoot, GameObject customModel)
        {
            if (playerRoot == null) return null;
            var body = playerRoot.GetComponent<PlayerBody>();
            if (body == null) body = playerRoot.AddComponent<PlayerBody>();
            if (customModel != null) body.customModelPrefab = customModel;
            if (Application.isPlaying) body.Build();
            return body;
        }

        void Start() { Build(); }

        /// <summary>Idempotent: builds the body once.</summary>
        public void Build()
        {
            if (built) return;
            built = true;
            Transform stale = transform.Find("PlayerBody");
            if (stale != null) Destroy(stale.gameObject);
            bodyLayer = MirrorSurface.ResolvePlayerBodyLayer();
            fpc = GetComponent<FirstPersonController>();
            cc = GetComponent<CharacterController>();
            cameraPivot = fpc != null && fpc.cameraPivot != null ? fpc.cameraPivot : null;
            if (firstPersonCamera == null)
            {
                if (cameraPivot != null) firstPersonCamera = cameraPivot.GetComponentInChildren<Camera>();
                if (firstPersonCamera == null) firstPersonCamera = GetComponentInChildren<Camera>();
                if (firstPersonCamera == null) firstPersonCamera = Camera.main;
            }
            if (cameraPivot == null && firstPersonCamera != null) cameraPivot = firstPersonCamera.transform;

            bodyRoot = new GameObject("PlayerBody").transform;
            bodyRoot.SetParent(transform, false);   // yaws with the player root

            if (customModelPrefab != null) BuildCustom();
            else BuildPrimitives();

            SetLayerRecursive(bodyRoot.gameObject, bodyLayer);
            HideFromFirstPersonCamera();
        }

        /// <summary>Remove the body layer from the first-person camera (mirror cameras keep it).</summary>
        public void HideFromFirstPersonCamera()
        {
            if (firstPersonCamera != null) firstPersonCamera.cullingMask &= ~(1 << bodyLayer);
        }

        // ------------------------------------------------------------------ building

        void BuildCustom()
        {
            GameObject model = Instantiate(customModelPrefab, bodyRoot);
            model.name = customModelPrefab.name;
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            foreach (var col in model.GetComponentsInChildren<Collider>(true)) DestroyImmediate(col);
            ModelFit.FitToHeight(model, bodyHeight, true, true);
        }

        void BuildPrimitives()
        {
            gownMat = LevelMaterials.Make("PlayerGown", gownColour, 0.15f);
            legMat = LevelMaterials.Make("PlayerLegs", legColour, 0.1f);
            skinMat = LevelMaterials.Make("PlayerSkin", skinColour, 0.25f);

            float scale = bodyHeight / (LegLen + TorsoLen + HeadDia);
            float legLen = LegLen * scale, torsoLen = TorsoLen * scale, headDia = HeadDia * scale;

            hips = Pivot("Hips", bodyRoot, new Vector3(0f, legLen, 0f));
            legL = Pivot("LegL", hips, new Vector3(-0.1f, 0f, 0f));
            legR = Pivot("LegR", hips, new Vector3(0.1f, 0f, 0f));
            Limb(legL, PrimitiveType.Capsule, legMat, new Vector3(0.15f, legLen * 0.5f, 0.15f), -legLen * 0.5f);
            Limb(legR, PrimitiveType.Capsule, legMat, new Vector3(0.15f, legLen * 0.5f, 0.15f), -legLen * 0.5f);

            torso = Pivot("Torso", hips, Vector3.zero);
            Limb(torso, PrimitiveType.Capsule, gownMat, new Vector3(0.40f, torsoLen * 0.5f, 0.25f), torsoLen * 0.5f);

            // Shoulders and neck are positioned (not parented under the scaled torso) so crouch squash never
            // turns the head into an egg.
            armL = Pivot("ArmL", bodyRoot, Vector3.zero);
            armR = Pivot("ArmR", bodyRoot, Vector3.zero);
            float armLen = 0.66f * scale;
            Limb(armL, PrimitiveType.Capsule, gownMat, new Vector3(0.11f, armLen * 0.5f, 0.11f), -armLen * 0.5f);
            Limb(armR, PrimitiveType.Capsule, gownMat, new Vector3(0.11f, armLen * 0.5f, 0.11f), -armLen * 0.5f);

            head = Pivot("Head", bodyRoot, Vector3.zero);
            Limb(head, PrimitiveType.Sphere, skinMat, new Vector3(headDia * 0.9f, headDia, headDia), headDia * 0.5f);

            ApplyCrouch(1f);
        }

        static Transform Pivot(string name, Transform parent, Vector3 localPos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            return t;
        }

        /// <summary>A collider-free primitive hanging from <paramref name="pivot"/>, offset along local Y.</summary>
        static void Limb(Transform pivot, PrimitiveType type, Material mat, Vector3 scale, float yOffset)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = pivot.name + "Mesh";
            var col = go.GetComponent<Collider>();
            if (col != null) DestroyImmediate(col);   // never touch the CharacterController or raycasts
            go.transform.SetParent(pivot, false);
            go.transform.localPosition = new Vector3(0f, yOffset, 0f);
            go.transform.localScale = scale;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.On;
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

        // ------------------------------------------------------------------ animation

        void LateUpdate()
        {
            if (!built || bodyRoot == null) return;
            if (customModelPrefab != null) return;   // custom model: yaw-follow only (via parenting)

            float dt = Time.deltaTime;
            float standH = fpc != null ? fpc.standHeight : 1.8f;
            float k = cc != null ? Mathf.Clamp(cc.height / Mathf.Max(0.01f, standH), 0.45f, 1f) : 1f;
            ApplyCrouch(k);

            // Leg / arm swing from horizontal speed.
            Vector3 v = fpc != null ? fpc.Velocity : (cc != null ? cc.velocity : Vector3.zero);
            v.y = 0f;
            float speed = v.magnitude;
            bool grounded = fpc == null || fpc.IsGrounded;
            float walk = fpc != null ? Mathf.Max(0.1f, fpc.walkSpeed) : 5f;
            float amount = grounded && !(fpc != null && fpc.IsSliding) ? Mathf.Clamp01(speed / walk) : 0f;
            if (speed > 0.05f) phase += speed / Mathf.Max(0.1f, strideLength) * Mathf.PI * 2f * dt;
            else phase = Mathf.MoveTowards(phase, Mathf.Round(phase / Mathf.PI) * Mathf.PI, dt * 6f);

            float s = Mathf.Sin(phase) * amount;
            legL.localRotation = Quaternion.Euler(s * maxLegSwingDeg, 0f, 0f);
            legR.localRotation = Quaternion.Euler(-s * maxLegSwingDeg, 0f, 0f);
            armL.localRotation = Quaternion.Euler(-s * maxArmSwingDeg, 0f, 0f);
            armR.localRotation = Quaternion.Euler(s * maxArmSwingDeg, 0f, 0f);

            // Head follows the camera pivot, relative to the body, within limits.
            if (cameraPivot != null)
            {
                Quaternion rel = Quaternion.Inverse(bodyRoot.rotation) * cameraPivot.rotation;
                Vector3 e = rel.eulerAngles;
                float pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, e.x), headPitchMin, headPitchMax);
                float yaw = Mathf.Clamp(Mathf.DeltaAngle(0f, e.y), -headYawLimit, headYawLimit);
                head.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            }
        }

        /// <summary>Squash legs and torso by <paramref name="k"/> (1 = standing) and re-seat shoulders and head.</summary>
        void ApplyCrouch(float k)
        {
            if (hips == null) return;
            float scale = bodyHeight / (LegLen + TorsoLen + HeadDia);
            float legLen = LegLen * scale * k, torsoLen = TorsoLen * scale * k;

            hips.localPosition = new Vector3(0f, legLen, 0f);
            legL.localScale = legR.localScale = new Vector3(1f, k, 1f);
            torso.localScale = new Vector3(1f, k, 1f);

            float neckY = legLen + torsoLen;
            float shoulderY = neckY - 0.07f * scale;
            armL.localPosition = new Vector3(-0.25f, shoulderY, 0f);
            armR.localPosition = new Vector3(0.25f, shoulderY, 0f);
            head.localPosition = new Vector3(0f, neckY, 0f);
        }

        void OnDestroy()
        {
            DestroyMat(gownMat);
            DestroyMat(legMat);
            DestroyMat(skinMat);
            if (bodyRoot != null)
            {
                if (Application.isPlaying) Destroy(bodyRoot.gameObject); else DestroyImmediate(bodyRoot.gameObject);
            }
        }

        static void DestroyMat(Material m)
        {
            if (m == null) return;
            if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
        }
    }
}
