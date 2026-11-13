// Mood for the placeholder NavMeshAgent enemy (EnemyAgent + StalkerBrain), from EnemyAgent.StateName.
using UnityEngine;

namespace Game.Enemy
{
    public sealed class PlaceholderMoodProvider : MonsterMoodProvider
    {
        EnemyAgent _agent;

        protected override void Awake()
        {
            base.Awake();
            _agent = GetComponent<EnemyAgent>();
        }

        protected override Mood Evaluate()
        {
            if (_agent == null) return Mood.Idle;
            switch (_agent.StateName) // string switch: compares, never allocates
            {
                case "Patrol": return Mood.Prowl;
                case "Investigate": return Mood.Alert;
                case "Chase": return Mood.Hunt;
                case "Attack": return Mood.Lunge;
                case "Recovering": return Mood.Idle;
                default: return Mood.Idle;
            }
        }
    }
}
