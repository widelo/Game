// Host MonoBehaviour for one enemy: owns the NavMeshAgent and the brain, ticks the brain, and handles the
// "caught" consequence (player back to spawn, enemy back to its spawn room). Brain-agnostic.
using System;
using LevelGen.Core;
using LevelGen.Unity;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Enemy
{
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class EnemyAgent : MonoBehaviour
    {
        [Tooltip("Seconds the enemy stands still after a catch before its brain resumes.")]
        [SerializeField] float caughtCooldown = 3f;

        [Tooltip("Height above PlayerSpawnPoint the caught player is dropped at (matches LevelDebugController).")]
        [SerializeField] float playerRespawnHeight = 1.1f;

        public int CaughtCount { get; private set; }
        /// <summary>The brain's last reported state, or "Recovering" during the post-catch cooldown.</summary>
        public string StateName => OnCooldown ? "Recovering" : _brainState;
        /// <summary>Time.time of the last catch; -infinity if never caught.</summary>
        public float LastCaughtTime { get; private set; } = float.NegativeInfinity;
        public bool OnCooldown => Time.time < _cooldownUntil;
        public IEnemyBrain Brain => _brain;
        public NavMeshAgent Agent { get; private set; }
        public Vector3 SpawnPosition { get; private set; }

        /// <summary>Every brain event (including "caught" and "state:*"), for audio/UI hooks.</summary>
        public event Action<EnemyAgent, string> OnEnemyEvent;

        IEnemyBrain _brain;
        EnemyContext _ctx;
        float _cooldownUntil;
        string _brainState = "Idle";

        /// <summary>Called once by the spawner after the agent has been warped onto the NavMesh.</summary>
        public void Initialise(IEnemyBrain brain, LevelLayout layout, LevelBuilder builder, Transform player,
                               Vector3 spawnPosition)
        {
            Agent = GetComponent<NavMeshAgent>();
            SpawnPosition = spawnPosition;
            _brain = brain;
            _ctx = new EnemyContext
            {
                Layout = layout, Builder = builder, Player = player,
                Agent = Agent, Self = transform, OnEvent = HandleEvent,
            };
            _brain.Initialise(_ctx);
        }

        void Update()
        {
            if (_brain == null || OnCooldown) return;
            _brain.Tick(Time.deltaTime);
        }

        void HandleEvent(string e)
        {
            if (e == "caught")
            {
                if (OnCooldown) return;
                Caught();
            }
            else if (e.StartsWith("state:", StringComparison.Ordinal))
            {
                _brainState = e.Substring(6); // allocates on state change only
            }
            OnEnemyEvent?.Invoke(this, e);
        }

        void Caught()
        {
            CaughtCount++;
            LastCaughtTime = Time.time;
            _cooldownUntil = Time.time + caughtCooldown;

            Transform player = _ctx.Player;
            if (player != null && _ctx.Builder != null)
            {
                // Same pattern as LevelDebugController: an enabled CharacterController fights a raw position write.
                var cc = player.GetComponent<CharacterController>();
                bool wasEnabled = cc != null && cc.enabled;
                if (cc != null) cc.enabled = false;
                player.position = _ctx.Builder.PlayerSpawnPoint + Vector3.up * playerRespawnHeight;
                if (cc != null) cc.enabled = wasEnabled;
            }

            if (Agent.isOnNavMesh) Agent.ResetPath();
            Agent.Warp(SpawnPosition);
            _brain.Initialise(_ctx); // full reset; the brain resumes once the cooldown expires
        }
    }
}
