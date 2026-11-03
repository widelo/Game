// The core loop: collect every key, unlock the exit, walk out, next floor.
//
// On every LevelBuilder.OnLevelBuilt (first build, R/N/P debug rebuilds, and the escape -> next floor build)
// this destroys the previous objective objects and spawns:
//  * one KeyPickup per key room, at builder.KeySpawnPoints (key room centres) raised to 0.9 m on a stand;
//  * one ExitGate at the exit room's doorway (builder.ExitDoorwayPoint, side from builder.RoomDoorSide).
//
// GATE PLACEMENT (deliberate deviation from "1 m inside the room"): the panel sits 1 m OUT into the exit
// corridor, not 1 m into the room. The corridor is exactly 3.2 m wide between its walls, so a 3.2 m panel
// seals it; 1 m inside a room the space is as wide as the room (and End Room.prefab's doorway is 4.55 m), so
// a 3.2 m panel would be trivially walked around. The player still walks right up to it (from the corridor),
// and the escape volume is just inside the room, past the doorway. Tune with gateOffsetIntoCorridor.
//
// When the layout has no exit room (LevelGenSettings.ExitRoom off) the old GDD rule applies: the gate stands
// at the spawn room's doorway as a NON-blocking marker (a blocking panel there would lock the player in at
// the start), and walking back into the spawn room with every key escapes.
//
// Everything spawned lives under "<Level>/Objectives", so a rebuild that destroys the level also takes the
// objective objects with it; they are additionally destroyed explicitly so edit-mode builds stay clean.
using System;
using System.Collections;
using System.Collections.Generic;
using LevelGen.Core;
using LevelGen.Unity;
using UnityEngine;

namespace Game.Objectives
{
    [AddComponentMenu("Game/Objective Tracker")]
    public sealed class ObjectiveTracker : MonoBehaviour
    {
        [Tooltip("Level whose OnLevelBuilt drives the objectives. Found automatically if empty.")]
        public LevelBuilder builder;

        [Tooltip("Player root (the CharacterController object). Found automatically if empty.")]
        public Transform player;

        [Tooltip("Key model. Empty = a glowing yellow 0.35 m primitive with a small point light.")]
        public GameObject keyPrefab;

        [Tooltip("Padlock model hung on the exit gate. Empty = a red primitive box.")]
        public GameObject padlockPrefab;

        [Tooltip("Horizontal distance (m) at which the player picks a key up.")]
        public float keyPickupRadius = 1.2f;

        [Tooltip("After escaping, build seed + 1 (the next floor) automatically.")]
        public bool advanceToNextLevelOnEscape = true;

        [Tooltip("Seconds between escaping and the next floor being built.")]
        public float nextLevelDelay = 1.5f;

        [Tooltip("How far the gate panel stands out into the exit corridor from the room's doorway (m).")]
        public float gateOffsetIntoCorridor = 1.0f;

        [Tooltip("Read from the layout's key room count at every build. Display only.")]
        [SerializeField] int totalKeysRequired;

        public const float KeyHeight = 0.9f;
        public const float KeySize = 0.35f;
        const float TriggerDistanceIntoRoom = 1.5f;   // centre of the escape volume, from the doorway, into the room
        const float TriggerDepth = 1.5f;
        const float PlayerDropHeight = 1.1f;          // matches LevelDebugController.spawnHeight

        // ------------------------------------------------------------------ public state

        public int KeysCollected { get; private set; }
        public int KeysRequired => totalKeysRequired;
        public bool AllKeysCollected => KeysCollected >= KeysRequired;
        public bool Escaped { get; private set; }
        /// <summary>1-based floor number; increments on every escape.</summary>
        public int Floor { get; private set; } = 1;
        /// <summary>Time.time of the last escape; -infinity if never. The HUD flashes off this.</summary>
        public float LastEscapeTime { get; private set; } = float.NegativeInfinity;

        public ExitGate Gate { get; private set; }
        public IReadOnlyList<KeyPickup> Keys => _keys;
        /// <summary>False when the layout had no exit room and the gate stands at the spawn doorway instead.</summary>
        public bool ExitIsDedicatedRoom { get; private set; }

        /// <summary>(collected, required) after every pickup.</summary>
        public event Action<int, int> OnKeyCollected;
        /// <summary>The NEW floor number, raised the moment the player walks out.</summary>
        public event Action<int> OnEscaped;

        // ------------------------------------------------------------------ internals

        readonly List<KeyPickup> _keys = new List<KeyPickup>();
        GameObject _root;
        bool _subscribed;
        Coroutine _advance;

        Material _keyMat, _standMat, _gateLockedMat, _gateOpenMat, _padlockMat;

        void OnEnable() { Subscribe(); }

        void Start()
        {
            Subscribe();
            // The builder may have built before we subscribed (script order); catch up.
            if (builder != null && builder.CurrentLayout != null && _root == null) Spawn(builder.CurrentLayout);
        }

        void OnDisable()
        {
            if (_subscribed && builder != null) builder.OnLevelBuilt -= HandleLevelBuilt;
            _subscribed = false;
        }

        void OnDestroy()
        {
            ClearObjectives();
            DestroyMat(ref _keyMat);
            DestroyMat(ref _standMat);
            DestroyMat(ref _gateLockedMat);
            DestroyMat(ref _gateOpenMat);
            DestroyMat(ref _padlockMat);
        }

        void Subscribe()
        {
            if (_subscribed) return;
            if (builder == null) builder = FindAnyObjectByType<LevelBuilder>();
            ResolvePlayer();
            if (builder == null) return;
            builder.OnLevelBuilt += HandleLevelBuilt;
            _subscribed = true;
        }

        void ResolvePlayer()
        {
            if (player != null) return;
            var fpc = FindAnyObjectByType<Game.Player.FirstPersonController>();
            if (fpc != null) player = fpc.transform;
        }

        void HandleLevelBuilt(LevelLayout layout) { Spawn(layout); }

        /// <summary>
        /// Resets per-floor state and the floor counter to 1, and respawns the objectives for the level
        /// currently built (if any). Note Unity also calls Reset() when the component is added in the editor;
        /// with nothing built that only clears the counters.
        /// </summary>
        public void Reset()
        {
            Floor = 1;
            LastEscapeTime = float.NegativeInfinity;
            if (_advance != null) { StopCoroutine(_advance); _advance = null; }
            if (builder != null && builder.CurrentLayout != null && builder.LevelRoot != null) Spawn(builder.CurrentLayout);
            else { KeysCollected = 0; Escaped = false; }
        }

        /// <summary>
        /// Destroy the previous objectives and spawn keys + exit gate for <paramref name="layout"/>. Public so the
        /// edit-mode verifier can call it (OnLevelBuilt subscriptions made in OnEnable/Start do not exist there).
        /// </summary>
        public void Spawn(LevelLayout layout)
        {
            ClearObjectives();
            KeysCollected = 0;
            Escaped = false;
            totalKeysRequired = 0;
            if (builder == null || layout == null) return;
            ResolvePlayer();
            EnsureMaterials();

            _root = new GameObject("Objectives");
            // Parent under the tracker, NOT under the level root: anything under "Level" is collected by the
            // NavMesh bake, and a gate collider there would carve the doorway out of the NavMesh and seal the
            // exit room for the enemy and the verifier. The tracker clears these objects itself on every build.
            _root.transform.SetParent(transform, false);

            // ---- keys ----
            var keyRooms = new List<Room>(layout.KeyRooms);
            IReadOnlyList<Vector3> points = builder.KeySpawnPoints;
            for (int i = 0; i < keyRooms.Count; i++)
            {
                Vector3 floorPoint = points != null && i < points.Count ? points[i] : builder.RoomWorldCenter(keyRooms[i].Id);
                SpawnKey(keyRooms[i].Id, floorPoint, i);
            }
            totalKeysRequired = _keys.Count;

            // ---- exit gate ----
            SpawnGate(layout);

            if (AllKeysCollected && Gate != null) { Gate.Unlock(); HideAuthoredLocks(layout); } // no key rooms: open from the start
        }

        void SpawnKey(int roomId, Vector3 floorPoint, int index)
        {
            var go = new GameObject($"Key_{index}_room{roomId}");
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(floorPoint.x, KeyHeight, floorPoint.z);

            // Stand: a slim plinth from the floor to just under the key. No collider (the NavMesh is already
            // baked, and a collider here would block a player teleported onto the key point).
            const float standHeight = 0.6f; // floor to 0.6 m; the key floats above it at KeyHeight
            GenPrimitives.CylPart(go.transform, "Stand", _standMat, new Vector3(0f, standHeight * 0.5f - KeyHeight, 0f),
                                  0.3f, standHeight);

            Transform visual;
            if (keyPrefab != null)
            {
                GameObject m = Instantiate(keyPrefab, go.transform);
                m.name = keyPrefab.name;
                m.transform.localPosition = Vector3.zero;
                LevelGen.Unity.ModelFit.FitToBox(m, Vector3.one * KeySize, true, false, true);
                foreach (var c in m.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                visual = m.transform;
            }
            else
            {
                var spin = new GameObject("Visual");
                spin.transform.SetParent(go.transform, false);
                // A tilted cube reads as a "gem" from any angle.
                GenPrimitives.Part(spin.transform, "KeyGem", GenPrimitives.Box, _keyMat, Vector3.zero,
                                   Vector3.one * KeySize, Quaternion.Euler(45f, 0f, 45f));
                visual = spin.transform;
            }

            var lightGo = new GameObject("KeyGlow");
            lightGo.transform.SetParent(go.transform, false);
            var light = lightGo.AddComponent<Light>();
            light.type = UnityEngine.LightType.Point;
            light.range = 3f;
            light.intensity = 1.5f;
            light.color = new Color(1f, 0.85f, 0.3f);
            light.shadows = LightShadows.None;

            var key = go.AddComponent<KeyPickup>();
            key.Initialise(this, player, roomId, keyPickupRadius, visual);
            _keys.Add(key);
        }

        void SpawnGate(LevelLayout layout)
        {
            Room exit = layout.ExitRoom;
            ExitIsDedicatedRoom = exit != null;
            Room room = exit ?? layout.SpawnRoom;
            if (room == null || room.CorridorIds == null || room.CorridorIds.Count == 0)
            {
                UnityEngine.Debug.LogWarning("[ObjectiveTracker] no exit/spawn room with a corridor - no exit gate this floor");
                return;
            }

            int corridorId = room.CorridorIds[0];
            Vector3 doorway = exit != null ? builder.ExitDoorwayPoint : builder.RoomDoorwayWorld(room.Id, corridorId);
            if (exit != null && doorway == Vector3.zero) doorway = builder.RoomDoorwayWorld(room.Id, corridorId);
            Vector3 outward = SideToVector(builder.RoomDoorSide(room.Id, corridorId));

            var go = new GameObject(ExitIsDedicatedRoom ? "ExitGate" : "ExitGate_Spawn");
            go.transform.SetParent(_root.transform, false);
            go.transform.position = new Vector3(doorway.x, 0f, doorway.z) + outward * gateOffsetIntoCorridor;
            // +Z faces into the corridor: the padlock side is the side the player approaches from.
            go.transform.rotation = Quaternion.LookRotation(outward, Vector3.up);

            Gate = go.AddComponent<ExitGate>();
            Gate.Build(this, padlockPrefab, _gateLockedMat, _gateOpenMat, _padlockMat,
                       gateOffsetIntoCorridor + TriggerDistanceIntoRoom, TriggerDepth, blocks: ExitIsDedicatedRoom);
        }

        public static Vector3 SideToVector(Cardinal side)
        {
            switch (side)
            {
                case Cardinal.North: return Vector3.forward;
                case Cardinal.East:  return Vector3.right;
                case Cardinal.South: return Vector3.back;
                default:             return Vector3.left;
            }
        }

        // ------------------------------------------------------------------ callbacks from the pieces

        internal void NotifyKeyCollected(KeyPickup key)
        {
            if (!_keys.Contains(key)) return;
            KeysCollected = Mathf.Min(KeysCollected + 1, KeysRequired);
            OnKeyCollected?.Invoke(KeysCollected, KeysRequired);
            if (AllKeysCollected && Gate != null) { Gate.Unlock(); HideAuthoredLocks(builder != null ? builder.CurrentLayout : null); }
        }

        internal void NotifyEscaped(ExitGate gate)
        {
            if (Escaped || gate != Gate || !AllKeysCollected) return;
            Escaped = true;
            Floor++;
            LastEscapeTime = Time.time;
            OnEscaped?.Invoke(Floor);
            if (advanceToNextLevelOnEscape && Application.isPlaying && builder != null && builder.CurrentLayout != null)
            {
                if (_advance != null) StopCoroutine(_advance);
                _advance = StartCoroutine(AdvanceAfterDelay(builder.CurrentLayout.Seed + 1));
            }
        }

        IEnumerator AdvanceAfterDelay(int nextSeed)
        {
            yield return new WaitForSeconds(nextLevelDelay);
            _advance = null;
            builder.Build(nextSeed);   // OnLevelBuilt -> Spawn() respawns the objectives for the new floor

            // LevelDebugController teleports the player on OnLevelBuilt. We cannot inspect another component's
            // event subscription, so check the outcome instead: if the player is not at the new spawn, move it
            // ourselves (same CharacterController-off pattern as EnemyAgent / LevelDebugController).
            if (player != null)
            {
                Vector3 spawn = builder.PlayerSpawnPoint;
                Vector2 d = new Vector2(player.position.x - spawn.x, player.position.z - spawn.z);
                if (d.magnitude > 2f) TeleportPlayer(spawn + Vector3.up * PlayerDropHeight);
            }
        }

        public void TeleportPlayer(Vector3 position)
        {
            if (player == null) return;
            var cc = player.GetComponent<CharacterController>();
            bool wasEnabled = cc != null && cc.enabled;
            if (cc != null) cc.enabled = false;
            player.position = position;
            if (cc != null) cc.enabled = wasEnabled;
        }

        // ------------------------------------------------------------------ cleanup / materials

        void ClearObjectives()
        {
            _keys.Clear();
            Gate = null;
            if (_root == null) return;
            if (Application.isPlaying) Destroy(_root);
            else DestroyImmediate(_root);
            _root = null;
        }

        void EnsureMaterials()
        {
            if (_keyMat == null)
            {
                _keyMat = LevelMaterials.Make("ObjKey", new Color(1f, 0.85f, 0.15f), 0.8f);
                _keyMat.EnableKeyword("_EMISSION");
                if (_keyMat.HasProperty("_EmissionColor")) _keyMat.SetColor("_EmissionColor", new Color(1f, 0.8f, 0.2f) * 2.5f);
            }
            if (_standMat == null) _standMat = LevelMaterials.Make("ObjKeyStand", new Color(0.25f, 0.25f, 0.27f), 0.3f);
            if (_gateLockedMat == null) _gateLockedMat = LevelMaterials.Make("ObjGateLocked", new Color(0.35f, 0.08f, 0.08f), 0.4f);
            if (_padlockMat == null) _padlockMat = LevelMaterials.Make("ObjPadlock", new Color(0.85f, 0.1f, 0.1f), 0.6f);
            if (_gateOpenMat == null)
            {
                _gateOpenMat = LevelMaterials.Make("ObjGateOpen", new Color(0.2f, 1f, 0.35f, 0.25f), 0.4f);
                MakeTransparent(_gateOpenMat);
                _gateOpenMat.EnableKeyword("_EMISSION");
                if (_gateOpenMat.HasProperty("_EmissionColor")) _gateOpenMat.SetColor("_EmissionColor", new Color(0.1f, 0.6f, 0.2f));
            }
        }

        /// <summary>URP Lit switched to alpha-blended transparent (the inspector's Surface Type = Transparent).</summary>
        static void MakeTransparent(Material m)
        {
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        static void DestroyMat(ref Material m)
        {
            if (m == null) return;
            if (Application.isPlaying) Destroy(m);
            else DestroyImmediate(m);
            m = null;
        }
        /// <summary>
        /// A hand-built exit room may carry its own padlock/chain props across the doorway. They are the visual
        /// "locked" state, so they go away together with the gate's padlock once every key is collected.
        /// </summary>
        void HideAuthoredLocks(LevelLayout layout)
        {
            if (layout == null || layout.ExitRoom == null || builder == null) return;
            if (!builder.RoomObjects.TryGetValue(layout.ExitRoom.Id, out GameObject roomObject) || roomObject == null) return;
            foreach (var t in roomObject.GetComponentsInChildren<Transform>(true))
                if (t != roomObject.transform && t.name.IndexOf("padlock", System.StringComparison.OrdinalIgnoreCase) >= 0 && t.parent != null
                    && t.parent.name.IndexOf("padlock", System.StringComparison.OrdinalIgnoreCase) < 0)
                    t.gameObject.SetActive(false);
        }

    }
}
