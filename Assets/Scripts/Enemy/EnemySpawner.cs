// Spawns the enemies after every level build (OnLevelBuilt fires on each rebuild, NavMesh already baked).
// Enemies are parented under this spawner, NOT under LevelRoot, so they never feed the next NavMesh bake
// (the bake collects LevelRoot's children) and are explicitly destroyed before each respawn.
using System.Collections.Generic;
using Game.Player;
using LevelGen.Core;
using LevelGen.Unity;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Enemy
{
    public sealed class EnemySpawner : MonoBehaviour
    {
        [Tooltip("Level whose OnLevelBuilt drives spawning. Found automatically if empty.")]
        public LevelBuilder builder;
        [Tooltip("Player root. Found via FirstPersonController if empty.")]
        public Transform player;
        [Tooltip("Visual model. Null = a primitive capsule so the enemy still exists.")]
        public GameObject modelPrefab;
        public float modelHeight = 2.8f;
        [Tooltip("Applied to every renderer of the model. Null = a dark red URP Lit with a faint red glow.")]
        public Material modelMaterial;
        public bool spawnOnLevelBuilt = true;
        [Min(0)] public int enemyCount = 1;

        const float AgentRadius = 0.5f;
        const float SampleRadius = 4f;

        readonly List<EnemyAgent> _enemies = new List<EnemyAgent>();
        public IReadOnlyList<EnemyAgent> Enemies => _enemies;

        Material _defaultMaterial;
        static Mesh _capsuleMesh;
        bool _subscribed;

        void Start()
        {
            if (builder == null) builder = FindAnyObjectByType<LevelBuilder>();
            if (player == null)
            {
                var fpc = FindAnyObjectByType<FirstPersonController>();
                if (fpc != null) player = fpc.transform;
            }
            if (builder == null) { UnityEngine.Debug.LogWarning("[EnemySpawner] no LevelBuilder; nothing to do."); return; }

            builder.OnLevelBuilt += HandleLevelBuilt;
            _subscribed = true;
            // The builder builds in its own Start, which may already have run.
            if (spawnOnLevelBuilt && builder.CurrentLayout != null) Spawn(builder.CurrentLayout);
        }

        void OnDestroy()
        {
            if (_subscribed && builder != null) builder.OnLevelBuilt -= HandleLevelBuilt;
            if (_defaultMaterial != null) SafeDestroy(_defaultMaterial);
        }

        void HandleLevelBuilt(LevelLayout layout)
        {
            if (spawnOnLevelBuilt) Spawn(layout);
        }

        /// <summary>Destroys previous enemies and spawns <see cref="enemyCount"/> new ones, farthest rooms first.</summary>
        public void Spawn(LevelLayout layout)
        {
            Clear();
            if (layout == null || layout.Rooms.Count < 2 || enemyCount <= 0) return;

            List<int> rooms = RoomsFarthestFirst(layout);
            // Only rooms the enemy can actually walk out of. A prefab room whose walls we did not generate may
            // have no doorway yet (sealed interior), and furniture or a bad bake could in theory island a room:
            // require a complete NavMesh path from the room centre to the player's spawn point.
            Vector3 target = builder != null ? builder.PlayerSpawnPoint : Vector3.zero;
            rooms.RemoveAll(id => !RoomIsReachable(layout, id, target));
            if (rooms.Count == 0)
            {
                Debug.LogWarning("[EnemySpawner] no reachable room to spawn in; skipping enemies this level");
                return;
            }
            for (int i = 0; i < enemyCount; i++) _enemies.Add(CreateEnemy(i, layout, rooms[i % rooms.Count]));
        }

        public void Clear()
        {
            foreach (var e in _enemies) if (e != null) SafeDestroy(e.gameObject);
            _enemies.Clear();
        }

        /// <summary>THE brain line. Esteban: return your IEnemyBrain here.</summary>
        IEnemyBrain CreateBrain(int enemyIndex) => new StalkerBrain();

        bool RoomIsReachable(LevelLayout layout, int roomId, Vector3 target)
        {
            var room = layout.Rooms[roomId];
            if (room.Template != null && !room.Template.GenerateWalls) return false; // author-owned walls: may be sealed
            Vector3 from = builder.RoomWorldCenter(roomId);
            if (!NavMesh.SamplePosition(from, out NavMeshHit a, SampleRadius, NavMesh.AllAreas)) return false;
            if (!NavMesh.SamplePosition(target, out NavMeshHit b, SampleRadius, NavMesh.AllAreas)) return false;
            var path = new NavMeshPath();
            return NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path)
                   && path.status == NavMeshPathStatus.PathComplete;
        }

        /// <summary>Reachable non-spawn rooms sorted by BFS distance from the spawn, descending; ties shuffled.</summary>
        static List<int> RoomsFarthestFirst(LevelLayout layout)
        {
            int spawnId = layout.SpawnRoom != null ? layout.SpawnRoom.Id : 0;
            int[] d = layout.BfsDistances(spawnId);
            var ids = new List<int>();
            for (int i = 0; i < d.Length; i++) if (i != spawnId && d[i] > 0) ids.Add(i);
            for (int i = ids.Count - 1; i > 0; i--) // shuffle first so the stable-order sort breaks ties randomly
            {
                int j = Random.Range(0, i + 1);
                (ids[i], ids[j]) = (ids[j], ids[i]);
            }
            var tiebreak = new Dictionary<int, int>();
            for (int i = 0; i < ids.Count; i++) tiebreak[ids[i]] = i;
            ids.Sort((a, b) => d[a] != d[b] ? d[b].CompareTo(d[a]) : tiebreak[a].CompareTo(tiebreak[b]));
            return ids;
        }

        EnemyAgent CreateEnemy(int index, LevelLayout layout, int roomId)
        {
            Vector3 pos = builder.RoomWorldCenter(roomId);
            if (NavMesh.SamplePosition(pos, out NavMeshHit hit, SampleRadius, NavMesh.AllAreas)) pos = hit.position;
            else UnityEngine.Debug.LogWarning($"[EnemySpawner] room {roomId} centre is not near the NavMesh.");

            var go = new GameObject("Enemy_" + index);
            go.transform.SetParent(transform, false);
            go.transform.position = pos;

            GameObject model = modelPrefab != null ? Instantiate(modelPrefab, go.transform, false) : BuildCapsule(go.transform);
            model.name = "Model";
            ModelFit.FitToHeight(model, modelHeight);
            // The root capsule is the enemy's only collider: model colliders would block its own sight line.
            foreach (var c in model.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            ApplyMaterial(model, modelMaterial != null ? modelMaterial : DefaultMaterial());

            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.isTrigger = false;
            capsule.radius = AgentRadius;
            capsule.height = modelHeight;
            capsule.center = new Vector3(0f, modelHeight * 0.5f, 0f);

            var agent = go.AddComponent<NavMeshAgent>();
            agent.agentTypeID = builder.Profile.agentTypeId;
            agent.radius = AgentRadius;
            agent.height = modelHeight;
            agent.baseOffset = 0f;
            agent.Warp(pos);

            var enemy = go.AddComponent<EnemyAgent>();
            enemy.Initialise(CreateBrain(index), layout, builder, player, pos);
            return enemy;
        }

        static void ApplyMaterial(GameObject model, Material mat)
        {
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
            }
        }

        Material DefaultMaterial()
        {
            if (_defaultMaterial != null) return _defaultMaterial;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            _defaultMaterial = new Material(shader) { name = "EnemyPlaceholder (runtime)" };
            var red = new Color(0.25f, 0.02f, 0.02f);
            if (_defaultMaterial.HasProperty("_BaseColor")) _defaultMaterial.SetColor("_BaseColor", red);
            if (_defaultMaterial.HasProperty("_Color")) _defaultMaterial.SetColor("_Color", red);
            if (_defaultMaterial.HasProperty("_EmissionColor"))
            {
                _defaultMaterial.EnableKeyword("_EMISSION");
                _defaultMaterial.SetColor("_EmissionColor", new Color(0.35f, 0.02f, 0.02f));
            }
            return _defaultMaterial;
        }

        /// <summary>Capsule mesh without a collider: borrow the primitive's mesh, throw the primitive away.</summary>
        static GameObject BuildCapsule(Transform parent)
        {
            if (_capsuleMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                _capsuleMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediate(tmp);
            }
            var go = new GameObject("Capsule");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = _capsuleMesh;
            go.AddComponent<MeshRenderer>();
            return go;
        }
        /// <summary>Destroy that also works when invoked from edit-mode tooling (the headless verifier).</summary>
        static void SafeDestroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

    }
}
