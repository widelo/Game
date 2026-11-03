using System.Collections;
using System.Collections.Generic;
using Game.Player;
using LevelGen.Core;
using LevelGen.Unity;
using UnityEngine;

namespace CS462.StalkerAI
{
    /// <summary>Opt-in integration with LevelBuilder; owns one shared graph and the A* enemies.</summary>
    [DefaultExecutionOrder(100), DisallowMultipleComponent]
    public sealed class AStarDirector : MonoBehaviour
    {
        [Header("Existing project references")]
        public LevelBuilder builder;
        public Transform player;
        public GameObject modelPrefab;
        public Material modelMaterial;
        [Min(0.5f)] public float modelHeight = 2.8f;
        [Range(1, 4)] public int enemyCount = 1;
        public int behaviorSeed = 462;
        [Tooltip("Optional authored patrol transforms. Empty uses physically reachable room centres.")]
        public Transform[] patrolLocations;

        public NavigationSettings navigation = new NavigationSettings();
        public StalkerSettings behavior = new StalkerSettings();
        [Header("Sample damage integration")]
        public bool addSampleHealth = true;
        public bool addFootstepEmitter = true;
        public bool respawnOnDefeat = true;
        [Min(0f)] public float respawnProtection = 5f;
        public bool showHud = true;

        public AStarNavigation Navigation { get; private set; }
        public IReadOnlyList<StalkerBoss> Bosses => bosses;
        public StalkerTarget Target { get; private set; }
        public string Status { get; private set; } = "Waiting for level";

        readonly List<StalkerBoss> bosses = new List<StalkerBoss>();
        readonly List<Vector3> patrol = new List<Vector3>();
        Coroutine buildRoutine;
        LevelLayout requestedLayout;
        LevelBuilder subscribedBuilder;
        PlayerNoiseEmitter ownedNoise;
        StalkerTarget ownedTarget;
        Material fallbackMaterial;
        bool started;

        void OnEnable()
        {
            ConnectBuilder();
            if (started && builder != null && builder.CurrentLayout != null) ScheduleBuild(builder.CurrentLayout);
        }

        void Start()
        {
            started = true; ConnectBuilder();
            if (builder == null) { Fail("No LevelBuilder. Assign the existing scene builder."); return; }
            if (builder.CurrentLayout != null) ScheduleBuild(builder.CurrentLayout);
        }

        void ConnectBuilder()
        {
            if (builder == null) builder = FindAnyObjectByType<LevelBuilder>();
            if (subscribedBuilder == builder) return;
            if (subscribedBuilder != null) subscribedBuilder.OnLevelBuilt -= ScheduleBuild;
            subscribedBuilder = builder;
            if (subscribedBuilder != null) subscribedBuilder.OnLevelBuilt += ScheduleBuild;
        }

        void OnDisable()
        {
            if (subscribedBuilder != null) subscribedBuilder.OnLevelBuilt -= ScheduleBuild;
            subscribedBuilder = null;
            if (buildRoutine != null) StopCoroutine(buildRoutine);
            buildRoutine = null; requestedLayout = null; ClearBosses(); Navigation = null;
            if (Target != null) Target.Defeated -= OnDefeated;
            if (ownedNoise != null) { ownedNoise.enabled = false; Destroy(ownedNoise); }
            if (ownedTarget != null) Destroy(ownedTarget);
            ownedNoise = null; ownedTarget = null; Target = null;
            if (fallbackMaterial != null) Destroy(fallbackMaterial);
            fallbackMaterial = null;
        }

        void OnValidate()
        {
            navigation.Validate(); behavior.Validate();
            enemyCount = Mathf.Clamp(enemyCount, 1, 4); modelHeight = Mathf.Max(0.5f, modelHeight);
        }

        void ScheduleBuild(LevelLayout layout)
        {
            if (!isActiveAndEnabled || layout == null) return;
            if (requestedLayout == layout && buildRoutine != null) return;
            if (buildRoutine != null) StopCoroutine(buildRoutine);
            ClearBosses(); Navigation = null; requestedLayout = layout;
            buildRoutine = StartCoroutine(BuildAndSpawn(layout));
        }

        [ContextMenu("Rebuild A Star Navigation (Play Mode)")]
        public void RebuildNavigation()
        {
            if (Application.isPlaying && builder != null && builder.CurrentLayout != null)
            { requestedLayout = null; ScheduleBuild(builder.CurrentLayout); }
        }

        IEnumerator BuildAndSpawn(LevelLayout layout)
        {
            Status = "Waiting for old geometry to be destroyed";
            // LevelBuilder.ClearLevel uses deferred Destroy in Play Mode. Never scan both old and new maps.
            yield return null;
            navigation.Validate(); behavior.Validate();
            if (builder == null || builder.CurrentLayout != layout) yield break;
            if (builder.transform.position.sqrMagnitude > 0.0001f
                || Quaternion.Angle(builder.transform.rotation, Quaternion.identity) > 0.01f
                || (builder.transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f
                || (transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
            { Fail("Use an identity LevelBuilder and unit-scaled director, as required by this project's level helpers."); buildRoutine = null; yield break; }
            if (!BindPlayer()) { buildRoutine = null; yield break; }
            Physics.SyncTransforms();
            Navigation = new AStarNavigation(navigation, player, transform);
            Status = "Sampling A* navigation";
            yield return Navigation.Build(builder.LevelRoot);
            if (!Navigation.Ready) { Fail(Navigation.Error); buildRoutine = null; yield break; }
            if (!Navigation.TryNode(builder.PlayerSpawnPoint, navigation.endpointSnapDistance, out int spawnNode))
            { Fail("Player spawn has no clear supported node. Inspect spawn floor/colliders and capsule dimensions."); buildRoutine = null; yield break; }
            int component = Navigation.Components[spawnNode];
            patrol.Clear(); var candidateSpawns = new List<Vector3>();
            var roomIds = new List<int>();
            int spawnId = layout.SpawnRoom != null ? layout.SpawnRoom.Id : 0;
            int[] distances = layout.BfsDistances(spawnId);
            for (int i = 0; i < layout.Rooms.Count; i++) roomIds.Add(i);
            roomIds.Sort((a, b) => distances[b].CompareTo(distances[a]));
            foreach (int roomId in roomIds)
            {
                if (!Navigation.TryNode(builder.RoomWorldCenter(roomId), navigation.endpointSnapDistance, out int node, component)) continue;
                Vector3 point = Navigation.World(node); patrol.Add(point);
                if (roomId != spawnId && distances[roomId] > 0
                    && Vector3.Distance(point, player.position) > behavior.preferredStalkDistance)
                    candidateSpawns.Add(point);
            }
            if (patrolLocations != null && patrolLocations.Length > 0)
            {
                patrol.Clear();
                foreach (Transform marker in patrolLocations)
                    if (marker != null && Navigation.TryNode(marker.position, navigation.endpointSnapDistance, out int node, component))
                        patrol.Add(Navigation.World(node));
                if (patrol.Count == 0) patrol.Add(Navigation.World(spawnNode));
            }
            int count = Mathf.Min(enemyCount, candidateSpawns.Count);
            for (int i = 0; i < count; i++) SpawnBoss(candidateSpawns[i], i);
            if (Target != null) Target.ResetHealth(respawnProtection);
            Status = $"{Navigation.WalkableCount} walkable nodes; {bosses.Count} A* stalker(s)";
            if (count < enemyCount) UnityEngine.Debug.LogWarning("[A*] Fewer safe, connected non-spawn rooms than requested enemies. See grid Gizmos.", this);
            UnityEngine.Debug.Log("[A*] " + Status, this);
            buildRoutine = null;
        }

        bool BindPlayer()
        {
            if (player == null)
            {
                var fpc = FindAnyObjectByType<FirstPersonController>();
                if (fpc != null) player = fpc.transform;
            }
            if (player == null) { Fail("No player root. Assign Game.Player.FirstPersonController's transform."); return false; }
            if (Target != null) Target.Defeated -= OnDefeated;
            Target = player.GetComponent<StalkerTarget>();
            if (Target == null && addSampleHealth) { ownedTarget = player.gameObject.AddComponent<StalkerTarget>(); Target = ownedTarget; }
            if (Target != null) Target.Defeated += OnDefeated;
            if (addFootstepEmitter && player.GetComponent<PlayerNoiseEmitter>() == null
                && player.GetComponent<FirstPersonController>() != null)
                ownedNoise = player.gameObject.AddComponent<PlayerNoiseEmitter>();
            if (Target == null) UnityEngine.Debug.LogWarning("[A*] No StalkerTarget: perception/movement work, but attacks apply no damage.", this);
            return true;
        }

        void SpawnBoss(Vector3 position, int index)
        {
            var root = new GameObject("AStarStalker_" + index);
            root.transform.SetParent(transform, false); root.transform.position = position + Vector3.up * 0.03f;
            GameObject model = modelPrefab != null ? Instantiate(modelPrefab, root.transform, false)
                                                   : GameObject.CreatePrimitive(PrimitiveType.Capsule);
            model.name = "Model"; model.transform.SetParent(root.transform, false);
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (Rigidbody body in model.GetComponentsInChildren<Rigidbody>(true))
            { body.isKinematic = true; body.detectCollisions = false; }
            foreach (Animator animator in model.GetComponentsInChildren<Animator>(true)) animator.applyRootMotion = false;
            ModelFit.FitToHeight(model, modelHeight);
            Material material = modelMaterial;
            if (modelPrefab == null && material == null) material = FallbackMaterial();
            if (material != null)
            {
                foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] slots = renderer.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++) slots[i] = material;
                    renderer.sharedMaterials = slots;
                }
            }
            root.AddComponent<AStarMotor>();
            StalkerBoss boss = root.AddComponent<StalkerBoss>();
            boss.Initialise(Navigation, player, Target, patrol, behavior, unchecked(behaviorSeed + index * 7919));
            bosses.Add(boss);
        }

        Material FallbackMaterial()
        {
            if (fallbackMaterial != null) return fallbackMaterial;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null;
            fallbackMaterial = new Material(shader) { name = "AStarStalker (runtime)" };
            if (fallbackMaterial.HasProperty("_BaseColor")) fallbackMaterial.SetColor("_BaseColor", new Color(0.22f, 0.03f, 0.04f));
            return fallbackMaterial;
        }

        void OnDefeated(StalkerTarget damaged)
        {
            foreach (StalkerBoss boss in bosses) if (boss != null) boss.ForceDisengage();
            if (!respawnOnDefeat || builder == null || player == null) return;
            CharacterController cc = player.GetComponent<CharacterController>();
            bool enabledBefore = cc != null && cc.enabled;
            if (cc != null) cc.enabled = false;
            player.position = builder.PlayerSpawnPoint + Vector3.up * 1.1f;
            if (cc != null) cc.enabled = enabledBefore;
            Physics.SyncTransforms();
            damaged.ResetHealth(respawnProtection);
        }

        void ClearBosses()
        {
            foreach (StalkerBoss boss in bosses)
            {
                if (boss == null) continue;
                boss.gameObject.SetActive(false); Destroy(boss.gameObject);
            }
            bosses.Clear();
        }

        void Fail(string reason) { Status = reason; UnityEngine.Debug.LogError("[A*] " + reason, this); }
        void OnDrawGizmosSelected()
        {
            if (Navigation == null) return;
            Navigation.Settings.drawGrid = navigation.drawGrid;
            Navigation.Settings.drawBlockedNodes = navigation.drawBlockedNodes;
            Navigation.Settings.maxGizmoNodes = Mathf.Max(1, navigation.maxGizmoNodes);
            Navigation.DrawGizmos();
        }

        void OnGUI()
        {
            if (!showHud) return;
            string text = Navigation != null && !Navigation.Ready
                ? $"A* grid: {Navigation.BuildProgress:P0}" : Status;
            if (Target != null) text += $"\nHealth: {Target.Health:F0}/{Target.maxHealth:F0}";
            if (bosses.Count > 0 && bosses[0] != null)
            {
                StalkerBoss boss = bosses[0];
                text += $"\n{boss.State} | Attack: {boss.LastAttack}\nExpanded: {boss.Motor.Expanded} | Path cost: {boss.Motor.GraphPathCost:F1}";
                if (boss.Motor.Failed) text += "\n" + boss.Motor.FailureReason;
                if (boss.WindingUp)
                {
                    Color previous = GUI.color; GUI.color = Color.red;
                    GUI.Box(new Rect(Screen.width * 0.5f - 120f, Screen.height * 0.65f, 240f, 35f), "ATTACK WINDUP — MOVE!");
                    GUI.color = previous;
                }
            }
            GUI.Box(new Rect(Screen.width - 360f, 8f, 352f, 125f), text);
        }
    }
}
