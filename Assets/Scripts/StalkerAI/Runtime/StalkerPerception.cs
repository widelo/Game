using System;
using Game.Player;
using UnityEngine;

namespace CS462.StalkerAI
{
    /// <summary>The only class that reads the live player pose for vision. The FSM receives observations.</summary>
    public sealed class StalkerPerception
    {
        readonly Transform self, player;
        readonly FirstPersonController controller;
        readonly AStarNavigation world;
        readonly StalkerSettings settings;
        readonly System.Random random;
        float nextSense, nextHearing;
        bool hasObservation;

        public bool Visible { get; private set; }
        public Vector3 LastKnown { get; private set; }
        public float LastSeenTime { get; private set; } = float.NegativeInfinity;
        public float ObservedTime { get; private set; } = float.NegativeInfinity;
        public bool HasMemory => hasObservation && Time.time - ObservedTime <= settings.memoryDuration;
        public Vector3 Eye => self.position + Vector3.up * settings.eyeHeight;

        public StalkerPerception(Transform self, Transform player, AStarNavigation world, StalkerSettings settings, System.Random random)
        {
            this.self = self; this.player = player; this.world = world; this.settings = settings; this.random = random;
            controller = player != null ? player.GetComponent<FirstPersonController>() : null;
        }

        public void Sample()
        {
            if (Time.time < nextSense) return;
            nextSense = Time.time + settings.senseInterval;
            Visible = CanSeePlayerNow();
            if (!Visible) return;
            LastKnown = player.position;
            ObservedTime = LastSeenTime = Time.time; hasObservation = true;
        }

        public bool CanSeePlayerNow()
        {
            if (player == null || !player.gameObject.activeInHierarchy) return false;
            Vector3 aim = controller != null && controller.cameraPivot != null
                ? controller.cameraPivot.position : player.position + Vector3.up * 1.5f;
            Vector3 delta = aim - Eye;
            if (delta.sqrMagnitude > settings.sightDistance * settings.sightDistance) return false;
            Vector3 flat = delta; flat.y = 0f;
            if (flat.sqrMagnitude > 0.0001f && Vector3.Dot(self.forward, flat.normalized)
                < Mathf.Cos(settings.fieldOfView * 0.5f * Mathf.Deg2Rad)) return false;
            return world.ClearSight(Eye, aim);
        }

        public void Hear(NoiseSignal signal)
        {
            if (Visible || Time.time < nextHearing || signal.Source == self
                || (signal.Source != null && signal.Source.IsChildOf(self))) return;
            float radius = Mathf.Min(settings.hearingDistance, signal.Radius);
            float distanceSquared = (signal.Position - self.position).sqrMagnitude;
            if (distanceSquared > radius * radius) return;
            if (!world.ClearSight(Eye, signal.Position + Vector3.up)) radius *= settings.occludedHearingMultiplier;
            if (distanceSquared > radius * radius) return;
            nextHearing = Time.time + settings.hearingCooldown;
            float angle = (float)random.NextDouble() * Mathf.PI * 2f;
            float uncertainty = Mathf.Sqrt((float)random.NextDouble()) * settings.hearingUncertainty;
            LastKnown = signal.Position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * uncertainty;
            ObservedTime = Time.time; hasObservation = true;
        }

        public void Forget()
        {
            Visible = false; hasObservation = false;
            ObservedTime = LastSeenTime = float.NegativeInfinity; nextSense = 0f;
        }
    }
}
