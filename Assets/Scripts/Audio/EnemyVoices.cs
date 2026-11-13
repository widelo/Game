// Gives every enemy a voice as the director discovers it (enemies spawn after the level, and again after
// every rebuild, so discovery is continuous rather than a one-off scan).
using CS462.StalkerAI;
using Game.Enemy;
using UnityEngine;

namespace Game.Audio
{
    [AddComponentMenu("")]
    public sealed class EnemyVoices : MonoBehaviour
    {
        AudioDirector _director;

        void Awake() { _director = GetComponent<AudioDirector>(); }
        void OnEnable() { if (_director != null) _director.EnemyDiscovered += Attach; }
        void OnDisable() { if (_director != null) _director.EnemyDiscovered -= Attach; }

        void Attach(Component enemy)
        {
            if (enemy == null || enemy.GetComponent<EnemyVoice>() != null) return;
            var voice = enemy.gameObject.AddComponent<EnemyVoice>();
            if (enemy is StalkerBoss boss) voice.Bind(boss);
            else if (enemy is EnemyAgent agent) voice.Bind(agent);
        }
    }

    /// <summary>
    /// One 3D source per enemy. Breathes every 2-4 s while searching (Patrol / Investigate / Stalk), growls on
    /// entering Chase plus a 2D shriek stinger once per chase, and goes silent while disengaging.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class EnemyVoice : MonoBehaviour
    {
        public enum Mood { Search, Chase, Silent }

        public Vector2 breathInterval = new Vector2(2f, 4f);
        public float breathVolume = 0.55f;
        public float growlVolume = 0.9f;
        public float shriekVolume = 0.7f;

        public Mood Current { get; private set; } = Mood.Search;
        public AudioSource Source { get; private set; }

        StalkerBoss _boss;
        EnemyAgent _agent;
        float _nextBreath;
        bool _shriekedThisChase;

        void Awake()
        {
            var go = new GameObject("Voice");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * 1.6f;
            Source = go.AddComponent<AudioSource>();
            AudioDirector.Configure3D(Source);
            _nextBreath = Time.time + Random.Range(0.5f, breathInterval.y);
        }

        public void Bind(StalkerBoss boss)
        {
            _boss = boss;
            boss.StateChanged += OnBossState;
            SetMood(Map(boss.State), false);
        }

        public void Bind(EnemyAgent agent)
        {
            _agent = agent;
            agent.OnEnemyEvent += OnAgentEvent;
            SetMood(Map(agent.StateName), false);
        }

        void OnDestroy()
        {
            if (_boss != null) _boss.StateChanged -= OnBossState;
            if (_agent != null) _agent.OnEnemyEvent -= OnAgentEvent;
        }

        public static Mood Map(StalkerState s)
        {
            switch (s)
            {
                case StalkerState.Chase: return Mood.Chase;
                case StalkerState.Disengage: return Mood.Silent;
                default: return Mood.Search; // Patrol, Investigate, Stalk
            }
        }

        /// <summary>Placeholder brain states mapped onto the same moods.</summary>
        public static Mood Map(string stateName)
        {
            switch (stateName)
            {
                case "Chase":
                case "Attack": return Mood.Chase;
                case "Recovering":
                case "Disengage": return Mood.Silent;
                default: return Mood.Search; // Idle, Patrol, Investigate, Stalk
            }
        }

        void OnBossState(StalkerBoss boss, StalkerState state) => SetMood(Map(state), true);

        void OnAgentEvent(EnemyAgent agent, string e)
        {
            if (e == "caught") { SetMood(Mood.Silent, true); return; }
            if (e.StartsWith("state:", System.StringComparison.Ordinal)) SetMood(Map(e.Substring(6)), true);
        }

        void SetMood(Mood next, bool announce)
        {
            Mood prev = Current;
            Current = next;
            if (next == Mood.Silent) { Source.Stop(); _shriekedThisChase = false; return; }
            if (next == Mood.Search) { _shriekedThisChase = false; return; }
            if (!announce || prev == Mood.Chase) return;

            AudioDirector d = AudioDirector.Instance;
            float bus = d != null ? d.BusVolume(AudioBus.Enemy) : 1f;
            Source.Stop();
            Source.pitch = Random.Range(0.92f, 1.05f);
            Source.PlayOneShot(ProceduralClips.Growl(), growlVolume * bus);
            if (!_shriekedThisChase && d != null)
            {
                _shriekedThisChase = true;
                d.Play2D(ProceduralClips.Shriek(), shriekVolume, AudioBus.Enemy);
            }
            _nextBreath = Time.time + Random.Range(breathInterval.x, breathInterval.y);
        }

        void Update()
        {
            if (_agent != null) // placeholder: the HUD state also flips to "Recovering" without an event
            {
                Mood m = Map(_agent.StateName);
                if (m != Current && m == Mood.Silent) SetMood(m, true);
                else if (m == Mood.Search && Current == Mood.Silent) SetMood(m, false);
            }
            if (Current != Mood.Search || Time.time < _nextBreath) return;
            _nextBreath = Time.time + Random.Range(breathInterval.x, breathInterval.y);
            if (Source.isPlaying) return;
            AudioDirector d = AudioDirector.Instance;
            Source.pitch = Random.Range(0.85f, 1.05f);
            Source.PlayOneShot(ProceduralClips.Breath(), breathVolume * (d != null ? d.BusVolume(AudioBus.Enemy) : 1f));
        }
    }
}
