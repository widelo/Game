using System;
using UnityEngine;
using UnityEngine.Events;

namespace CS462.StalkerAI
{
    /// <summary>Optional sample health adapter; the supplied project had no health/damage contract.</summary>
    [DisallowMultipleComponent]
    public sealed class StalkerTarget : MonoBehaviour
    {
        [Min(1f)] public float maxHealth = 100f;
        [Min(0f)] public float hitInvulnerability = 1f;
        public UnityEvent onHit = new UnityEvent();
        public UnityEvent onDefeated = new UnityEvent();
        public event Action<StalkerTarget> Defeated;
        public float Health { get; private set; }
        public bool Alive => Health > 0f;
        public bool Protected => Time.time < protectedUntil;
        float protectedUntil;

        void Awake() { ResetHealth(0f); }

        public void ResetHealth(float protectionSeconds)
        { Health = Mathf.Max(1f, maxHealth); protectedUntil = Time.time + Mathf.Max(0f, protectionSeconds); }

        public bool TakeDamage(float damage)
        {
            if (!Alive || Protected || damage <= 0f || float.IsNaN(damage)) return false;
            Health = Mathf.Max(0f, Health - damage);
            protectedUntil = Time.time + Mathf.Max(0f, hitInvulnerability);
            onHit.Invoke();
            if (!Alive) { onDefeated.Invoke(); Defeated?.Invoke(this); }
            return true;
        }
    }
}
