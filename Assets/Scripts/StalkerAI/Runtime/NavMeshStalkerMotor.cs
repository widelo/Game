using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace CS462.StalkerAI
{
    /// <summary>
    /// <see cref="IStalkerMotor"/> over the NavMesh that LevelBuilder bakes at runtime. The agent moves the root
    /// and handles wall collision against the baked mesh; facing stays manual (updateRotation = false) so the FSM
    /// turns the boss exactly as it does with the A* motor.
    /// </summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NavMeshAgent))]
    public sealed class NavMeshStalkerMotor : MonoBehaviour, IStalkerMotor
    {
        /// <summary>How far SetGoal / recovery may search for the nearest NavMesh point.</summary>
        public const float SnapDistance = 2f;
        const float ArriveSlack = 0.1f, GoalResendDistance = 0.25f, CornerRefreshInterval = 0.2f;

        public NavMeshAgent Agent { get; private set; }
        public bool Arrived { get; private set; }
        public bool HasGoal => hasGoal;
        public bool Failed { get; private set; }
        public string FailureReason { get; private set; }
        public IReadOnlyList<Vector3> Path { get { RefreshCorners(); return corners; } }

        readonly List<Vector3> corners = new List<Vector3>();
        StalkerSettings settings;
        Vector3 goal;
        float cornersAt = float.NegativeInfinity;
        bool hasGoal;

        /// <summary>Agent tuning for the stalker. Call before the agent is used (the director does this at spawn).</summary>
        public static void Configure(NavMeshAgent agent, float radius, float height)
        {
            agent.radius = radius;
            agent.height = height;
            agent.baseOffset = 0f;
            agent.acceleration = 14f;
            agent.angularSpeed = 0f;          // facing is manual
            agent.updateRotation = false;
            agent.stoppingDistance = 1.4f;
            agent.autoBraking = true;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance; // one enemy
        }

        public void Initialise(StalkerSettings behavior)
        {
            settings = behavior;
            Agent = GetComponent<NavMeshAgent>();
            Agent.updateRotation = false;
            Stop();
            EnsureOnMesh();
        }

        bool EnsureOnMesh()
        {
            if (Agent == null || !Agent.enabled) return false;
            if (Agent.isOnNavMesh) return true;
            if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, SnapDistance, NavMesh.AllAreas))
                Agent.Warp(hit.position);
            if (Agent.isOnNavMesh) return true;
            Fail("Agent is off the NavMesh and no mesh point is within " + SnapDistance + " m");
            return false;
        }

        void Fail(string reason) { Failed = true; Arrived = false; FailureReason = reason; }

        public void SetGoal(Vector3 point)
        {
            if (hasGoal && !Failed && (point - goal).sqrMagnitude < GoalResendDistance * GoalResendDistance) return;
            goal = point; hasGoal = true; Arrived = false; Failed = false;
            if (!EnsureOnMesh()) return;
            if (!NavMesh.SamplePosition(point, out NavMeshHit hit, SnapDistance, NavMesh.AllAreas))
            { Fail("Goal has no NavMesh point within " + SnapDistance + " m"); return; }
            if (!Agent.SetDestination(hit.position)) Fail("NavMeshAgent rejected the destination");
        }

        public void Stop()
        {
            hasGoal = false; Arrived = false; Failed = false; corners.Clear();
            if (Agent != null && Agent.enabled && Agent.isOnNavMesh) Agent.ResetPath();
        }

        public void Tick(float dt, float speed, bool paused)
        {
            if (Agent == null || !Agent.enabled) return;
            if (!Agent.isOnNavMesh && !EnsureOnMesh()) return;
            Agent.speed = Mathf.Max(0f, speed);
            Agent.isStopped = paused || !hasGoal;
            if (!hasGoal || Failed) return;
            if (!Agent.pathPending)
            {
                if (Agent.pathStatus == NavMeshPathStatus.PathInvalid) { Fail("NavMesh path invalid"); return; }
                float remaining = Agent.hasPath ? Agent.remainingDistance : FlatDistance(transform.position, Agent.destination);
                Arrived = remaining <= Agent.stoppingDistance + ArriveSlack;
            }
            Vector3 heading = Agent.desiredVelocity;
            if (!paused && heading.sqrMagnitude > 0.01f) Face(heading, dt);
        }

        public void Face(Vector3 direction, float dt)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) return;
            float turn = settings != null ? settings.turnSpeed : 200f;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), turn * dt);
        }

        void RefreshCorners()
        {
            if (Time.time - cornersAt < CornerRefreshInterval) return;
            cornersAt = Time.time; corners.Clear();
            if (Agent != null && Agent.enabled && Agent.isOnNavMesh && Agent.hasPath && hasGoal)
                corners.AddRange(Agent.path.corners);
        }

        public void DrawGizmos()
        {
            RefreshCorners();
            Gizmos.color = Color.cyan;
            Vector3 previous = transform.position + Vector3.up * 0.15f;
            foreach (Vector3 corner in corners)
            {
                Vector3 next = corner + Vector3.up * 0.15f;
                Gizmos.DrawLine(previous, next); Gizmos.DrawSphere(next, 0.07f); previous = next;
            }
        }

        static float FlatDistance(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }
    }
}
