// Mood for the A* stalker boss (CS462.StalkerAI.StalkerBoss), read-only: the boss's AI keeps every decision.
//   Patrol -> Prowl, Investigate -> Alert, Chase -> Hunt (Lunge while winding up an attack, or when the player
//   is within lungeDistance right after the chase starts), Disengage -> Idle.
//   Stalk -> Alert, except the GDD weeping-angel beat: while the player's camera looks within freezeAngle of
//   the boss from farther than freezeMinDistance, it goes Frozen for at most maxFreezeSeconds, then back to
//   Alert until the player looks away (which re-arms the freeze). Purely visual.
using CS462.StalkerAI;
using UnityEngine;

namespace Game.Enemy
{
    public sealed class StalkerMoodProvider : MonsterMoodProvider
    {
        public float freezeAngle = 12f;
        public float freezeMinDistance = 4f;
        public float maxFreezeSeconds = 1.5f;
        public float lungeDistance = 2.2f;
        [Tooltip("A Chase younger than this counts as 'just started' for the proximity lunge.")]
        public float lungeStateAge = 1.5f;
        [Tooltip("Height on the boss the freeze test aims at (chest).")]
        public float lookTargetHeight = 1.6f;

        StalkerBoss _boss;
        Camera _camera;
        bool _freezeArmed = true;
        float _freezeStart = -1f;

        protected override void Awake()
        {
            base.Awake();
            _boss = GetComponent<StalkerBoss>();
        }

        protected override Mood Evaluate()
        {
            if (_boss == null) return Mood.Idle;
            if (_boss.WindingUp) return Mood.Lunge;
            StalkerState state = _boss.State;
            if (state != StalkerState.Stalk) { _freezeArmed = true; _freezeStart = -1f; }
            switch (state)
            {
                case StalkerState.Patrol: return Mood.Prowl;
                case StalkerState.Investigate: return Mood.Alert;
                case StalkerState.Stalk: return StalkMood();
                case StalkerState.Chase:
                    return PlayerDistance() < lungeDistance && _boss.StateAge < lungeStateAge ? Mood.Lunge : Mood.Hunt;
                default: return Mood.Idle; // Disengage
            }
        }

        Mood StalkMood()
        {
            if (!PlayerLooking())
            {
                _freezeArmed = true;
                _freezeStart = -1f;
                return Mood.Alert;
            }
            if (!_freezeArmed) return Mood.Alert;
            if (_freezeStart < 0f) _freezeStart = Time.time;
            if (Time.time - _freezeStart <= maxFreezeSeconds) return Mood.Frozen;
            _freezeArmed = false; // held long enough; wait for the player to look away
            return Mood.Alert;
        }

        bool PlayerLooking()
        {
            Camera cam = PlayerCamera();
            if (cam == null) return false;
            Vector3 target = transform.position + Vector3.up * lookTargetHeight;
            Vector3 to = target - cam.transform.position;
            float dist = to.magnitude;
            if (dist <= freezeMinDistance) return false;
            return Vector3.Angle(cam.transform.forward, to) <= freezeAngle;
        }

        Camera PlayerCamera()
        {
            if (_camera != null && _camera.isActiveAndEnabled) return _camera;
            var player = MonsterAnimator.Player;
            _camera = player != null ? player.GetComponentInChildren<Camera>() : null;
            if (_camera == null) _camera = Camera.main;
            return _camera;
        }
    }
}
