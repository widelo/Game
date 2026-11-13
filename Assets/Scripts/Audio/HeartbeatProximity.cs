// The player's heartbeat: silent until an enemy is within 14 m, then 1.0 -> 2.4 beats/s and 0 -> 0.5 volume
// as the distance closes.
using UnityEngine;

namespace Game.Audio
{
    [AddComponentMenu("")]
    public sealed class HeartbeatProximity : MonoBehaviour
    {
        public float range = 14f;
        public float minRate = 1.0f;
        public float maxRate = 2.4f;
        public float maxVolume = 0.5f;

        public float CurrentRate { get; private set; }
        public float CurrentVolume { get; private set; }

        AudioDirector _director;
        AudioSource _source;
        float _phase;

        void Awake()
        {
            _director = GetComponent<AudioDirector>();
            var go = new GameObject("Heartbeat");
            go.transform.SetParent(transform, false);
            _source = go.AddComponent<AudioSource>();
            AudioDirector.Configure2D(_source);
        }

        void Update()
        {
            CurrentRate = 0f;
            CurrentVolume = 0f;
            if (_director == null || _director.Player == null) { _phase = 0f; return; }
            if (_director.NearestEnemy(_director.Player.transform.position, out float dist) == null || dist >= range)
            {
                _phase = 0.7f; // the first beat comes quickly once an enemy closes in
                return;
            }
            float t = 1f - dist / range;
            CurrentRate = Mathf.Lerp(minRate, maxRate, t);
            CurrentVolume = Mathf.Lerp(0f, maxVolume, t);
            _phase += CurrentRate * Time.deltaTime;
            if (_phase < 1f) return;
            _phase -= Mathf.Floor(_phase);
            _source.PlayOneShot(ProceduralClips.Heartbeat(), CurrentVolume * _director.BusVolume(AudioBus.Sfx));
        }
    }
}
