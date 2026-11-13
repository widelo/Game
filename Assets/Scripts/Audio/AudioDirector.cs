// Owns the game's procedural audio: bus volumes, a small pool of one-shot sources, the enemy list every
// sound component shares, and the child components that make the actual sounds.
//
// Installed automatically after any scene load that contains a LevelBuilder (Main), or explicitly with
// AudioDirector.Install(). Nothing is authored in a scene and no asset files are involved.
//
// There is no AudioMixer: each bus is a float multiplied into the volumes the components set, and master
// drives AudioListener.volume (M toggles mute).
using System;
using System.Collections.Generic;
using CS462.StalkerAI;
using Game.Enemy;
using Game.Player;
using LevelGen.Core;
using LevelGen.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Audio
{
    public enum AudioBus { Ambient, Sfx, Enemy }

    [AddComponentMenu("Game/Audio Director")]
    [DisallowMultipleComponent]
    public sealed class AudioDirector : MonoBehaviour
    {
        public static AudioDirector Instance { get; private set; }

        /// <summary>When true, every scene load that contains a LevelBuilder gets an AudioDirector.</summary>
        public static bool AutoInstall = true;

        [Header("Buses")]
        [Range(0f, 1f)] public float master = 1f;
        [Range(0f, 1f)] public float ambient = 1f;
        [Range(0f, 1f)] public float sfx = 1f;
        [Range(0f, 1f)] public float enemy = 1f;

        [Header("Debug")]
        public KeyCode muteKey = KeyCode.M;

        public bool Muted { get; private set; }
        public LevelBuilder Builder { get; private set; }
        public FirstPersonController Player { get; private set; }
        public AmbientDrone Drone { get; private set; }
        public HeartbeatProximity Heart { get; private set; }
        public EnemyVoices Voices { get; private set; }
        public ObjectiveSounds Objectives { get; private set; }
        public PlayerFootsteps Footsteps { get; private set; }

        /// <summary>Live enemy roots (StalkerBoss and placeholder EnemyAgent), rescanned every second.</summary>
        public IReadOnlyList<Component> Enemies => _enemies;

        /// <summary>Raised once per enemy component the first time a rescan sees it.</summary>
        public event Action<Component> EnemyDiscovered;
        /// <summary>Raised after every LevelBuilder.OnLevelBuilt (and once on start if a level is already built).</summary>
        public event Action LevelReady;

        const float RescanInterval = 1f;
        const int PoolSize = 10;

        readonly List<Component> _enemies = new List<Component>();
        readonly HashSet<Component> _known = new HashSet<Component>();
        readonly AudioSource[] _pool = new AudioSource[PoolSize];
        AudioSource _ui;
        int _next;
        float _rescanAt;
        bool _subscribed, _loggedMute;

        // ------------------------------------------------------------------ install

        /// <summary>Creates the "Audio" object with every sound component wired, unless one already exists.</summary>
        public static AudioDirector Install()
        {
            if (Instance != null) return Instance;
            var existing = FindAnyObjectByType<AudioDirector>();
            if (existing != null) return existing;
            var go = new GameObject("Audio");
            return go.AddComponent<AudioDirector>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            TryAutoInstall();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => TryAutoInstall();

        static void TryAutoInstall()
        {
            if (!AutoInstall || !Application.isPlaying) return;
            if (FindAnyObjectByType<LevelBuilder>() != null) Install();
        }

        // ------------------------------------------------------------------ lifecycle

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;

            _ui = gameObject.AddComponent<AudioSource>();
            Configure2D(_ui);
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject("OneShot3D_" + i);
                go.transform.SetParent(transform, false);
                _pool[i] = go.AddComponent<AudioSource>();
                Configure3D(_pool[i]);
            }

            Drone = gameObject.AddComponent<AmbientDrone>();
            Heart = gameObject.AddComponent<HeartbeatProximity>();
            Voices = gameObject.AddComponent<EnemyVoices>();
            Objectives = gameObject.AddComponent<ObjectiveSounds>();
        }

        void Start()
        {
            ProceduralClips.WarmAll();
            ResolveScene();
            EnsureSingleListener();
            Rescan();
            if (Builder != null && Builder.CurrentLayout != null) LevelReady?.Invoke();
        }

        void OnDestroy()
        {
            if (_subscribed && Builder != null) Builder.OnLevelBuilt -= HandleLevelBuilt;
            _subscribed = false;
            if (Instance == this)
            {
                Instance = null;
                AudioListener.volume = 1f;
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(muteKey))
            {
                Muted = !Muted;
                if (!_loggedMute) { _loggedMute = true; UnityEngine.Debug.Log($"[Audio] {muteKey} toggles master mute"); }
            }
            AudioListener.volume = Muted ? 0f : master;

            if (Time.unscaledTime >= _rescanAt)
            {
                _rescanAt = Time.unscaledTime + RescanInterval;
                ResolveScene();
                Rescan();
            }
        }

        void ResolveScene()
        {
            if (Builder == null)
            {
                Builder = FindAnyObjectByType<LevelBuilder>();
                _subscribed = false;
            }
            if (Builder != null && !_subscribed)
            {
                Builder.OnLevelBuilt += HandleLevelBuilt;
                _subscribed = true;
            }
            if (Player == null)
            {
                Player = FindAnyObjectByType<FirstPersonController>();
                if (Player != null)
                {
                    Footsteps = Player.GetComponent<PlayerFootsteps>();
                    if (Footsteps == null) Footsteps = Player.gameObject.AddComponent<PlayerFootsteps>();
                }
            }
        }

        void HandleLevelBuilt(LevelLayout layout)
        {
            _rescanAt = 0f; // enemies are respawned by their spawners; pick them up next frame
            LevelReady?.Invoke();
        }

        /// <summary>The player camera carries the scene's AudioListener; add one only when none exists.</summary>
        void EnsureSingleListener()
        {
            AudioListener[] all = FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude);
            int enabled = 0;
            foreach (var l in all) if (l.enabled) enabled++;
            if (enabled > 0)
            {
                if (enabled > 1) UnityEngine.Debug.LogWarning($"[Audio] {enabled} AudioListeners enabled; Unity will only use one");
                return;
            }
            Camera cam = Camera.main;
            if (cam == null && Player != null) cam = Player.GetComponentInChildren<Camera>();
            (cam != null ? cam.gameObject : gameObject).AddComponent<AudioListener>();
        }

        void Rescan()
        {
            _enemies.RemoveAll(c => c == null);
            foreach (var boss in FindObjectsByType<StalkerBoss>(FindObjectsInactive.Exclude)) Track(boss);
            foreach (var agent in FindObjectsByType<EnemyAgent>(FindObjectsInactive.Exclude)) Track(agent);
        }

        void Track(Component c)
        {
            if (!_known.Add(c)) { if (!_enemies.Contains(c)) _enemies.Add(c); return; }
            _enemies.Add(c);
            EnemyDiscovered?.Invoke(c);
        }

        // ------------------------------------------------------------------ queries / playback

        public float BusVolume(AudioBus bus)
        {
            switch (bus)
            {
                case AudioBus.Ambient: return ambient;
                case AudioBus.Enemy: return enemy;
                default: return sfx;
            }
        }

        /// <summary>Nearest active enemy to <paramref name="from"/> (horizontal distance), or null.</summary>
        public Transform NearestEnemy(Vector3 from, out float distance)
        {
            distance = float.PositiveInfinity;
            Transform best = null;
            foreach (var c in _enemies)
            {
                if (c == null || !((Behaviour)c).isActiveAndEnabled) continue;
                Vector3 d = c.transform.position - from;
                d.y = 0f;
                float m = d.magnitude;
                if (m < distance) { distance = m; best = c.transform; }
            }
            return best;
        }

        /// <summary>Non-positional one-shot (UI-ish stingers).</summary>
        public void Play2D(AudioClip clip, float volume, AudioBus bus, float pitch = 1f)
        {
            if (clip == null) return;
            _ui.pitch = pitch;
            _ui.PlayOneShot(clip, volume * BusVolume(bus));
        }

        /// <summary>Positional one-shot from a round-robin pool.</summary>
        public void Play3D(AudioClip clip, Vector3 position, float volume, AudioBus bus, float pitch = 1f)
        {
            if (clip == null) return;
            AudioSource s = _pool[_next];
            _next = (_next + 1) % PoolSize;
            s.transform.position = position;
            s.pitch = pitch;
            s.clip = clip;
            s.volume = volume * BusVolume(bus);
            s.Play();
        }

        public static void Configure2D(AudioSource s)
        {
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            s.dopplerLevel = 0f;
        }

        public static void Configure3D(AudioSource s)
        {
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = 2f;
            s.maxDistance = 30f;
            s.dopplerLevel = 0f;
        }
    }
}
