// Procedural "life" for rigid, unrigged enemy models (the boss is a static OBJ with no skeleton and no clips).
// Sits on the enemy ROOT, never touches the root itself (the AI owns it), and only animates the visual child:
// breathing, a speed-driven walk bob/sway, forward lean, smooth facing, random twitches, a proximity lunge
// (anticipation -> strike -> recover) and a weeping-angel freeze, plus a per-instance dark material whose
// emission pulses with the breath and flashes on the lunge strike.
//
// Lunge timeline (seconds from trigger, offsets on the model child's local pose only):
//   0.00-0.18 anticipation: scale.y x0.92, lean -8 deg, pull back 0.15 m along -facing
//   0.18-0.30 strike:       scale.y x1.10, lean +18 deg, thrust 0.35 m along facing, emission flash
//   0.30-0.65 recover:      ease back to the walk pose. Walk bob is suppressed throughout.
// Fired by TriggerLunge() (mood provider / attack event), by Mood.Lunge, or by Mood.Hunt within lungeDistance
// of the player. 1.2 s cooldown after a lunge ends; a lunge in progress is never restarted.
//
// Driven by three inputs only: horizontal speed (root position delta, smoothed), Mood (written every frame by a
// MonsterMoodProvider on the same root) and a hash-seeded System.Random for per-instance variation.
// No per-frame allocations: renderers, the property block and the random source are created once.
using Game.Player;
using LevelGen.Unity;
using UnityEngine;

namespace Game.Enemy
{
    /// <summary>What the monster is "feeling"; a visual layer only, the AI keeps its own state.</summary>
    public enum Mood { Idle, Prowl, Alert, Hunt, Lunge, Frozen }

    [DisallowMultipleComponent]
    public sealed class MonsterAnimator : MonoBehaviour
    {
        public const string ModelChildName = "Model";

        // ---- tuning (spec values) ----
        const float BreathHzIdle = 0.4f, BreathHzAlert = 0.55f, BreathHzHunt = 1.1f;
        const float BreathAmplitude = 0.03f;                     // scale.y 1.00 .. 1.03
        const float BobPerSpeed = 0.04f / 2f, MaxBob = 0.12f;    // 0.04 m * speed / 2
        const float StepsPerMetre = 0.55f;                       // cadence proportional to speed
        const float SwayDegrees = 2f, MaxLeanHunt = 10f, MaxLeanWalk = 3f;
        const float AngularSpeed = 240f;
        const float LungeAnticipation = 0.18f, LungeStrike = 0.12f, LungeRecover = 0.35f, LungeCooldown = 1.2f;
        const float LungeTotal = LungeAnticipation + LungeStrike + LungeRecover;
        const float AnticScaleY = 0.92f, AnticLean = -8f, AnticPull = -0.15f;
        const float StrikeScaleY = 1.10f, StrikeLean = 18f, StrikeThrust = 0.35f;
        static readonly Color LungeFlash = new Color(1f, 0.1f, 0.05f);
        const float TeleportSpeed = 25f;                         // faster than this = warp, not walking
        static readonly Color BodyColour = new Color(0.08f, 0.07f, 0.09f);
        static readonly Color DimGlow = new Color(0.35f, 0.02f, 0.02f);
        static readonly Color HuntGlow = new Color(0.9f, 0.05f, 0.05f);
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        [Tooltip("Replace the model's materials with a per-instance dark URP Lit whose emission this component pulses.")]
        [SerializeField] bool overrideMaterial = true;
        [Tooltip("In Hunt, the lunge fires when the player is within this horizontal distance (m).")]
        [SerializeField] float lungeDistance = 3f;

        /// <summary>Set by a provider every frame (or by hand). Read in LateUpdate.</summary>
        public Mood Mood { get; set; } = Mood.Idle;
        /// <summary>Must be set before the first frame (Start) to take effect.</summary>
        public bool OverrideMaterial { get => overrideMaterial; set => overrideMaterial = value; }
        public Transform Model => _model;
        /// <summary>Smoothed horizontal speed of the root, m/s.</summary>
        public float Speed => _speed;
        public Mood CurrentMood => _mood;
        public float LungeDistance { get => lungeDistance; set => lungeDistance = value; }
        /// <summary>True from TriggerLunge until the recover phase ends.</summary>
        public bool IsLunging => Time.time - _lungeStart < LungeTotal;
        /// <summary>True when TriggerLunge would start a lunge now (not lunging, cooldown elapsed).</summary>
        public bool LungeReady => !IsLunging && Time.time >= _nextLungeAt;

        Transform _model;
        Vector3 _basePos, _baseScale, _pivot;
        Quaternion _baseRot;
        Renderer[] _renderers;
        Material[][] _originalMaterials;
        Material _material;
        MaterialPropertyBlock _block;
        System.Random _rng;

        Vector3 _lastRootPos, _velocity;
        float _speed, _worldYaw, _lean, _breathPhase, _walkPhase, _rateJitter, _glowJitter;
        float _nextTwitchAt, _twitchStart, _twitchDuration;
        Vector3 _twitchEuler;
        float _lungeStart = -100f, _nextLungeAt;
        Mood _mood = Mood.Idle;
        Color _glow;
        bool _bound;
        float _nextBindAttempt;

        static FirstPersonController s_player;
        static float s_nextPlayerLookup;

        /// <summary>The player, found once with FindAnyObjectByType and cached (re-looked-up at most 1/s while missing).</summary>
        internal static FirstPersonController Player
        {
            get
            {
                if (s_player == null && Time.unscaledTime >= s_nextPlayerLookup)
                {
                    s_nextPlayerLookup = Time.unscaledTime + 1f;
                    s_player = FindAnyObjectByType<FirstPersonController>();
                }
                return s_player;
            }
        }

        void Start()
        {
            unchecked
            {
                // Hash seed: name + instance id, so two enemies spawned in the same frame still differ.
                int seed = (gameObject.name.GetHashCode() * 486187739) ^ GetEntityId().GetHashCode();
                _rng = new System.Random(seed);
            }
            _rateJitter = Range(0.85f, 1.15f);
            _glowJitter = Range(0.85f, 1.1f);
            _breathPhase = Range(0f, Mathf.PI * 2f);
            _walkPhase = Range(0f, Mathf.PI * 2f);
            _block = new MaterialPropertyBlock();
            _lastRootPos = transform.position;
            _worldYaw = transform.eulerAngles.y;
            ScheduleTwitch(Time.time);
            TryBind();
        }

        /// <summary>
        /// Starts the anticipation+strike lunge. Ignored while a lunge is playing or during the 1.2 s cooldown
        /// that follows one, so callers (mood rule, attack event) may fire it freely.
        /// </summary>
        public void TriggerLunge()
        {
            if (!LungeReady) return;
            _lungeStart = Time.time;
            _nextLungeAt = _lungeStart + LungeTotal + LungeCooldown;
        }

        void OnDestroy()
        {
            RestoreMaterials();
            if (_material != null) Destroy(_material);
        }

        /// <summary>Finds the visual child ("Model", else the first child with renderers) and records its rest pose.</summary>
        bool TryBind()
        {
            _nextBindAttempt = Time.time + 0.5f;
            Transform model = transform.Find(ModelChildName);
            if (model == null)
            {
                for (int i = 0; i < transform.childCount && model == null; i++)
                {
                    Transform c = transform.GetChild(i);
                    if (c.GetComponentInChildren<Renderer>(true) != null) model = c;
                }
            }
            if (model == null) return false;

            _model = model;
            _basePos = model.localPosition;
            _baseRot = model.localRotation;
            _baseScale = model.localScale; // the model's own starting scale (e.g. a director's 0.8 X/Z squash) is the rest
            _lastWrittenScale = _baseScale;
            // Pivot for scale/lean = the model's feet, so breathing and leaning do not lift it off the floor.
            Bounds b = ModelFit.RendererBounds(model.gameObject);
            float groundY = b.size.sqrMagnitude > 0f ? transform.InverseTransformPoint(b.min).y : _basePos.y;
            _pivot = new Vector3(_basePos.x, groundY, _basePos.z);

            _renderers = model.GetComponentsInChildren<Renderer>(true);
            if (overrideMaterial) ApplyMaterial();
            _glow = DimGlow;
            _bound = true;
            return true;
        }

        void ApplyMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return;
            _material = new Material(shader) { name = "MonsterSkin (runtime)" };
            if (_material.HasProperty(BaseColorId)) _material.SetColor(BaseColorId, BodyColour);
            if (_material.HasProperty(ColorId)) _material.SetColor(ColorId, BodyColour);
            if (_material.HasProperty(SmoothnessId)) _material.SetFloat(SmoothnessId, 0.2f);
            if (_material.HasProperty(MetallicId)) _material.SetFloat(MetallicId, 0f);
            if (_material.HasProperty(EmissionId))
            {
                _material.EnableKeyword("_EMISSION");
                _material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                _material.SetColor(EmissionId, DimGlow);
            }
            _originalMaterials = new Material[_renderers.Length][];
            for (int r = 0; r < _renderers.Length; r++)
            {
                Renderer rend = _renderers[r];
                if (rend is LineRenderer || rend is TrailRenderer || rend.GetType().Name == "ParticleSystemRenderer") continue; // init only
                _originalMaterials[r] = rend.sharedMaterials;
                var slots = new Material[Mathf.Max(1, _originalMaterials[r].Length)];
                for (int i = 0; i < slots.Length; i++) slots[i] = _material;
                rend.sharedMaterials = slots;
            }
        }

        void RestoreMaterials()
        {
            if (_originalMaterials == null || _renderers == null) return;
            for (int r = 0; r < _renderers.Length; r++)
                if (_renderers[r] != null && _originalMaterials[r] != null)
                {
                    _renderers[r].SetPropertyBlock(null);
                    _renderers[r].sharedMaterials = _originalMaterials[r];
                }
        }

        // LateUpdate: the AI (CharacterController.Move / NavMeshAgent) has already moved the root this frame.
        void LateUpdate()
        {
            if (!_bound && (Time.time < _nextBindAttempt || !TryBind())) return;
            if (_model == null) { _bound = false; return; } // model destroyed/replaced: rebind later

            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            float now = Time.time;

            // ---- horizontal speed from the root's position delta ----
            Vector3 pos = transform.position;
            Vector3 delta = pos - _lastRootPos; delta.y = 0f;
            _lastRootPos = pos;
            Vector3 rawVel = delta / dt;
            if (rawVel.sqrMagnitude > TeleportSpeed * TeleportSpeed) rawVel = Vector3.zero;
            _velocity = Vector3.Lerp(_velocity, rawVel, 1f - Mathf.Exp(-10f * dt));
            _speed = _velocity.magnitude;

            // ---- mood transitions ----
            Mood mood = Mood;
            bool unfreezeSnap = false;
            if (mood != _mood)
            {
                if (_mood == Mood.Frozen) unfreezeSnap = true;
                if (mood == Mood.Frozen) SetGlow(Color.black);
                _mood = mood;
            }
            // Weeping-angel beat: hold the exact pose (world facing included), glow off. The root may still move.
            if (mood == Mood.Frozen)
            {
                ComposePose(_lastBob, _lean, _lastRoll, _lastThrust, _lastScaleK, _lastTwitch);
                return;
            }

            // A director (or anything else) re-scaled the model since our last write: adopt it as the new rest scale.
            if (_model.localScale != _lastWrittenScale) _baseScale = _model.localScale;

            // ---- lunge triggers: Mood.Lunge, or Hunt with the player inside lungeDistance ----
            if (mood == Mood.Lunge || (mood == Mood.Hunt && PlayerDistance(pos) < lungeDistance)) TriggerLunge();
            LungeSample(now - _lungeStart, out float lungeScaleY, out float lungeLean, out float thrust,
                        out float flash, out float lungeEnv);

            bool hunting = mood == Mood.Hunt || mood == Mood.Lunge;

            // ---- breathing ----
            float breathHz = (hunting ? BreathHzHunt : mood == Mood.Alert ? BreathHzAlert : BreathHzIdle) * _rateJitter;
            _breathPhase = Wrap(_breathPhase + Mathf.PI * 2f * breathHz * dt);
            float breath01 = 0.5f * (1f + Mathf.Sin(_breathPhase));
            float scaleY = 1f + BreathAmplitude * breath01;

            // ---- walk cycle ----
            float moving = Mathf.Clamp01(_speed / 1.5f);
            _walkPhase = Wrap(_walkPhase + Mathf.PI * 2f * StepsPerMetre * _speed * dt);
            float bob = Mathf.Min(MaxBob, BobPerSpeed * _speed) * Mathf.Abs(Mathf.Sin(_walkPhase)) * (1f - lungeEnv);
            float roll = SwayDegrees * moving * Mathf.Sin(_walkPhase); // one sway per two steps

            // ---- lean ----
            float leanTarget = mood == Mood.Hunt || mood == Mood.Lunge ? MaxLeanHunt * Mathf.Clamp01(_speed / 3f)
                             : mood == Mood.Prowl || mood == Mood.Alert ? MaxLeanWalk * Mathf.Clamp01(_speed / 3f)
                             : 0f;
            _lean = unfreezeSnap ? leanTarget : Mathf.Lerp(_lean, leanTarget, 1f - Mathf.Exp(-8f * dt));

            // ---- facing: toward the player when Alert/Hunt, else toward velocity, else the root's own facing ----
            float targetYaw = transform.eulerAngles.y;
            FirstPersonController player = (mood == Mood.Alert || hunting) ? Player : null;
            if (player != null)
            {
                Vector3 to = player.transform.position - pos; to.y = 0f;
                if (to.sqrMagnitude > 0.04f) targetYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            }
            else if (_speed > 0.2f) targetYaw = Mathf.Atan2(_velocity.x, _velocity.z) * Mathf.Rad2Deg;
            _worldYaw = unfreezeSnap ? targetYaw : Mathf.MoveTowardsAngle(_worldYaw, targetYaw, AngularSpeed * dt);

            // ---- twitch ----
            if (unfreezeSnap) StartTwitch(now, 4f, 8f, 0.08f, 0.12f); // the single hard snap out of Frozen
            else if (now >= _nextTwitchAt) StartTwitch(now, 0.5f, 2f, 0.06f, 0.12f);
            Quaternion twitch = Quaternion.identity;
            float tt = (now - _twitchStart) / _twitchDuration;
            if (tt >= 0f && tt < 1f)
            {
                float w = tt < 0.6f ? 1f : 1f - (tt - 0.6f) / 0.4f; // snap in, ease out
                twitch = Quaternion.Euler(_twitchEuler * w);
            }

            Vector3 scaleK = new Vector3(1f, scaleY * lungeScaleY, 1f);
            ComposePose(bob, _lean + lungeLean, roll, thrust, scaleK, twitch);

            // ---- emission ----
            Color glow = hunting ? HuntGlow * (0.75f + 0.25f * breath01) : DimGlow * (0.35f + 0.65f * breath01);
            SetGlow(Color.Lerp(_glow, glow * _glowJitter, unfreezeSnap ? 1f : 1f - Mathf.Exp(-6f * dt)), flash);
        }

        /// <summary>
        /// Lunge offsets at <paramref name="t"/> seconds after the trigger (rest values outside 0..LungeTotal).
        /// env = 1 through anticipation+strike, easing to 0 over recover (walk-bob suppression weight).
        /// </summary>
        static void LungeSample(float t, out float scaleY, out float lean, out float thrust, out float flash, out float env)
        {
            scaleY = 1f; lean = 0f; thrust = 0f; flash = 0f; env = 0f;
            if (t < 0f || t >= LungeTotal) return;
            if (t < LungeAnticipation)
            {
                float u = Smooth(t / LungeAnticipation);
                scaleY = Mathf.Lerp(1f, AnticScaleY, u);
                lean = AnticLean * u;
                thrust = AnticPull * u;
                env = 1f;
            }
            else if (t < LungeAnticipation + LungeStrike)
            {
                float u = (t - LungeAnticipation) / LungeStrike;
                u = 1f - (1f - u) * (1f - u); // ease-out: snaps forward, settles into the peak
                scaleY = Mathf.Lerp(AnticScaleY, StrikeScaleY, u);
                lean = Mathf.Lerp(AnticLean, StrikeLean, u);
                thrust = Mathf.Lerp(AnticPull, StrikeThrust, u);
                flash = u;
                env = 1f;
            }
            else
            {
                float u = Smooth((t - LungeAnticipation - LungeStrike) / LungeRecover);
                scaleY = Mathf.Lerp(StrikeScaleY, 1f, u);
                lean = StrikeLean * (1f - u);
                thrust = StrikeThrust * (1f - u);
                flash = 1f - u;
                env = 1f - u;
            }
        }

        static float Smooth(float u) => u * u * (3f - 2f * u);

        /// <summary>Horizontal distance from <paramref name="pos"/> to the player, +infinity when there is none.</summary>
        static float PlayerDistance(Vector3 pos)
        {
            FirstPersonController player = Player;
            if (player == null) return float.PositiveInfinity;
            Vector3 d = player.transform.position - pos; d.y = 0f;
            return d.magnitude;
        }

        Vector3 _lastScaleK = Vector3.one, _lastWrittenScale;
        float _lastBob, _lastRoll, _lastThrust;
        Quaternion _lastTwitch = Quaternion.identity;

        /// <summary>
        /// Writes the model's local pose: rest pose (incl. its starting localScale), scaled and rotated about the
        /// feet pivot, plus bob and a thrust along the model's facing. Never touches the root.
        /// </summary>
        void ComposePose(float bob, float lean, float roll, float thrust, Vector3 scaleK, Quaternion twitch)
        {
            _lastScaleK = scaleK; _lastBob = bob; _lastRoll = roll; _lastThrust = thrust; _lastTwitch = twitch;
            float yawOffset = Mathf.DeltaAngle(transform.eulerAngles.y, _worldYaw);
            Quaternion yaw = Quaternion.Euler(0f, yawOffset, 0f);
            Quaternion off = yaw * Quaternion.Euler(lean, 0f, roll) * twitch;
            Vector3 rel = _basePos - _pivot;
            Vector3 scaled = new Vector3(rel.x * scaleK.x, rel.y * scaleK.y, rel.z * scaleK.z);
            _model.localPosition = _pivot + off * scaled + Vector3.up * bob + yaw * (Vector3.forward * thrust);
            _model.localRotation = off * _baseRot;
            _lastWrittenScale = Vector3.Scale(_baseScale, scaleK);
            _model.localScale = _lastWrittenScale;
        }

        /// <summary>Sets the smoothed base glow and applies it, blended toward the lunge flash by <paramref name="flash"/>.</summary>
        void SetGlow(Color c, float flash = 0f)
        {
            _glow = c;
            if (_material == null || _renderers == null) return;
            _block.SetColor(EmissionId, flash > 0f ? Color.Lerp(c, LungeFlash, flash) : c);
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && _originalMaterials[i] != null) _renderers[i].SetPropertyBlock(_block);
        }

        void StartTwitch(float now, float minDeg, float maxDeg, float minDur, float maxDur)
        {
            _twitchStart = now;
            _twitchDuration = Range(minDur, maxDur);
            _twitchEuler = new Vector3(SignedRange(minDeg, maxDeg), SignedRange(minDeg, maxDeg), SignedRange(minDeg, maxDeg));
            ScheduleTwitch(now);
        }

        void ScheduleTwitch(float now)
        {
            bool hunting = _mood == Mood.Hunt || _mood == Mood.Lunge;
            _nextTwitchAt = now + (hunting ? Range(0.3f, 0.8f) : Range(1f, 4f));
        }

        float Range(float a, float b) => a + (float)_rng.NextDouble() * (b - a);
        float SignedRange(float a, float b) => Range(a, b) * (_rng.Next(2) == 0 ? -1f : 1f);
        static float Wrap(float phase) => phase > Mathf.PI * 2f ? phase - Mathf.PI * 2f : phase;
    }
}
