// The building's room tone: a 2D low drone loop, plus a creak every 8-25 s somewhere 6-15 m from the player.
using UnityEngine;

namespace Game.Audio
{
    [AddComponentMenu("")]
    public sealed class AmbientDrone : MonoBehaviour
    {
        public float droneVolume = 0.35f;
        public float creakVolume = 0.6f;
        public Vector2 creakInterval = new Vector2(8f, 25f);
        public Vector2 creakDistance = new Vector2(6f, 15f);

        public AudioSource Source { get; private set; }

        AudioDirector _director;
        float _nextCreak;

        void Awake()
        {
            _director = GetComponent<AudioDirector>();
            var go = new GameObject("AmbientDrone");
            go.transform.SetParent(transform, false);
            Source = go.AddComponent<AudioSource>();
            AudioDirector.Configure2D(Source);
            Source.loop = true;
            Source.priority = 0; // never stolen by voice limiting
        }

        void Start()
        {
            Source.clip = ProceduralClips.DroneLoop();
            Source.volume = droneVolume * Bus();
            Source.Play();
            ScheduleCreak();
        }

        void Update()
        {
            Source.volume = droneVolume * Bus();
            if (Time.time < _nextCreak) return;
            ScheduleCreak();
            if (_director == null || _director.Player == null) return;
            Vector2 dir = Random.insideUnitCircle.normalized;
            if (dir == Vector2.zero) dir = Vector2.right;
            float dist = Random.Range(creakDistance.x, creakDistance.y);
            Vector3 pos = _director.Player.transform.position + new Vector3(dir.x, 0f, dir.y) * dist + Vector3.up * 1.5f;
            _director.Play3D(ProceduralClips.Creak(), pos, creakVolume, AudioBus.Ambient, Random.Range(0.85f, 1.1f));
        }

        void ScheduleCreak() => _nextCreak = Time.time + Random.Range(creakInterval.x, creakInterval.y);

        float Bus() => _director != null ? _director.BusVolume(AudioBus.Ambient) : 1f;
    }
}
