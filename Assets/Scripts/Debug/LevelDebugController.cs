// Debug driver + HUD for the level generator. Owns player spawning so Game.Player stays level-agnostic.
// Old Input Manager only. Legacy OnGUI on purpose for v1 - no Canvas / UI Toolkit dependency.
// NOTE: this namespace is literally named "Debug", so UnityEngine.Debug must always be fully qualified.
using System.Collections.Generic;
using System.Text;
using Game.Player;
using LevelGen.Core;
using LevelGen.Unity;
using UnityEngine;

namespace Game.Debug
{
    public sealed class LevelDebugController : MonoBehaviour
    {
        [Tooltip("The LevelBuilder whose OnLevelBuilt event drives player placement.")]
        public LevelBuilder builder;

        [Tooltip("Root transform of the player (the object carrying the CharacterController).")]
        public Transform player;

        [Tooltip("Height above the spawn marker the player capsule is dropped at.")]
        public float spawnHeight = 1.1f;

        public bool showHud = true;

        /// <summary>L-key override: every builder light forced to intensity 1 so geometry reads through the murk.</summary>
        public bool lightsDebug;

        readonly Dictionary<Light, float> _originalIntensity = new Dictionary<Light, float>();
        readonly Dictionary<Light, bool> _originalEnabled = new Dictionary<Light, bool>();
        FirstPersonController _fpc;

        void OnEnable()
        {
            if (builder != null) builder.OnLevelBuilt += HandleLevelBuilt;
        }

        void OnDisable()
        {
            if (builder != null) builder.OnLevelBuilt -= HandleLevelBuilt;
        }

        void HandleLevelBuilt(LevelLayout layout)
        {
            // The old lights were destroyed with the previous geometry; their entries are dead keys.
            _originalIntensity.Clear();
            _originalEnabled.Clear();
            TeleportPlayerToSpawn();
            if (lightsDebug) ApplyLightsDebug(true);
        }

        /// <summary>
        /// Moves the player to the builder's spawn point. The CharacterController is disabled across the
        /// assignment because an enabled controller resolves penetration and fights a raw transform write.
        /// </summary>
        public void TeleportPlayerToSpawn()
        {
            if (builder == null || player == null) return;

            var cc = player.GetComponent<CharacterController>();
            bool wasEnabled = cc != null && cc.enabled;
            if (cc != null) cc.enabled = false;

            player.position = builder.PlayerSpawnPoint + Vector3.up * spawnHeight;

            if (cc != null) cc.enabled = wasEnabled;
        }

        void Update()
        {
            if (builder == null) return;

            if (Input.GetKeyDown(KeyCode.R)) builder.Rebuild();
            else if (Input.GetKeyDown(KeyCode.N)) builder.Build(CurrentSeed() + 1);
            else if (Input.GetKeyDown(KeyCode.P)) builder.Build(CurrentSeed() - 1);
            else if (Input.GetKeyDown(KeyCode.F1)) showHud = !showHud;
            else if (Input.GetKeyDown(KeyCode.L)) ApplyLightsDebug(!lightsDebug);
        }

        /// <summary>Flat-lights every Light under the builder, remembering the authored values so L toggles back.</summary>
        public void ApplyLightsDebug(bool on)
        {
            lightsDebug = on;
            if (builder == null) return;

            foreach (var light in builder.transform.GetComponentsInChildren<Light>(true))
            {
                if (on)
                {
                    if (!_originalIntensity.ContainsKey(light))
                    {
                        _originalIntensity[light] = light.intensity;
                        _originalEnabled[light] = light.enabled;
                    }
                    light.intensity = 1f;
                    light.enabled = true;
                }
                else
                {
                    if (_originalIntensity.TryGetValue(light, out float i)) light.intensity = i;
                    if (_originalEnabled.TryGetValue(light, out bool e)) light.enabled = e;
                }
            }
            if (!on)
            {
                _originalIntensity.Clear();
                _originalEnabled.Clear();
            }
        }

        int CurrentSeed()
        {
            var layout = builder.CurrentLayout;
            return layout != null ? layout.Seed : 0;
        }

        /// <summary>Room whose Center is nearest the player on the XZ plane, or -1 if unknown.</summary>
        public int CurrentRoomId()
        {
            var layout = builder != null ? builder.CurrentLayout : null;
            if (layout == null || layout.Rooms == null || layout.Rooms.Count == 0 || player == null) return -1;

            Vector3 p = player.position;
            int best = -1;
            double bestSq = double.MaxValue;
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                var c = layout.Rooms[i].Center;
                double dx = c.X - p.x;
                double dz = c.Z - p.z;
                double sq = dx * dx + dz * dz;
                if (sq < bestSq) { bestSq = sq; best = layout.Rooms[i].Id; }
            }
            return best;
        }

        // Counted inline rather than via a Core extension so this HUD never blocks on Core's public surface.
        static void CountContents(LevelLayout layout, out int lights, out int props)
        {
            lights = 0;
            props = 0;
            if (layout == null) return;
            foreach (var r in layout.Rooms)
            {
                if (r.Lights != null) lights += r.Lights.Count;
                if (r.Props != null) props += r.Props.Count;
            }
            foreach (var c in layout.Corridors)
            {
                if (c.Lights != null) lights += c.Lights.Count;
                if (c.Props != null) props += c.Props.Count;
            }
        }

        string MovementLine()
        {
            if (_fpc == null && player != null) _fpc = player.GetComponent<FirstPersonController>();
            if (_fpc == null) return "move: -";

            Vector3 v = _fpc.Velocity;
            float speed = new Vector2(v.x, v.z).magnitude;
            string state = _fpc.IsSliding ? "SLIDE"
                         : _fpc.IsCrouching ? "crouch"
                         : _fpc.IsSprinting ? "sprint"
                         : "walk";
            return string.Format("move: {0:F1} m/s  {1}  {2}", speed, state, _fpc.IsGrounded ? "ground" : "AIR");
        }

        void OnGUI()
        {
            if (!showHud) return;

            var sb = new StringBuilder();
            var layout = builder != null ? builder.CurrentLayout : null;

            if (layout == null)
            {
                sb.AppendLine("no level built yet");
            }
            else
            {
                CountContents(layout, out int lights, out int props);
                sb.AppendLine("seed: " + layout.Seed);
                sb.AppendLine("rooms: " + layout.Rooms.Count + "  corridors: " + layout.Corridors.Count);
                sb.AppendLine("lights: " + lights + "  props: " + props);

                var spawn = layout.SpawnRoom;
                sb.AppendLine("spawn room: " + (spawn != null ? spawn.Id.ToString() : "-"));

                sb.Append("key rooms: ");
                bool first = true;
                foreach (var kr in layout.KeyRooms)
                {
                    if (!first) sb.Append(", ");
                    sb.Append(kr.Id);
                    first = false;
                }
                if (first) sb.Append("-");
                sb.AppendLine();

                // v3: the template and the door count are what you check when a prefab room or a
                // single-entrance spawn room looks wrong, so they sit next to the room id in the HUD.
                int roomId = CurrentRoomId();
                if (roomId >= 0 && roomId < layout.Rooms.Count)
                {
                    var room = layout.Rooms[roomId];
                    string template = string.IsNullOrEmpty(room.TemplateName) ? "-" : room.TemplateName;
                    sb.AppendLine("player room: " + roomId + " (" + room.Type + ")");
                    sb.AppendLine("  template: " + template + "  doors: " + room.DoorCount);
                }
                else
                {
                    sb.AppendLine("player room: - (-)");
                    sb.AppendLine("  template: -  doors: -");
                }
            }

            sb.AppendLine(MovementLine());
            sb.AppendLine("lights debug: " + (lightsDebug ? "ON" : "off"));

            sb.AppendLine();
            sb.AppendLine("[R] rebuild  [N] seed+1  [P] seed-1");
            sb.AppendLine("[L] flat lights  [F1] hud  [Esc] cursor");
            sb.AppendLine("[Tab] overhead view (WASD pan, scroll zoom, Home refit)");
            sb.AppendLine("WASD  [Shift] sprint  [Space] jump");
            sb.AppendLine("[Ctrl/C] crouch (sprint+crouch = slide)");

            GUI.Box(new Rect(8f, 8f, 320f, 270f), GUIContent.none);
            GUI.Label(new Rect(16f, 14f, 304f, 258f), sb.ToString());
        }
    }
}
