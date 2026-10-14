using System.Collections.Generic;
using CS462.StalkerAI.Core;
using UnityEngine;

namespace CS462.StalkerAI
{
    [DisallowMultipleComponent, RequireComponent(typeof(CharacterController))]
    public sealed class AStarMotor : MonoBehaviour
    {
        public AStarNavigation Navigation { get; private set; }
        public bool Arrived { get; private set; }
        public bool Failed { get; private set; }
        public string FailureReason { get; private set; }
        public bool HasGoal => hasGoal;
        public bool Searching => searching;
        public IReadOnlyList<Vector3> Path => path;
        public int Expanded => search.Expanded;
        public double GraphPathCost => search.PathCost;

        readonly AStarSearch search = new AStarSearch();
        readonly List<Vector3> path = new List<Vector3>();
        CharacterController controller;
        StalkerSettings settings;
        Vector3 desiredGoal, plannedGoal, endpoint;
        int waypoint;
        float nextRepath, nextSteer, currentSpeed, verticalSpeed, stuckTime;
        bool hasGoal, searching, needsPlan;

        public void Initialise(AStarNavigation navigation, StalkerSettings behavior)
        {
            Navigation = navigation; settings = behavior;
            controller = GetComponent<CharacterController>();
            controller.radius = navigation.Settings.bodyRadius;
            controller.height = navigation.Settings.bodyHeight;
            controller.center = Vector3.up * (controller.height * 0.5f);
            controller.skinWidth = 0.025f; controller.stepOffset = 0.05f;
            controller.slopeLimit = 1f; controller.minMoveDistance = 0f;
            Stop();
        }

        public void SetGoal(Vector3 point)
        {
            point = Navigation.OnFloor(point);
            if (!hasGoal || (point - desiredGoal).sqrMagnitude > Navigation.Settings.cellSize * Navigation.Settings.cellSize * 0.25f)
            { desiredGoal = point; needsPlan = true; Arrived = false; Failed = false; }
            hasGoal = true;
        }

        public void Stop()
        {
            hasGoal = false; searching = false; path.Clear(); waypoint = 0;
            currentSpeed = 0f; stuckTime = 0f; needsPlan = false; Arrived = false; Failed = false;
        }

        void Fail(string reason)
        {
            path.Clear(); waypoint = 0; searching = false; currentSpeed = 0f;
            Failed = true; FailureReason = reason; needsPlan = true;
            nextRepath = Time.time + Navigation.Settings.repathInterval;
        }

        void BeginPlan()
        {
            nextRepath = Time.time + Navigation.Settings.repathInterval;
            float snap = Navigation.Settings.endpointSnapDistance;
            if (!Navigation.TryNode(transform.position, snap, out int start)
                || !Navigation.TryNode(desiredGoal, snap, out int goal, Navigation.Components[start]))
            { Fail("No connected, physically reachable endpoint"); return; }
            plannedGoal = desiredGoal; endpoint = Navigation.World(goal);
            search.Begin(Navigation.Graph, start, goal,
                         Navigation.Settings.checkLiveObstacles ? Navigation.LiveEdgeClear : (System.Func<int, int, bool>)null);
            searching = true; needsPlan = false; Failed = false;
            // Keep an existing valid path while a moving target's next route is computed.
        }

        void FinishPlan()
        {
            searching = false;
            if (search.Status != SearchStatus.Complete) { Fail(search.Status.ToString()); return; }
            path.Clear(); waypoint = 0;
            foreach (int node in search.Path) path.Add(Navigation.World(node));
            // Rejoin near the start if the body moved during this incremental search; never teleport.
            int join = -1;
            for (int i = Mathf.Min(path.Count - 1, 8); i >= 0; i--)
            {
                if ((path[i] - Navigation.OnFloor(transform.position)).sqrMagnitude > 9f) continue;
                if (Navigation.CanTraverse(transform.position, path[i])) { join = i; break; }
            }
            if (join < 0) { Fail("Cannot safely join completed route"); return; }
            waypoint = join;
            needsPlan = (plannedGoal - desiredGoal).sqrMagnitude > Navigation.Settings.cellSize * Navigation.Settings.cellSize * 0.25f;
        }

        /// <summary>Called by the FSM, once a frame; no separate Update ordering dependency.</summary>
        public void Tick(float dt, float speed, bool paused)
        {
            if (Navigation == null || !Navigation.Ready || controller == null || !controller.enabled) return;
            if (hasGoal && !paused)
            {
                if (!searching && needsPlan && Time.time >= nextRepath) BeginPlan();
                if (searching && search.Step(Navigation.Settings.searchExpansionsPerFrame) != SearchStatus.Searching) FinishPlan();
            }
            Vector3 horizontal = Vector3.zero;
            if (hasGoal && !paused && waypoint < path.Count)
            {
                Vector3 feet = Navigation.OnFloor(transform.position);
                while (waypoint < path.Count && (path[waypoint] - feet).sqrMagnitude < 0.0016f) waypoint++;
                if (waypoint < path.Count)
                {
                    // Short, collision-checked lookahead smooths the grid stair steps. No unsafe splines.
                    if (Time.time >= nextSteer)
                    {
                        nextSteer = Time.time + 0.15f;
                        for (int i = Mathf.Min(path.Count - 1, waypoint + 4); i > waypoint; i--)
                        {
                            if (!Navigation.CanTraverse(feet, path[i])) continue;
                            waypoint = i; break;
                        }
                    }
                    Vector3 delta = path[waypoint] - feet;
                    currentSpeed = Mathf.MoveTowards(currentSpeed, speed, settings.acceleration * dt);
                    horizontal = delta.normalized * Mathf.Min(delta.magnitude, currentSpeed * dt);
                    if (!Navigation.CanTraverse(feet, feet + horizontal))
                    { horizontal = Vector3.zero; Fail("Route obstructed; waiting to replan"); }
                    else Face(delta, dt);
                }
                else
                {
                    Arrived = (feet - endpoint).sqrMagnitude <= 0.01f;
                    currentSpeed = 0f;
                }
            }
            else currentSpeed = 0f;

            verticalSpeed = controller.isGrounded ? -2f : Mathf.Max(-30f, verticalSpeed + settings.gravity * dt);
            Vector3 before = transform.position;
            controller.Move(horizontal + Vector3.up * (verticalSpeed * dt));
            float moved = (Navigation.OnFloor(transform.position) - Navigation.OnFloor(before)).magnitude;
            if (horizontal.magnitude > 0.001f && moved < horizontal.magnitude * 0.15f) stuckTime += dt;
            else stuckTime = 0f;
            if (stuckTime > 0.6f) { stuckTime = 0; Fail("Body blocked by another collider"); }
        }

        public void Face(Vector3 direction, float dt)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), settings.turnSpeed * dt);
        }

        public void DrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Vector3 previous = transform.position + Vector3.up * 0.15f;
            for (int i = waypoint; i < path.Count; i++)
            {
                Vector3 next = path[i] + Vector3.up * 0.15f;
                Gizmos.DrawLine(previous, next); Gizmos.DrawSphere(next, 0.07f); previous = next;
            }
        }
    }
}
