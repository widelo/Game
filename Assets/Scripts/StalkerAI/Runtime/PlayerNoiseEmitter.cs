using Game.Player;
using UnityEngine;

namespace CS462.StalkerAI
{
    [DisallowMultipleComponent, RequireComponent(typeof(FirstPersonController))]
    public sealed class PlayerNoiseEmitter : MonoBehaviour
    {
        [Min(0f)] public float walkRadius = 4f;
        [Min(0f)] public float sprintRadius = 14f;
        [Min(0f)] public float crouchRadius = 1.5f;
        [Min(0f)] public float slideRadius = 18f;
        [Min(0.1f)] public float stepInterval = 0.55f;
        FirstPersonController player;
        float nextStep;

        void Awake() { player = GetComponent<FirstPersonController>(); }
        void Update()
        {
            if (player == null || !player.enabled || !player.IsGrounded || Time.time < nextStep) return;
            Vector3 velocity = player.Velocity; velocity.y = 0f;
            if (velocity.sqrMagnitude < 0.25f) return;
            nextStep = Time.time + Mathf.Max(0.1f, stepInterval);
            float radius = player.IsSliding ? slideRadius : player.IsCrouching ? crouchRadius
                         : player.IsSprinting ? sprintRadius : walkRadius;
            NoiseBus.Emit(transform.position, radius, transform);
        }
    }
}
