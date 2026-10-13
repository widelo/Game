// ======================================================================================================
// PLACEHOLDER ENEMY AI - Esteban, this file is yours to replace.
//
// How to swap in your AI:
//   1. Write a class implementing Game.Enemy.IEnemyBrain (two methods: Initialise(EnemyContext), Tick(dt)).
//   2. Change the one line in EnemySpawner.CreateBrain() (`return new StalkerBrain();`) to return yours
//      (it receives the enemy index, so different enemies can get different brains).
//   3. Delete this file once nothing references it. EnemyAgent / EnemySpawner / EnemyHud do not depend on it.
//
// What EnemyContext gives you (all valid from Initialise until the next level rebuild):
//   Layout  - LevelLayout: Rooms (Center, SizeX/SizeZ, Role), Neighbours(id), BfsDistances(id), SpawnRoom, KeyRooms
//   Builder - LevelBuilder: RoomWorldCenter(id), PlayerSpawnPoint, RoomObjects, LevelRoot
//   Player  - player root Transform (FirstPersonController on it: Velocity, IsGrounded, IsSprinting...)
//   Agent   - NavMeshAgent already on the baked NavMesh; you own speed/destination/etc.
//   Self    - enemy root Transform
//   OnEvent - call OnEvent("caught") when you catch the player (host teleports them and calls Initialise
//             again after a cooldown); call OnEvent("state:Name") on state changes so the HUD shows it.
// Rules the host relies on: Initialise must fully reset you (it is re-called after each catch), and make
// "caught" the last thing you do in a Tick.
// ======================================================================================================
using Game.Player;
using LevelGen.Core;
using UnityEngine;

namespace Game.Enemy
{
    /// <summary>Patrol -> Investigate (heard sprinting) -> Chase (seen) -> Attack (in reach). No coroutines.</summary>
    public sealed class StalkerBrain : IEnemyBrain
    {
        public enum State { Patrol, Investigate, Chase, Attack }

        public float HearRange = 18f, SprintSpeed = 5.5f, SightRange = 14f, AttackRange = 1.6f;
        public float EyeHeight = 2.2f, LoseSightSeconds = 4f;
        public float PatrolSpeed = 2.2f, InvestigateSpeed = 3.2f, ChaseSpeed = 4.5f;

        const float RepathInterval = 0.2f;     // chase re-targets at 5 Hz, not every frame
        const float LegTimeout = 30f;          // give up on a patrol leg that never arrives
        const float DefaultCameraHeight = 1.6f;

        public State Current { get; private set; }

        EnemyContext _ctx;
        FirstPersonController _fpc;
        Vector3 _lastKnown;
        float _sinceSeen, _repathTimer, _lingerTimer, _legTimer;
        bool _lingering;
        int _patrolRoom = -1;

        public void Initialise(EnemyContext ctx)
        {
            _ctx = ctx;
            _fpc = ctx.Player != null ? ctx.Player.GetComponent<FirstPersonController>() : null;
            var a = ctx.Agent;
            a.angularSpeed = 240f;
            a.acceleration = 12f;
            a.stoppingDistance = 1.2f;
            _sinceSeen = LoseSightSeconds;
            _patrolRoom = -1;
            Enter(State.Patrol);
        }

        public void Tick(float dt)
        {
            if (_ctx == null || _ctx.Player == null || !_ctx.Agent.isOnNavMesh) return;

            Vector3 self = _ctx.Self.position, player = _ctx.Player.position;
            float dist = Vector3.Distance(self, player);
            bool sees = dist <= SightRange && HasLineOfSight();
            bool hears = dist <= HearRange && IsSprinting();

            if (sees) { _lastKnown = player; _sinceSeen = 0f; }
            else _sinceSeen += dt;

            if (sees && dist <= AttackRange)
            {
                if (Current != State.Attack) { Enter(State.Attack); _ctx.OnEvent?.Invoke("caught"); }
                return; // "caught" is always the last thing in a Tick
            }

            switch (Current)
            {
                case State.Attack:      // player slipped out of reach
                    Enter(State.Chase);
                    break;

                case State.Chase:
                    if (_sinceSeen >= LoseSightSeconds) { Enter(State.Investigate); break; }
                    _repathTimer -= dt;
                    if (_repathTimer <= 0f) { _repathTimer = RepathInterval; _ctx.Agent.SetDestination(_lastKnown); }
                    break;

                case State.Investigate:
                    if (sees) { Enter(State.Chase); break; }
                    if (hears && (player - _lastKnown).sqrMagnitude > 1f)
                    { _lastKnown = player; _ctx.Agent.SetDestination(_lastKnown); _lingering = false; }
                    if (TickArriveAndLinger(dt)) Enter(State.Patrol);
                    break;

                case State.Patrol:
                    if (sees) { Enter(State.Chase); break; }
                    if (hears) { _lastKnown = player; Enter(State.Investigate); break; }
                    _legTimer += dt;
                    if (TickArriveAndLinger(dt) || _legTimer > LegTimeout) StartPatrolLeg();
                    break;
            }
        }

        void Enter(State s)
        {
            Current = s;
            var a = _ctx.Agent;
            _lingering = false;
            switch (s)
            {
                case State.Patrol:      a.speed = PatrolSpeed; StartPatrolLeg(); break;
                case State.Investigate: a.speed = InvestigateSpeed; a.SetDestination(_lastKnown); break;
                case State.Chase:       a.speed = ChaseSpeed; _repathTimer = 0f; break;
                case State.Attack:      a.ResetPath(); break;
            }
            _ctx.OnEvent?.Invoke(StateEvents[(int)s]);
        }

        static readonly string[] StateEvents = { "state:Patrol", "state:Investigate", "state:Chase", "state:Attack" };

        /// <summary>True once the agent has reached its destination and waited 1-3 s there.</summary>
        bool TickArriveAndLinger(float dt)
        {
            var a = _ctx.Agent;
            if (!_lingering)
            {
                if (a.pathPending || a.remainingDistance > a.stoppingDistance + 0.25f) return false;
                _lingering = true;
                _lingerTimer = Random.Range(1f, 3f);
            }
            _lingerTimer -= dt;
            return _lingerTimer <= 0f;
        }

        /// <summary>Random room, weighted 1/(1+d)^2 by corridor distance from the player's room. Never the current one.</summary>
        void StartPatrolLeg()
        {
            _lingering = false;
            _legTimer = 0f;
            var rooms = _ctx.Layout.Rooms;
            if (rooms.Count == 0) return;

            int[] d = _ctx.Layout.BfsDistances(NearestRoom(_ctx.Player.position)); // once per leg, not per frame
            float total = 0f;
            for (int i = 0; i < rooms.Count; i++) total += Weight(i, d);

            int pick = _patrolRoom;
            if (total > 0f)
            {
                float r = Random.value * total;
                for (int i = 0; i < rooms.Count; i++)
                {
                    float w = Weight(i, d);
                    if (w <= 0f) continue;
                    pick = i;
                    if ((r -= w) <= 0f) break;
                }
            }
            if (pick < 0) return;
            _patrolRoom = pick;
            _ctx.Agent.SetDestination(_ctx.Builder.RoomWorldCenter(pick));
        }

        float Weight(int room, int[] d)
        {
            if (room == _patrolRoom || d[room] < 0) return 0f;
            float w = 1f / (1f + d[room]);
            return w * w;
        }

        int NearestRoom(Vector3 p)
        {
            int best = 0;
            float bestSq = float.MaxValue;
            var rooms = _ctx.Layout.Rooms;
            for (int i = 0; i < rooms.Count; i++)
            {
                float dx = (float)rooms[i].Center.X - p.x, dz = (float)rooms[i].Center.Z - p.z;
                float sq = dx * dx + dz * dz;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            return best;
        }

        bool HasLineOfSight()
        {
            Vector3 eye = _ctx.Self.position + Vector3.up * EyeHeight;
            Vector3 target = _fpc != null && _fpc.cameraPivot != null
                ? _fpc.cameraPivot.position
                : _ctx.Player.position + Vector3.up * DefaultCameraHeight;
            // Our own capsule is ignored because the eye starts inside it; anything else in the way blocks.
            if (!Physics.Linecast(eye, target, out RaycastHit hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return true;
            return hit.transform == _ctx.Player || hit.transform.IsChildOf(_ctx.Player);
        }

        bool IsSprinting()
        {
            if (_fpc == null) return false;
            Vector3 v = _fpc.Velocity;
            v.y = 0f;
            return v.sqrMagnitude > SprintSpeed * SprintSpeed;
        }
    }
}
