using System.Collections.Generic;
using UnityEngine;

namespace CS462.StalkerAI
{
    /// <summary>
    /// Locomotion seam for <see cref="StalkerBoss"/>. The FSM only asks for goals, speed and facing;
    /// <see cref="AStarMotor"/> (grid + CharacterController) and <see cref="NavMeshStalkerMotor"/>
    /// (baked NavMesh + NavMeshAgent) both implement it. Tick is called by the FSM once a frame.
    /// </summary>
    public interface IStalkerMotor
    {
        void SetGoal(Vector3 point);
        void Stop();
        void Tick(float dt, float speed, bool paused);
        void Face(Vector3 direction, float dt);
        bool Arrived { get; }
        bool HasGoal { get; }
        bool Failed { get; }
        string FailureReason { get; }
        IReadOnlyList<Vector3> Path { get; }
        void DrawGizmos();
    }
}
