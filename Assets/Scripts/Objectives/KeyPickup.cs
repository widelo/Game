// One collectable key. Collection is a DISTANCE check against the player transform in Update, not a physics
// trigger callback: a CharacterController only raises OnTriggerEnter when it MOVES into the trigger, so a
// teleported or stationary player (tests, the respawn path) would never collect. The trigger SphereCollider
// is still added so the pickup is visible to physics queries and the radius reads in the Scene view.
using System.Collections;
using UnityEngine;

namespace Game.Objectives
{
    public sealed class KeyPickup : MonoBehaviour
    {
        /// <summary>Layout room id this key belongs to (-1 when spawned outside a layout).</summary>
        public int RoomId { get; private set; } = -1;
        public bool Collected { get; private set; }

        ObjectiveTracker _tracker;
        Transform _player;
        float _radius;
        Transform _visual;

        const float SpinDegreesPerSecond = 60f;
        const float PopSeconds = 0.25f;

        public void Initialise(ObjectiveTracker tracker, Transform player, int roomId, float radius, Transform visual)
        {
            _tracker = tracker;
            _player = player;
            RoomId = roomId;
            _radius = radius;
            _visual = visual;

            var col = GetComponent<SphereCollider>();
            if (col == null) col = gameObject.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = radius;
        }

        void Update()
        {
            if (_visual != null && !Collected) _visual.Rotate(0f, SpinDegreesPerSecond * Time.deltaTime, 0f, Space.World);
            if (Collected || _tracker == null) return;

            if (_player == null) _player = _tracker.player;
            if (_player == null) return;

            // Player root sits at the feet; the key floats at 0.9 m. Compare horizontally and allow a generous
            // vertical band so a jumping or freshly dropped player still collects.
            Vector3 d = _player.position - transform.position;
            float dy = Mathf.Abs(d.y);
            d.y = 0f;
            if (d.sqrMagnitude <= _radius * _radius && dy <= 2.5f) Collect();
        }

        /// <summary>Marks the key collected, tells the tracker, then pops and destroys itself.</summary>
        public void Collect()
        {
            if (Collected) return;
            Collected = true;
            if (_tracker != null) _tracker.NotifyKeyCollected(this);
            if (Application.isPlaying && isActiveAndEnabled) StartCoroutine(PopAndDie());
            else DestroyImmediate(gameObject);
        }

        IEnumerator PopAndDie()
        {
            Transform t = _visual != null ? _visual : transform;
            Vector3 start = t.localScale;
            float t0 = Time.time;
            while (Time.time - t0 < PopSeconds)
            {
                float k = (Time.time - t0) / PopSeconds;
                // Swell to 1.6x for the first 40%, then collapse to nothing.
                float s = k < 0.4f ? Mathf.Lerp(1f, 1.6f, k / 0.4f) : Mathf.Lerp(1.6f, 0f, (k - 0.4f) / 0.6f);
                t.localScale = start * s;
                yield return null;
            }
            Destroy(gameObject);
        }
    }
}
