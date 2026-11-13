// Base for the components that translate an AI's state into a MonsterAnimator Mood. Providers only READ the
// AI (never write to it), so they can be bolted onto a teammate's enemy without touching its code.
using UnityEngine;

namespace Game.Enemy
{
    [RequireComponent(typeof(MonsterAnimator))]
    public abstract class MonsterMoodProvider : MonoBehaviour
    {
        protected MonsterAnimator Animator { get; private set; }

        protected virtual void Awake() => Animator = GetComponent<MonsterAnimator>();

        // Update runs before MonsterAnimator.LateUpdate, so the animator always sees this frame's mood.
        void Update()
        {
            if (Animator != null) Animator.Mood = Evaluate();
        }

        /// <summary>This frame's mood. Called once per frame; must not allocate.</summary>
        protected abstract Mood Evaluate();

        /// <summary>Horizontal distance from this enemy to the player, or +infinity when there is no player.</summary>
        protected float PlayerDistance()
        {
            var player = MonsterAnimator.Player;
            if (player == null) return float.PositiveInfinity;
            Vector3 d = player.transform.position - transform.position; d.y = 0f;
            return d.magnitude;
        }
    }
}
