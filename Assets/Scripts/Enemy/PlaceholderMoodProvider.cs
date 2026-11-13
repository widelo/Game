// Mood for the placeholder NavMeshAgent enemy (EnemyAgent + StalkerBrain), from EnemyAgent.StateName.
// Entering "Attack" also fires MonsterAnimator.TriggerLunge() so the strike lines up with the attack.
using UnityEngine;

namespace Game.Enemy
{
    public sealed class PlaceholderMoodProvider : MonsterMoodProvider
    {
        EnemyAgent _agent;
        bool _wasAttacking;

        protected override void Awake()
        {
            base.Awake();
            _agent = GetComponent<EnemyAgent>();
        }

        protected override Mood Evaluate()
        {
            if (_agent == null) return Mood.Idle;
            bool attacking = _agent.StateName == "Attack";
            if (attacking && !_wasAttacking) Animator.TriggerLunge();
            _wasAttacking = attacking;
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
