// Objective feedback: key chime on pickup, a clank at the gate when the last key unlocks it, a rising sting
// on escape, and an impact + heartbeat when an enemy catches the player.
using CS462.StalkerAI;
using Game.Enemy;
using Game.Objectives;
using UnityEngine;

namespace Game.Audio
{
    [AddComponentMenu("")]
    public sealed class ObjectiveSounds : MonoBehaviour
    {
        public float chimeVolume = 0.6f;
        public float gateVolume = 1f;
        public float escapeVolume = 0.7f;
        public float caughtVolume = 0.9f;

        AudioDirector _director;
        ObjectiveTracker _tracker;
        float _lastCaught = float.NegativeInfinity;

        void Awake() { _director = GetComponent<AudioDirector>(); }

        void OnEnable()
        {
            if (_director == null) return;
            _director.LevelReady += Resubscribe;
            _director.EnemyDiscovered += HookEnemy;
        }

        void OnDisable()
        {
            if (_director != null)
            {
                _director.LevelReady -= Resubscribe;
                _director.EnemyDiscovered -= HookEnemy;
            }
            Unsubscribe();
        }

        void Start() => Resubscribe();

        /// <summary>Idempotent: safe on every OnLevelBuilt.</summary>
        void Resubscribe()
        {
            Unsubscribe();
            _tracker = FindAnyObjectByType<ObjectiveTracker>();
            if (_tracker == null) return;
            _tracker.OnKeyCollected += HandleKey;
            _tracker.OnEscaped += HandleEscaped;
        }

        void Unsubscribe()
        {
            if (_tracker == null) return;
            _tracker.OnKeyCollected -= HandleKey;
            _tracker.OnEscaped -= HandleEscaped;
            _tracker = null;
        }

        void HandleKey(int collected, int required)
        {
            _director.Play2D(ProceduralClips.KeyChime(), chimeVolume, AudioBus.Sfx);
            if (collected < required || _tracker == null || _tracker.Gate == null) return;
            _director.Play3D(ProceduralClips.GateUnlock(), _tracker.Gate.transform.position + Vector3.up * 1.5f,
                             gateVolume, AudioBus.Sfx);
        }

        void HandleEscaped(int floor) => _director.Play2D(ProceduralClips.EscapeSting(), escapeVolume, AudioBus.Sfx);

        void HookEnemy(Component enemy)
        {
            if (enemy is EnemyAgent agent) agent.OnEnemyEvent += HandleAgentEvent;
            else if (enemy is StalkerBoss boss) boss.onAttackHit.AddListener(PlayCaught);
        }

        void HandleAgentEvent(EnemyAgent agent, string e)
        {
            if (e == "caught") PlayCaught();
        }

        void PlayCaught()
        {
            if (this == null || Time.time - _lastCaught < 1f) return;
            _lastCaught = Time.time;
            _director.Play2D(ProceduralClips.Caught(), caughtVolume, AudioBus.Sfx);
        }
    }
}
