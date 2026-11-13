// Footsteps on the player: a step every stride (1.6 m walking, 2.0 m sprinting, 0.9 m crouched) while
// grounded and moving faster than 0.5 m/s. Louder with speed; pitch jitters +/- 6%. Surface is tile unless
// the floor's material name says carpet/rug/fabric.
using Game.Player;
using UnityEngine;

namespace Game.Audio
{
    [AddComponentMenu("")]
    [RequireComponent(typeof(FirstPersonController))]
    public sealed class PlayerFootsteps : MonoBehaviour
    {
        public float walkStride = 1.6f;
        public float sprintStride = 2.0f;
        public float crouchStride = 0.9f;
        public float minSpeed = 0.5f;
        public float pitchJitter = 0.06f;

        FirstPersonController _fpc;
        AudioSource _source;
        float _stride;
        bool _wasGrounded = true;

        void Awake()
        {
            _fpc = GetComponent<FirstPersonController>();
            var go = new GameObject("Footsteps");
            go.transform.SetParent(transform, false);
            _source = go.AddComponent<AudioSource>();
            AudioDirector.Configure2D(_source);
        }

        void Update()
        {
            Vector3 v = _fpc.Velocity;
            float speed = new Vector2(v.x, v.z).magnitude;
            bool grounded = _fpc.IsGrounded;

            if (grounded && !_wasGrounded && v.y < -3f) Step(Mathf.Max(speed, 6f)); // landing
            _wasGrounded = grounded;

            if (!grounded || _fpc.IsSliding || speed < minSpeed) { _stride = Mathf.Min(_stride, 0.5f); return; }
            _stride += speed * Time.deltaTime;
            float length = _fpc.IsCrouching ? crouchStride : _fpc.IsSprinting ? sprintStride : walkStride;
            if (_stride < length) return;
            _stride -= length;
            Step(speed);
        }

        void Step(float speed)
        {
            AudioDirector d = AudioDirector.Instance;
            float bus = d != null ? d.BusVolume(AudioBus.Sfx) : 1f;
            float vol = Mathf.Lerp(0.2f, 0.75f, Mathf.InverseLerp(1f, 8f, speed));
            if (_fpc.IsCrouching) vol *= 0.6f;
            _source.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            _source.PlayOneShot(ProceduralClips.Footstep(!SoftFloor()), vol * bus);
        }

        bool SoftFloor()
        {
            if (!Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 3f,
                                 ~0, QueryTriggerInteraction.Ignore)) return false;
            var r = hit.collider.GetComponent<Renderer>();
            if (r == null || r.sharedMaterial == null) return false;
            string n = r.sharedMaterial.name.ToLowerInvariant();
            return n.Contains("carpet") || n.Contains("rug") || n.Contains("fabric");
        }
    }
}
