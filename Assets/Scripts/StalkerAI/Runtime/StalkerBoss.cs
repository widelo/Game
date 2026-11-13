using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace CS462.StalkerAI
{
    public enum StalkerState { Patrol, Investigate, Stalk, Chase, Disengage }

    /// <summary>Stalker FSM. Needs an <see cref="IStalkerMotor"/> (AStarMotor or NavMeshStalkerMotor) on the same object.</summary>
    [DisallowMultipleComponent]
    public sealed class StalkerBoss : MonoBehaviour
    {
        public StalkerSettings settings = new StalkerSettings();
        public UnityEvent onAttackWindup = new UnityEvent();
        public UnityEvent onAttackHit = new UnityEvent();
        public UnityEvent onAttackMiss = new UnityEvent();
        public event Action<StalkerBoss, StalkerState> StateChanged;

        public StalkerState State { get; private set; }
        public StalkerPerception Senses { get; private set; }
        public IStalkerMotor Motor { get; private set; }
        /// <summary>True when the motor is the NavMesh one: goals snap to the NavMesh and the A* grid is not needed.</summary>
        public bool UsesNavMesh => navMesh;
        public bool WindingUp => windingUp;
        public string LastAttack { get; private set; } = "none";
        public float StateAge => Time.time - enteredAt;

        Transform player;
        StalkerTarget target;
        AStarNavigation world;
        IReadOnlyList<Vector3> patrolPoints;
        System.Random random;
        int component, patrolIndex = -1;
        float enteredAt, decisionAt, pauseUntil, legStartedAt, searchUntil, nextSearchPoint;
        float chaseAt, nextChaseAt, nextAttackAt, windupEnds, recoveryEnds;
        float lastInvestigationObservation;
        Vector3 retreatFrom, attackDirection;
        bool ready, windingUp, searchingArea, navMesh;

        public void Initialise(AStarNavigation navigation, Transform playerRoot, StalkerTarget damageTarget,
                               IReadOnlyList<Vector3> patrol, StalkerSettings behavior, int seed)
        {
            world = navigation; player = playerRoot; target = damageTarget; patrolPoints = patrol;
            settings = behavior; settings.Validate();
            settings.eyeHeight = Mathf.Min(settings.eyeHeight, world.Settings.bodyHeight - 0.1f);
            random = new System.Random(seed);
            Motor = GetComponent<IStalkerMotor>();
            if (Motor == null) { UnityEngine.Debug.LogError("[Stalker] No IStalkerMotor (AStarMotor or NavMeshStalkerMotor) on the stalker.", this); return; }
            navMesh = Motor is NavMeshStalkerMotor;
            if (Motor is AStarMotor gridMotor) gridMotor.Initialise(world, settings);
            else if (Motor is NavMeshStalkerMotor meshMotor) meshMotor.Initialise(settings);
            Senses = new StalkerPerception(transform, player, world, settings, random);
            if (navMesh)
            {
                // The A* grid is never built on this path; `world` is only used for line-of-sight raycasts.
                component = -1;
                if (!NavMesh.SamplePosition(transform.position, out _, NavMeshStalkerMotor.SnapDistance, NavMesh.AllAreas))
                { UnityEngine.Debug.LogError("[Stalker] Spawn is not on the NavMesh.", this); return; }
            }
            else
            {
                if (!world.TryNode(transform.position, world.Settings.endpointSnapDistance, out int spawn))
                { UnityEngine.Debug.LogError("[Stalker] Spawn has no valid navigation cell.", this); return; }
                component = world.Components[spawn];
            }
            ready = true; Enter(StalkerState.Patrol, true);
        }

        void OnEnable() { NoiseBus.Heard += OnNoise; }
        void OnDisable()
        {
            NoiseBus.Heard -= OnNoise;
            windingUp = false;
            if (Motor != null) Motor.Stop();
        }

        void OnNoise(NoiseSignal signal)
        {
            if (ready && State != StalkerState.Disengage) Senses.Hear(signal);
        }

        void Update()
        {
            if (!ready || world == null || (!navMesh && !world.Ready)) return;
            float dt = Time.deltaTime;
            if (target != null && !target.Alive && State != StalkerState.Disengage)
                ForceDisengage();
            if (State != StalkerState.Disengage) Senses.Sample();

            // The chase cap also cancels a windup/recovery; attacks cannot extend pursuit indefinitely.
            if (State == StalkerState.Chase && (StateAge >= settings.chaseDuration || !Senses.HasMemory))
                Enter(StalkerState.Disengage);

            if (TickAttack(dt)) return; // TickAttack drives the motor itself (lunge or recovery hold).
            switch (State)
            {
                case StalkerState.Patrol: TickPatrol(dt); break;
                case StalkerState.Investigate: TickInvestigate(dt); break;
                case StalkerState.Stalk: TickStalk(dt); break;
                case StalkerState.Chase: TickChase(); break;
                case StalkerState.Disengage: TickDisengage(); break;
            }
            Motor.Tick(dt, SpeedForState(), Time.time < pauseUntil || windingUp);
        }

        float SpeedForState()
        {
            switch (State)
            {
                case StalkerState.Investigate: return settings.investigateSpeed;
                case StalkerState.Stalk: return settings.stalkSpeed;
                case StalkerState.Chase: return settings.chaseSpeed;
                case StalkerState.Disengage: return settings.retreatSpeed;
                default: return settings.patrolSpeed;
            }
        }

        void TickPatrol(float dt)
        {
            if (Senses.HasMemory) { Enter(StalkerState.Investigate); return; }
            if (Time.time < pauseUntil) { Scan(dt); return; }
            if (!Motor.HasGoal || Motor.Arrived || Motor.Failed || Time.time - legStartedAt > settings.patrolLegTimeout)
            {
                if (Motor.HasGoal) { Motor.Stop(); pauseUntil = Time.time + Range(settings.patrolPause); return; }
                PickPatrolGoal();
            }
            if (Time.time >= decisionAt)
            {
                decisionAt = Time.time + settings.patrolDecisionInterval;
                if (random.NextDouble() < settings.pauseWhileWalkingChance)
                    pauseUntil = Time.time + Range(settings.patrolPause);
            }
        }

        void PickPatrolGoal()
        {
            legStartedAt = Time.time;
            if (patrolPoints == null || patrolPoints.Count == 0) { pauseUntil = Time.time + 1f; return; }
            int start = random.Next(patrolPoints.Count);
            for (int offset = 0; offset < patrolPoints.Count; offset++)
            {
                int pick = (start + offset) % patrolPoints.Count;
                if (pick == patrolIndex && patrolPoints.Count > 1) continue;
                if (!TrySnap(patrolPoints[pick], world.Settings.endpointSnapDistance, out Vector3 goal)) continue;
                patrolIndex = pick; Motor.SetGoal(goal); return;
            }
            pauseUntil = Time.time + Range(settings.patrolPause);
        }

        void TickInvestigate(float dt)
        {
            if (Senses.Visible && StateAge >= settings.detectionDelay) { Enter(StalkerState.Stalk); return; }
            if (StateAge >= settings.investigationDuration || !Senses.HasMemory)
            { Enter(StalkerState.Disengage); return; }
            if (Senses.ObservedTime > lastInvestigationObservation && !Senses.Visible)
            {
                lastInvestigationObservation = Senses.ObservedTime;
                searchingArea = false; Motor.SetGoal(Senses.LastKnown);
            }
            if (!searchingArea && (Motor.Arrived || Motor.Failed))
            {
                searchingArea = true; searchUntil = Time.time + settings.searchAtPointDuration;
                nextSearchPoint = Time.time + 1f; Motor.Stop();
            }
            if (!searchingArea) return;
            Scan(dt);
            if (Time.time >= searchUntil) { Enter(StalkerState.Disengage); return; }
            if (Time.time >= nextSearchPoint)
            {
                nextSearchPoint = Time.time + 1f;
                Vector3 p = Senses.LastKnown + Direction(Range(0f, 360f)) * Range(0.5f, settings.searchRadius);
                if (TrySnap(p, world.Settings.endpointSnapDistance, out Vector3 goal)) Motor.SetGoal(goal);
            }
        }

        void TickStalk(float dt)
        {
            if (!Senses.HasMemory) { Enter(StalkerState.Disengage); return; }
            if (!Senses.Visible && Time.time - Senses.LastSeenTime >= settings.loseSightDelay)
            { Enter(StalkerState.Investigate); return; }
            float distance = FlatDistance(transform.position, Senses.LastKnown);
            if (Senses.Visible && Time.time >= nextChaseAt
                && (distance <= settings.closeAggroDistance || Time.time >= chaseAt))
            { Enter(StalkerState.Chase); return; }
            if (Time.time < pauseUntil) { Motor.Face(Senses.LastKnown - transform.position, dt); return; }
            if (Time.time < decisionAt) return;
            decisionAt = Time.time + settings.repositionInterval;
            if (distance >= settings.preferredStalkDistance - settings.stalkDistanceTolerance
                && random.NextDouble() < settings.watchChance)
            { pauseUntil = Time.time + Range(settings.watchDuration); return; }
            if (TryStalkPosition(out Vector3 position)) Motor.SetGoal(position);
            else Motor.Stop(); // Trapped or no ring point: watching is fairer than charging a hidden player.
        }

        bool TryStalkPosition(out Vector3 best)
        {
            best = default; float bestScore = float.NegativeInfinity;
            float offset = Range(0f, 360f);
            Vector3 rememberedEye = Senses.LastKnown + Vector3.up * 1.3f;
            for (int i = 0; i < settings.positionCandidates; i++)
            {
                float distance = Mathf.Max(1f, settings.preferredStalkDistance + Range(-settings.stalkDistanceTolerance, settings.stalkDistanceTolerance));
                Vector3 wish = Senses.LastKnown + Direction(offset + i * 360f / settings.positionCandidates) * distance;
                if (!TrySnap(wish, world.Settings.cellSize, out Vector3 point)) continue;
                bool headVisible = world.ClearSight(point + Vector3.up * settings.eyeHeight, rememberedEye);
                bool bodyVisible = world.ClearSight(point + Vector3.up * 0.9f, rememberedEye);
                float cover = !bodyVisible && headVisible ? settings.concealmentPreference
                            : !headVisible ? settings.concealmentPreference * 0.5f : 0f;
                float score = cover - Mathf.Abs(FlatDistance(point, Senses.LastKnown) - settings.preferredStalkDistance)
                            - FlatDistance(point, transform.position) * 0.025f + Range(0f, 0.4f);
                if (score > bestScore) { bestScore = score; best = point; }
            }
            return !float.IsNegativeInfinity(bestScore);
        }

        void TickChase()
        {
            if (StateAge >= settings.chaseDuration || !Senses.HasMemory)
            { Enter(StalkerState.Disengage); return; }
            if (!Senses.Visible && Time.time - Senses.LastSeenTime >= settings.loseSightDelay)
            { Enter(StalkerState.Investigate); return; }
            if (Time.time >= nextAttackAt && CanHitNow(false))
            {
                // No Motor.Stop(): the windup is a lunge that keeps the chase momentum (see TickAttack).
                attackDirection = player.position - transform.position; attackDirection.y = 0f;
                if (attackDirection.sqrMagnitude < 0.001f) attackDirection = transform.forward;
                attackDirection.Normalize();
                transform.rotation = Quaternion.LookRotation(attackDirection);
                windingUp = true; windupEnds = Time.time + settings.attackWindup;
                LastAttack = "WINDUP"; onAttackWindup.Invoke(); return;
            }
            Motor.SetGoal(Senses.LastKnown);
        }

        bool TickAttack(float dt)
        {
            if (State != StalkerState.Chase) return false;
            if (windingUp)
            {
                if (Time.time < windupEnds)
                {
                    // Windup is a dash at the player's live position, not a stand-still.
                    if (settings.attackLungeSpeed > 0f && player != null)
                    { Motor.SetGoal(player.position); Motor.Tick(dt, settings.attackLungeSpeed, false); }
                    else Motor.Tick(dt, 0f, true);
                    return true;
                }
                windingUp = false; nextAttackAt = Time.time + settings.attackCooldown;
                recoveryEnds = Time.time + settings.attackRecovery;
                // The strike lands where the boss is facing after the lunge (the motor turned it while moving).
                attackDirection = transform.forward; attackDirection.y = 0f;
                if (attackDirection.sqrMagnitude < 0.0001f) attackDirection = Vector3.forward;
                attackDirection.Normalize();
                bool hit = CanHitNow(true) && target != null && target.TakeDamage(settings.attackDamage);
                LastAttack = hit ? "HIT" : "MISS";
                if (hit) onAttackHit.Invoke(); else onAttackMiss.Invoke();
                Motor.Tick(dt, 0f, true);
                return true;
            }
            if (Time.time < recoveryEnds) { Motor.Tick(dt, 0f, true); return true; }
            return false;
        }

        /// <param name="release">False: may a windup start (needs sight + attackRange).
        /// True: does the strike connect at the end of the windup.</param>
        bool CanHitNow(bool release)
        {
            if (player == null || target == null || !target.Alive || target.Protected) return false;
            Vector3 delta = player.position - transform.position;
            if (Mathf.Abs(delta.y) > settings.attackVerticalTolerance) return false;
            delta.y = 0f;
            float distanceSquared = delta.sqrMagnitude;
            // Point blank: no FOV/eye-line requirement, only that no wall separates the two bodies at chest height.
            if (release && distanceSquared <= settings.pointBlankRange * settings.pointBlankRange)
            {
                Vector3 chest = transform.position + Vector3.up * 1f;
                return world.ClearSight(chest, new Vector3(player.position.x, chest.y, player.position.z));
            }
            if (!Senses.CanSeePlayerNow()) return false;
            float reach = settings.attackRange + (release ? settings.attackReleaseReach : 0f);
            if (distanceSquared > reach * reach) return false;
            return !release || distanceSquared < 0.0001f
                || Vector3.Dot(attackDirection, delta.normalized) >= Mathf.Cos(settings.attackHalfAngle * Mathf.Deg2Rad);
        }

        void TickDisengage()
        {
            if (StateAge < settings.disengagementCooldown) return;
            Senses.Forget(); Enter(StalkerState.Patrol);
        }

        void PickRetreatGoal()
        {
            Vector3 away = transform.position - retreatFrom; away.y = 0f;
            if (away.sqrMagnitude < 0.001f) away = -transform.forward;
            float baseAngle = Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg;
            float score = float.NegativeInfinity; Vector3 best = default;
            for (int i = 0; i < settings.positionCandidates; i++)
            {
                float angle = baseAngle + Range(-105f, 105f);
                Vector3 wish = transform.position + Direction(angle) * Range(settings.retreatDistance * 0.4f, settings.retreatDistance);
                if (!TrySnap(wish, world.Settings.cellSize, out Vector3 p)) continue;
                float value = FlatDistance(p, retreatFrom) - FlatDistance(p, transform.position) * 0.2f;
                if (!world.ClearSight(p + Vector3.up * settings.eyeHeight, retreatFrom + Vector3.up)) value += 3f;
                if (value > score) { score = value; best = p; }
            }
            if (!float.IsNegativeInfinity(score)) Motor.SetGoal(best);
        }

        public void ForceDisengage()
        {
            if (ready) Enter(StalkerState.Disengage, true);
        }

        void Enter(StalkerState next, bool force = false)
        {
            if (!force && State == next) return;
            if (State == StalkerState.Chase) nextChaseAt = Time.time + settings.chaseCooldown;
            State = next; enteredAt = Time.time; pauseUntil = 0f; decisionAt = Time.time;
            if (windingUp) LastAttack = "CANCELLED";
            windingUp = false; recoveryEnds = 0f; searchingArea = false; Motor.Stop();
            switch (next)
            {
                case StalkerState.Patrol:
                    decisionAt = Time.time + settings.patrolDecisionInterval; PickPatrolGoal(); break;
                case StalkerState.Investigate:
                    lastInvestigationObservation = Senses.ObservedTime;
                    if (Senses.HasMemory) Motor.SetGoal(Senses.LastKnown); break;
                case StalkerState.Stalk:
                    chaseAt = Time.time + Range(settings.stalkBeforeChase); break;
                case StalkerState.Chase:
                    nextAttackAt = Mathf.Max(nextAttackAt, Time.time + settings.detectionDelay);
                    Motor.SetGoal(Senses.LastKnown); break;
                case StalkerState.Disengage:
                    retreatFrom = Senses.HasMemory ? Senses.LastKnown : transform.position + transform.forward;
                    nextChaseAt = Mathf.Max(nextChaseAt, Time.time + settings.chaseCooldown);
                    PickRetreatGoal(); break;
            }
            if (settings.logStateChanges) UnityEngine.Debug.Log($"[Stalker] {name}: {next}", this);
            StateChanged?.Invoke(this, next);
        }

        /// <summary>Snap a wished-for point to somewhere the active motor can stand.</summary>
        bool TrySnap(Vector3 point, float radius, out Vector3 snapped)
        {
            snapped = default;
            if (navMesh)
            {
                // The level is flat (floors at NavigationSettings.floorY); project first so eye/root heights don't count.
                if (!NavMesh.SamplePosition(world.OnFloor(point), out NavMeshHit hit, Mathf.Max(radius, 0.5f), NavMesh.AllAreas)) return false;
                snapped = hit.position; return true;
            }
            if (!world.TryNode(point, radius, out int node, component)) return false;
            snapped = world.World(node); return true;
        }

        void Scan(float dt) { transform.Rotate(Vector3.up, settings.turnSpeed * 0.18f * dt, Space.World); }
        float Range(float minimum, float maximum) => Mathf.Lerp(minimum, maximum, (float)random.NextDouble());
        float Range(Vector2 range) => Range(range.x, range.y);
        static Vector3 Direction(float degrees) => Quaternion.Euler(0f, degrees, 0f) * Vector3.forward;
        static float FlatDistance(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }

        void OnDrawGizmosSelected()
        {
            if (!settings.drawGizmos) return;
            Vector3 eye = transform.position + Vector3.up * settings.eyeHeight;
            Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(eye, settings.sightDistance);
            Gizmos.DrawRay(eye, Quaternion.AngleAxis(-settings.fieldOfView * 0.5f, Vector3.up) * transform.forward * settings.sightDistance);
            Gizmos.DrawRay(eye, Quaternion.AngleAxis(settings.fieldOfView * 0.5f, Vector3.up) * transform.forward * settings.sightDistance);
            Gizmos.color = Color.red; Gizmos.DrawWireSphere(transform.position, settings.attackRange);
            if (Senses != null && Senses.HasMemory)
            {
                Gizmos.color = Color.magenta; Gizmos.DrawSphere(Senses.LastKnown, 0.25f);
                Gizmos.DrawLine(eye, Senses.LastKnown);
                Gizmos.color = Color.blue; Gizmos.DrawWireSphere(Senses.LastKnown, settings.preferredStalkDistance);
            }
            if (Motor != null) Motor.DrawGizmos();
        }
    }
}
