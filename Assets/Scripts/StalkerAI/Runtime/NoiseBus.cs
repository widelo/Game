using System;
using UnityEngine;

namespace CS462.StalkerAI
{
    public readonly struct NoiseSignal
    {
        public readonly Vector3 Position;
        public readonly float Radius;
        public readonly Transform Source;
        public NoiseSignal(Vector3 position, float radius, Transform source)
        { Position = position; Radius = radius; Source = source; }
    }

    /// <summary>Doors, thrown objects or other gameplay may call Emit. No live target tracking is stored.</summary>
    public static class NoiseBus
    {
        public static event Action<NoiseSignal> Heard;
        public static void Emit(Vector3 position, float radius, Transform source = null)
        { if (radius > 0f) Heard?.Invoke(new NoiseSignal(position, radius, source)); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Heard = null; }
    }
}
