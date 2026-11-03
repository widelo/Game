// The locked exit: a solid panel across the exit doorway carrying a padlock, plus an escape volume just
// inside the exit room. The panel's collider is what keeps the player out until every key is collected;
// unlocking drops the padlock, disables that collider and turns the panel translucent green. Walking into
// the escape volume with the gate open is the win condition.
//
// Like KeyPickup, the escape volume is checked by position in Update rather than by OnTriggerEnter, because
// a CharacterController that is teleported (or standing still) never raises trigger callbacks. The child
// "EscapeTrigger" BoxCollider exists so the volume is visible in the Scene view and to physics queries.
using System.Collections;
using UnityEngine;

namespace Game.Objectives
{
    public sealed class ExitGate : MonoBehaviour
    {
        public const float PanelWidth = 3.2f;
        public const float PanelHeight = 3.0f;
        public const float PanelThickness = 0.2f;
        public const float PadlockHeight = 0.5f;
        public const float PadlockCentreY = 1.3f;

        /// <summary>True once every key is in; the panel no longer blocks.</summary>
        public bool Unlocked { get; private set; }

        /// <summary>False for the "return to spawn" fallback, where a solid panel would trap the player at the start.</summary>
        public bool Blocks { get; private set; }

        /// <summary>World centre of the escape volume (inside the room). Tests teleport here.</summary>
        public Vector3 TriggerCentre => _trigger != null ? _trigger.transform.position : transform.position;

        public GameObject Panel => _panel;
        public GameObject Padlock => _padlock;

        ObjectiveTracker _tracker;
        GameObject _panel;
        BoxCollider _panelCollider;
        Renderer _panelRenderer;
        GameObject _padlock;
        BoxCollider _trigger;
        Material _openMaterial;

        /// <summary>
        /// Builds the panel, padlock and escape volume. The gate's own transform must already be placed at the
        /// panel position with +Z pointing INTO the corridor (away from the room).
        /// </summary>
        public void Build(ObjectiveTracker tracker, GameObject padlockPrefab, Material lockedMaterial,
                          Material openMaterial, Material fallbackPadlockMaterial, float triggerDistanceIntoRoom,
                          float triggerDepth, bool blocks)
        {
            _tracker = tracker;
            _openMaterial = openMaterial;
            Blocks = blocks;

            // ---- panel: a thin box across the doorway, with a solid collider when it blocks ----
            _panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _panel.name = "GatePanel";
            _panel.transform.SetParent(transform, false);
            _panel.transform.localPosition = new Vector3(0f, PanelHeight * 0.5f, 0f);
            _panel.transform.localRotation = Quaternion.identity;
            _panel.transform.localScale = new Vector3(PanelWidth, PanelHeight, PanelThickness);
            _panelRenderer = _panel.GetComponent<Renderer>();
            _panelRenderer.sharedMaterial = lockedMaterial;
            _panelCollider = _panel.GetComponent<BoxCollider>();
            _panelCollider.enabled = blocks;

            // ---- padlock: on the corridor-facing (+Z) side, 0.5 m tall, centred at 1.3 m ----
            Vector3 lockPos = new Vector3(0f, PadlockCentreY - PadlockHeight * 0.5f, PanelThickness * 0.5f + 0.06f);
            if (padlockPrefab != null)
            {
                _padlock = Instantiate(padlockPrefab, transform);
                _padlock.name = "Padlock";
                _padlock.transform.localPosition = lockPos;
                _padlock.transform.localRotation = Quaternion.identity;
                // Grounded = the model's lowest point lands on the anchor, so the centre ends up at 1.3 m.
                LevelGen.Unity.ModelFit.FitToHeight(_padlock, PadlockHeight, groundToFloor: true, centreXZ: true);
                // Push it clear of the panel face along +Z by half its fitted depth.
                Bounds lb = LevelGen.Unity.ModelFit.LocalBounds(_padlock);
                float depth = lb.size.z * _padlock.transform.localScale.z;
                _padlock.transform.localPosition += new Vector3(0f, 0f, depth * 0.5f);
            }
            else
            {
                _padlock = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _padlock.name = "Padlock";
                _padlock.transform.SetParent(transform, false);
                _padlock.transform.localPosition = new Vector3(0f, PadlockCentreY, PanelThickness * 0.5f + 0.15f);
                _padlock.transform.localScale = new Vector3(0.35f, PadlockHeight, 0.2f);
                _padlock.GetComponent<Renderer>().sharedMaterial = fallbackPadlockMaterial;
            }
            // The padlock is decoration: never let it collide with the player or the panel.
            foreach (var c in _padlock.GetComponentsInChildren<Collider>(true)) c.enabled = false;

            // ---- escape volume: just inside the room (-Z is into the room) ----
            var trig = new GameObject("EscapeTrigger");
            trig.transform.SetParent(transform, false);
            trig.transform.localPosition = new Vector3(0f, PanelHeight * 0.5f, -triggerDistanceIntoRoom);
            _trigger = trig.AddComponent<BoxCollider>();
            _trigger.isTrigger = true;
            _trigger.size = new Vector3(PanelWidth, PanelHeight, triggerDepth);
        }

        /// <summary>Open the gate: padlock drops away, the panel stops blocking and goes translucent green.</summary>
        public void Unlock()
        {
            if (Unlocked) return;
            Unlocked = true;
            if (_panelCollider != null) _panelCollider.enabled = false;
            if (_panelRenderer != null && _openMaterial != null) _panelRenderer.sharedMaterial = _openMaterial;

            if (_padlock != null)
            {
                if (Application.isPlaying && isActiveAndEnabled) StartCoroutine(DropPadlock(_padlock));
                else DestroyImmediate(_padlock);
                _padlock = null;
            }
        }

        IEnumerator DropPadlock(GameObject padlock)
        {
            Transform t = padlock.transform;
            Vector3 start = t.localPosition;
            Vector3 scale = t.localScale;
            const float seconds = 0.6f;
            float t0 = Time.time;
            while (padlock != null && Time.time - t0 < seconds)
            {
                float k = (Time.time - t0) / seconds;
                t.localPosition = start + Vector3.down * (k * k * 1.2f);
                t.localScale = scale * (1f - k);
                yield return null;
            }
            if (padlock != null) Destroy(padlock);
        }

        /// <summary>Is <paramref name="worldPos"/> (the player's feet) inside the escape volume?</summary>
        public bool InEscapeVolume(Vector3 worldPos)
        {
            if (_trigger == null) return false;
            Vector3 local = _trigger.transform.InverseTransformPoint(worldPos);
            Vector3 half = _trigger.size * 0.5f;
            // Vertical band is generous: the root is at the feet (y ~ 0) once grounded, ~1.1 m right after a teleport.
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.z) <= half.z &&
                   local.y >= -half.y - 1f && local.y <= half.y + 1f;
        }

        void Update()
        {
            if (!Unlocked || _tracker == null || _tracker.Escaped) return;
            Transform p = _tracker.player;
            if (p != null && InEscapeVolume(p.position)) _tracker.NotifyEscaped(this);
        }
    }
}
