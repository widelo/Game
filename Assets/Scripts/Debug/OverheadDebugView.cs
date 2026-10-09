// Overhead debug view: Tab toggles an orthographic top-down camera that looks straight through the ceilings,
// with fog off and ambient lifted so the whole generated layout reads at a glance. The player is frozen
// while overhead. WASD pans, scroll wheel zooms, Home refits to the level. Everything is restored on exit
// and the view re-fits itself whenever the level is rebuilt (R/N/P still work while overhead).
// NOTE: this namespace is "Debug", so UnityEngine.Debug must be fully qualified.
using System.Collections.Generic;
using Game.Player;
using LevelGen.Core;
using LevelGen.Unity;
using UnityEngine;

namespace Game.Debug
{
    public sealed class OverheadDebugView : MonoBehaviour
    {
        public LevelBuilder builder;
        public FirstPersonController player;
        public Camera playerCamera;

        public KeyCode toggleKey = KeyCode.Tab;
        [Tooltip("Metres of padding around the level bounds when fitting the view.")]
        public float fitPadding = 6f;
        public float panSpeed = 40f;
        public float zoomStep = 0.1f;
        public Color overheadAmbient = new Color(0.55f, 0.55f, 0.6f, 1f);

        public bool IsActive { get; private set; }

        Camera _cam;
        readonly List<Renderer> _hiddenCeilings = new List<Renderer>();
        bool _fogWas; Color _ambientWas; UnityEngine.Rendering.AmbientMode _ambientModeWas;
        bool _playerWasEnabled; bool _playerCamWasEnabled;

        void Awake() { EnsureCamera(); }

        /// <summary>Lazy so the view also works from edit-mode callers (HeadlessVerify, inspector buttons) where Awake never ran.</summary>
        void EnsureCamera()
        {
            if (_cam != null) return;
            var existing = transform.Find("OverheadCamera");
            var go = existing != null ? existing.gameObject : new GameObject("OverheadCamera");
            go.transform.SetParent(transform, false);
            go.hideFlags = HideFlags.DontSave;
            _cam = go.GetComponent<Camera>();
            if (_cam == null) _cam = go.AddComponent<Camera>(); // explicit: ?? ignores Unity's fake-null objects
            _cam.orthographic = true;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 400f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.05f, 0.05f, 0.06f);
            _cam.renderingPath = RenderingPath.DeferredShading;
            _cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _cam.enabled = false;
        }

        void OnEnable() { if (builder != null) builder.OnLevelBuilt += OnLevelBuilt; }
        void OnDisable() { if (builder != null) builder.OnLevelBuilt -= OnLevelBuilt; if (IsActive) Exit(); }

        void OnLevelBuilt(LevelLayout layout)
        {
            if (!IsActive) return;
            // The old ceilings were destroyed with the level; hide the new ones and refit.
            _hiddenCeilings.Clear();
            HideCeilings();
            Fit(layout);
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) { if (IsActive) Exit(); else Enter(); }
            if (!IsActive) return;

            var pan = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
            _cam.transform.position += pan * (panSpeed * Time.unscaledDeltaTime * (_cam.orthographicSize / 40f));

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f)
                _cam.orthographicSize = Mathf.Clamp(_cam.orthographicSize * (1f - scroll * zoomStep * 10f), 5f, 300f);

            if (Input.GetKeyDown(KeyCode.Home) && builder != null && builder.CurrentLayout != null) Fit(builder.CurrentLayout);
        }

        public void Enter()
        {
            if (IsActive) return;
            EnsureCamera();
            IsActive = true;

            _fogWas = RenderSettings.fog;
            _ambientWas = RenderSettings.ambientLight;
            _ambientModeWas = RenderSettings.ambientMode;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = overheadAmbient;

            HideCeilings();

            if (player != null) { _playerWasEnabled = player.enabled; player.enabled = false; player.SetLocked(false); }
            if (playerCamera != null) { _playerCamWasEnabled = playerCamera.enabled; playerCamera.enabled = false; }

            if (builder != null && builder.CurrentLayout != null) Fit(builder.CurrentLayout);
            _cam.enabled = true;
        }

        public void Exit()
        {
            if (!IsActive) return;
            IsActive = false;

            _cam.enabled = false;
            RenderSettings.fog = _fogWas;
            RenderSettings.ambientMode = _ambientModeWas;
            RenderSettings.ambientLight = _ambientWas;

            foreach (var r in _hiddenCeilings) if (r != null) r.enabled = true;
            _hiddenCeilings.Clear();

            if (playerCamera != null) playerCamera.enabled = _playerCamWasEnabled;
            if (player != null) { player.enabled = _playerWasEnabled; player.SetLocked(true); }
        }

        void HideCeilings()
        {
            if (builder == null) return;
            foreach (var r in builder.GetComponentsInChildren<Renderer>())
            {
                if (!r.gameObject.name.StartsWith("Ceiling")) continue;
                if (!r.enabled) continue;
                r.enabled = false;
                _hiddenCeilings.Add(r);
            }
        }

        /// <summary>Centre the camera over the level and size it so every room fits, honouring the screen aspect.</summary>
        public void Fit(LevelLayout layout)
        {
            EnsureCamera();
            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var room in layout.Rooms)
            {
                minX = System.Math.Min(minX, room.Center.X - room.HalfX);
                maxX = System.Math.Max(maxX, room.Center.X + room.HalfX);
                minZ = System.Math.Min(minZ, room.Center.Z - room.HalfZ);
                maxZ = System.Math.Max(maxZ, room.Center.Z + room.HalfZ);
            }
            if (layout.Rooms.Count == 0) return;

            float cx = (float)(minX + maxX) * 0.5f, cz = (float)(minZ + maxZ) * 0.5f;
            float halfW = (float)(maxX - minX) * 0.5f + fitPadding;
            float halfH = (float)(maxZ - minZ) * 0.5f + fitPadding;
            float aspect = _cam.aspect > 0.01f ? _cam.aspect : 16f / 9f;
            _cam.orthographicSize = Mathf.Max(halfH, halfW / aspect);

            float ceiling = (float)layout.Settings.CeilingHeight;
            _cam.transform.position = new Vector3(cx, ceiling + 50f, cz);
        }
    }
}
