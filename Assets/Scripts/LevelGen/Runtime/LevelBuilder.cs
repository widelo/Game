// Scene-side driver: generate a LevelLayout from a seed, turn it into meshes + colliders + prefab rooms +
// props + lights, apply the horror render settings, bake a NavMesh, drop spawn/key markers, fire OnLevelBuilt.
// Everything it creates lives under one child named "Level", so a rebuild is a single Destroy.
//
// Place this component at the world origin with an identity transform: mesh vertices are authored in the
// layout's own coordinates (X east, Z north, y = 0) and ToWorld / RoomWorldCenter / PlayerSpawnPoint are
// pure functions of the layout, so a moved or rotated LevelBuilder would desync geometry from those helpers.
//
// v4 (team port) changes:
//  * every tunable except the SEED POLICY moved out of this component and into a LevelGenProfile asset, so
//    level tuning is no longer a scene edit. A null profile builds a transient default in code.
//  * per-room build honours the room's RoomTemplateSpec flags (GenerateFloor/Walls/Ceiling/Props/Lights) and
//    instantiates the template's room prefab when one is authored. Corridors are always fully generated.
//  * props and lights can come from teammate prefabs (see LevelGenProfile.propPrefabs / lightPrefabs);
//    anything unmapped still falls back to the generated primitives.
//  * extension points for teammates: OnRoomBuilt / OnCorridorBuilt fire per element BEFORE the NavMesh bake,
//    RoomObjects / CorridorObjects expose the holders, and LevelRoot is the single parent of everything.
//  * materials are URP Lit (see LevelMaterials).
using System;
using System.Collections.Generic;
using LevelGen.Core;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace LevelGen.Unity
{
    [AddComponentMenu("LevelGen/Level Builder")]
    public sealed class LevelBuilder : MonoBehaviour
    {
        const string LevelRootName = "Level";
        const string PropsGroupName = "Props";
        const string LightsGroupName = "Lights";

        // ------------------------------------------------------------------ inspector
        //
        // Only two things remain serialized on the component: the seed and whether to roll a new one. Both are
        // per-scene-instance decisions ("this scene starts on seed 12345"), not level-design tuning.
        [Header("Seed")]
        [SerializeField] int seed = 12345;
        [SerializeField] bool randomSeedOnStart = false;

        [Header("Profile")]
        [Tooltip("All layout / contents / material / nav settings. Create one with " +
                 "Assets > Create > CS462 > Level Gen Profile. Leave empty to build the stock level from a " +
                 "default profile created at runtime.")]
        [SerializeField] LevelGenProfile profile;

        // ------------------------------------------------------------------ public API

        /// <summary>Raised once per Build(), after geometry exists and the NavMesh bake has run.</summary>
        public event Action<LevelLayout> OnLevelBuilt;

        /// <summary>
        /// EXTENSION POINT. Raised once per room, with the room and its holder GameObject
        /// ("Room_&lt;id&gt;_&lt;TemplateName&gt;"), after that room's floor / walls / ceiling / room prefab /
        /// props / lights all exist and BEFORE the NavMesh bake. Add your own children here (keys, enemy
        /// spawners, decals, extra colliders) and they will be included in the bake. Subscribe before calling
        /// Build - the event is raised during Build, not after it.
        /// </summary>
        public event Action<Room, GameObject> OnRoomBuilt;

        /// <summary>
        /// EXTENSION POINT. Raised once per corridor, with the corridor and its holder GameObject, after the
        /// corridor's geometry / props / lights exist and BEFORE the NavMesh bake. Same contract as
        /// <see cref="OnRoomBuilt"/>.
        /// </summary>
        public event Action<Corridor, GameObject> OnCorridorBuilt;

        /// <summary>The layout backing the geometry currently in the scene, or null before the first build.</summary>
        public LevelLayout CurrentLayout { get; private set; }

        /// <summary>
        /// EXTENSION POINT. Holder GameObject per room id, for the level currently in the scene. Cleared and
        /// repopulated by every Build. Use it to attach things to a specific room after the fact.
        /// </summary>
        public IReadOnlyDictionary<int, GameObject> RoomObjects => roomObjects;

        /// <summary>EXTENSION POINT. Holder GameObject per corridor id. See <see cref="RoomObjects"/>.</summary>
        public IReadOnlyDictionary<int, GameObject> CorridorObjects => corridorObjects;

        /// <summary>
        /// EXTENSION POINT. The single "Level" transform every generated object lives under, or null before the
        /// first build. Destroying it is how a rebuild clears the level, so do not park anything you want to
        /// keep under it.
        /// </summary>
        public Transform LevelRoot { get; private set; }

        /// <summary>
        /// EXTENSION POINT. The profile this builder generates from. Assign it BEFORE calling
        /// <see cref="Build"/> / <see cref="Rebuild"/> - it is read at the start of a build and ignored after.
        /// Reading it creates and caches the transient default when no asset is assigned, so it is never null.
        /// </summary>
        /// <summary>Name of the project-wide default profile asset, expected under a Resources folder.</summary>
        public const string DefaultProfileResourceName = "DefaultLevelGenProfile";

        /// <summary>
        /// Resolution order: the profile assigned in the inspector, then the project default found via
        /// Resources.Load(DefaultProfileResourceName), then a transient in-code default so an empty scene still builds.
        /// </summary>
        public LevelGenProfile Profile
        {
            get
            {
                if (profile == null) profile = Resources.Load<LevelGenProfile>(DefaultProfileResourceName);
                if (profile == null) profile = LevelGenProfile.CreateTransientDefault();
                return profile;
            }
            set { profile = value; }
        }

        /// <summary>Spawn room centre at floor level. Vector3.zero if nothing has been built.</summary>
        public Vector3 PlayerSpawnPoint
        {
            get
            {
                if (CurrentLayout == null) return Vector3.zero;
                Room spawn = CurrentLayout.SpawnRoom;
                return spawn == null ? Vector3.zero : ToWorld(spawn.Center);
            }
        }

        /// <summary>Floor-level world centre of a room by id. Vector3.zero if the id is unknown.</summary>
        public Vector3 RoomWorldCenter(int roomId)
        {
            if (CurrentLayout == null || roomId < 0 || roomId >= CurrentLayout.Rooms.Count) return Vector3.zero;
            return ToWorld(CurrentLayout.Rooms[roomId].Center);
        }

        public static Vector3 ToWorld(Vec2 v) => new Vector3((float)v.X, 0f, (float)v.Z);

        /// <summary>Destroy the previous level, generate from <paramref name="newSeed"/>, build, bake, notify.</summary>
        public void Build(int newSeed)
        {
            seed = newSeed;
            ClearLevel();

            LevelGenProfile p = Profile;
            LevelLayout layout = LevelGenerator.Generate(newSeed, p.ToSettings());
            CurrentLayout = layout;

            float ceiling = Mathf.Max(1.5f, (float)layout.Settings.CeilingHeight);
            float wallThickness = Mathf.Max(0.01f, p.wallThickness);

            GameObject root = new GameObject(LevelRootName);
            root.transform.SetParent(transform, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            LevelRoot = root.transform;

            EnsureMaterials(p);

            // Atmosphere first: the fixtures below are only readable against a dark, foggy scene. URP honours
            // these RenderSettings (ambient + fog) just as the built-in pipeline does.
            LightRig.ApplyRenderSettings(p.ambientLight, p.fogEnabled, p.fogColor, p.fogDensity);
            if (p.killDirectionalLight) LightRig.KillDirectionalLights();

            int propCount = 0, lightCount = 0, prefabRooms = 0;

            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                Room room = layout.Rooms[i];
                GameObject holder = BuildRoom(root, layout, room, p, ceiling, wallThickness,
                                              ref propCount, ref lightCount, ref prefabRooms);
                roomObjects[room.Id] = holder;
                OnRoomBuilt?.Invoke(room, holder);
            }

            for (int i = 0; i < layout.Corridors.Count; i++)
            {
                Corridor corridor = layout.Corridors[i];
                GameObject holder = BuildCorridor(root, corridor, p, ceiling, wallThickness,
                                                  ref propCount, ref lightCount);
                corridorObjects[corridor.Id] = holder;
                OnCorridorBuilt?.Invoke(corridor, holder);
            }

            BuildMarkers(root, layout, p.markerSize);

            bool navOk = p.bakeNavMesh && BakeNavMesh(root, p.agentTypeId);

            Debug.Log($"[LevelBuilder] seed={newSeed} rooms={layout.Rooms.Count} " +
                      $"corridors={layout.Corridors.Count} props={propCount} lights={lightCount} " +
                      $"prefabRooms={prefabRooms} navmesh={(navOk ? "ok" : "none")}");

            OnLevelBuilt?.Invoke(layout);
        }

        /// <summary>Rebuild: a fresh random seed when randomSeedOnStart is set, otherwise the current seed.</summary>
        public void Rebuild()
        {
            Build(randomSeedOnStart ? UnityEngine.Random.Range(int.MinValue, int.MaxValue) : seed);
        }

        void Start()
        {
            Build(randomSeedOnStart ? UnityEngine.Random.Range(int.MinValue, int.MaxValue) : seed);
        }

        // ------------------------------------------------------------------ build steps

        int propIndex;
        int lightIndex;

        readonly Dictionary<int, GameObject> roomObjects = new Dictionary<int, GameObject>();
        readonly Dictionary<int, GameObject> corridorObjects = new Dictionary<int, GameObject>();

        void ClearLevel()
        {
            propIndex = 0;
            lightIndex = 0;
            roomObjects.Clear();
            corridorObjects.Clear();
            LevelRoot = null;

            Transform existing = transform.Find(LevelRootName);
            if (existing == null) return;

            // DestroyImmediate so an editor-side "Rebuild" button works outside play mode.
            if (Application.isPlaying) Destroy(existing.gameObject);
            else DestroyImmediate(existing.gameObject);
        }

        /// <summary>
        /// One room, honouring its template. The template's Generate* flags decide which of the generated
        /// surfaces appear; a template with a <see cref="RoomTemplateAsset.prefab"/> also gets that prefab
        /// instantiated at the room centre. Room.Template is never null (Core defaults it to Procedural), but
        /// this is defensive about it anyway so a half-migrated Core cannot drop a room's floor.
        /// </summary>
        GameObject BuildRoom(GameObject root, LevelLayout layout, Room room, LevelGenProfile p,
                             float ceiling, float wallThickness,
                             ref int propCount, ref int lightCount, ref int prefabRooms)
        {
            RoomTemplateSpec spec = room.Template ?? RoomTemplateSpec.Procedural();
            RoomTemplateAsset asset = p.FindTemplate(room.TemplateName);

            var holder = new GameObject($"Room_{room.Id}_{room.TemplateName}");
            holder.transform.SetParent(root.transform, false);

            // The floor is "the floor under the prefab": always collided, because it is the one surface the
            // NavMesh is guaranteed to bake on even when a prefab brings no floor collider of its own.
            if (spec.GenerateFloor)
            {
                Material floorMat = asset != null && asset.floorMaterialOverride != null
                                  ? asset.floorMaterialOverride : mats.Floor;
                AddMeshObject(holder.transform, $"Floor_{room.Id}",
                              RoomMeshBuilder.BuildRoomFloor(room), floorMat, true);
            }

            if (spec.GenerateWalls)
                AddMeshObject(holder.transform, $"Walls_{room.Id}",
                              RoomMeshBuilder.BuildRoomWalls(room, layout, ceiling, wallThickness), mats.Wall, true);

            // No collider on ceilings: the player cannot jump 3 m, and a collider up there would give the
            // NavMesh bake a second walkable surface above the floor.
            if (spec.GenerateCeiling)
                AddMeshObject(holder.transform, $"Ceiling_{room.Id}",
                              RoomMeshBuilder.BuildRoomCeiling(room, ceiling), mats.Ceiling, false);

            // ------------------------------------------------------------ the hand-built room prefab
            //
            // Placed at the room centre with identity rotation and y = 0. No NavMeshModifier is added and no
            // collider is synthesised: the prefab is a child of "Level", and the runtime NavMeshSurface
            // collects its children's physics colliders, so whatever colliders the author put in the prefab
            // (and only those) shape the NavMesh.
            if (asset != null && asset.prefab != null)
            {
                GameObject instance = Instantiate(asset.prefab, holder.transform);
                instance.name = asset.prefab.name;
                instance.transform.localPosition = ToWorld(room.Center) + asset.prefabOffset;
                instance.transform.localRotation = Quaternion.identity;
                prefabRooms++;
            }

            if (spec.GenerateProps && room.Props != null && room.Props.Count > 0)
                propCount += SpawnProps(Group(holder.transform, PropsGroupName), room.Props, p);

            if (spec.GenerateLights && room.Lights != null && room.Lights.Count > 0)
                lightCount += SpawnLights(Group(holder.transform, LightsGroupName), room.Lights, p, ceiling, 0f);

            return holder;
        }

        /// <summary>
        /// One corridor. Corridors are owned by the generator end to end - they are what guarantees the level
        /// is connected and walkable - so templates never suppress their floor, walls or ceiling.
        /// </summary>
        GameObject BuildCorridor(GameObject root, Corridor corridor, LevelGenProfile p,
                                 float ceiling, float wallThickness, ref int propCount, ref int lightCount)
        {
            var holder = new GameObject($"Corridor_{corridor.Id}_{corridor.RoomA}-{corridor.RoomB}");
            holder.transform.SetParent(root.transform, false);

            AddMeshObject(holder.transform, $"Floor_{corridor.Id}",
                          RoomMeshBuilder.BuildCorridorFloor(corridor), mats.CorridorFloor, true);
            AddMeshObject(holder.transform, $"Walls_{corridor.Id}",
                          RoomMeshBuilder.BuildCorridorWalls(corridor, ceiling, wallThickness), mats.Wall, true);
            AddMeshObject(holder.transform, $"Ceiling_{corridor.Id}",
                          RoomMeshBuilder.BuildCorridorCeiling(corridor, ceiling), mats.Ceiling, false);

            if (corridor.Props != null && corridor.Props.Count > 0)
                propCount += SpawnProps(Group(holder.transform, PropsGroupName), corridor.Props, p);

            if (corridor.Lights != null && corridor.Lights.Count > 0)
                lightCount += SpawnLights(Group(holder.transform, LightsGroupName), corridor.Lights, p,
                                          ceiling, CorridorYaw(corridor));

            return holder;
        }

        static Transform Group(Transform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null) return existing;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>
        /// Furniture: the profile's prefab for the archetype when one is mapped, generated primitives otherwise.
        /// Both paths end up with a root collider and a Not Walkable NavMeshModifier (see PropBuilder).
        /// </summary>
        int SpawnProps(Transform parent, List<Prop> props, LevelGenProfile p)
        {
            int n = 0;
            for (int i = 0; i < props.Count; i++)
            {
                Prop prop = props[i];
                if (prop == null) continue;

                PropPrefabEntry entry = p.FindPropPrefab(prop.Type);
                GameObject go = entry != null
                    ? PropBuilder.BuildFromPrefab(parent, prop, propIndex, entry.prefab, entry.addColliderIfMissing)
                    : PropBuilder.Build(parent, prop, propIndex, mats);

                if (go == null) continue;
                propIndex++;
                n++;
            }
            return n;
        }

        /// <summary>
        /// Light fixtures: the profile's prefab for the fixture kind when one is mapped, generated housing +
        /// runtime point light otherwise. Flicker and the "dead fixture" state apply to both (see LightRig).
        /// </summary>
        int SpawnLights(Transform parent, List<LightSource> lights, LevelGenProfile p, float ceiling, float yaw)
        {
            int n = 0;
            for (int i = 0; i < lights.Count; i++)
            {
                LightSource src = lights[i];
                if (src == null) continue;

                LightPrefabEntry entry = p.FindLightPrefab(src.Type);
                GameObject go = entry != null
                    ? LightRig.BuildFixtureFromPrefab(parent, src, lightIndex, entry.prefab, ceiling,
                                                      entry.applyDataToLight, yaw)
                    : LightRig.BuildFixture(parent, src, lightIndex, ceiling, mats, yaw);

                if (go == null) continue;
                lightIndex++;
                n++;
            }
            return n;
        }

        /// <summary>Yaw that aligns an elongated fixture (a corridor strip) with the corridor's run.</summary>
        static float CorridorYaw(Corridor corridor)
        {
            List<Vector3> path = RoomMeshBuilder.PathPoints(corridor);
            if (path.Count < 2) return 0f;
            Vector3 d = path[path.Count - 1] - path[0];
            if (d.sqrMagnitude < 1e-8f) return 0f;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Mesh + renderer, and a MeshCollider only when <paramref name="collide"/>. Colliders are what the
        /// NavMesh collects, so "no collider" is how a surface is kept out of the bake entirely.
        /// </summary>
        GameObject AddMeshObject(Transform parent, string name, Mesh mesh, Material material, bool collide)
        {
            var go = new GameObject(name);
            go.layer = 0; // Default
            go.transform.SetParent(parent, false);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;

            if (collide && mesh != null && mesh.vertexCount > 0)
                go.AddComponent<MeshCollider>().sharedMesh = mesh;

            return go;
        }

        void BuildMarkers(GameObject root, LevelLayout layout, float markerSize)
        {
            Room spawn = layout.SpawnRoom;
            if (spawn != null)
                AddMarker(root.transform, "SpawnMarker", ToWorld(spawn.Center), mats.SpawnMarker, markerSize);

            foreach (Room key in layout.KeyRooms)
                AddMarker(root.transform, $"KeyMarker_{key.Id}", ToWorld(key.Center), mats.KeyMarker, markerSize);
        }

        /// <summary>Visual placeholder only - no trigger, no collider, no pickup logic (out of scope for this pass).</summary>
        void AddMarker(Transform parent, string name, Vector3 center, Material material, float markerSize)
        {
            // Keep markers out of the NavMesh carve: built from the shared box mesh, so no collider exists
            // to destroy in the first place.
            GenPrimitives.Part(parent, name, GenPrimitives.Box, material,
                               center + Vector3.up * (markerSize * 0.5f),
                               Vector3.one * markerSize, Quaternion.identity);
        }

        bool BakeNavMesh(GameObject root, int agentTypeId)
        {
            var surface = root.GetComponent<NavMeshSurface>();
            if (surface == null) surface = root.AddComponent<NavMeshSurface>();

            surface.agentTypeID = agentTypeId;
            // Children + PhysicsColliders is what makes teammate prefabs participate for free: anything with a
            // collider under "Level" - generated floor, prefab room walls, prop prefab colliders - is collected.
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;

            if (surface.navMeshData != null) surface.RemoveData();
            // Colliders created this frame are not visible to the NavMesh collector until the physics scene is
            // synced; without this the first bake can skip stretches of freshly built floor (seen as a ~1 m
            // NavMesh gap across a corridor that disappears on a second bake).
            Physics.SyncTransforms();
            surface.BuildNavMesh();

            return surface.navMeshData != null;
        }

        // ------------------------------------------------------------------ materials

        readonly LevelMaterials mats = new LevelMaterials();

        void EnsureMaterials(LevelGenProfile p)
        {
            mats.EnsureAll(p.floorMaterial, p.wallMaterial, p.corridorFloorMaterial, p.ceilingMaterial);
        }

        // ------------------------------------------------------------------ gizmos

        void OnDrawGizmosSelected()
        {
            LevelLayout layout = CurrentLayout;
            if (layout == null) return;

            float ceilingHeight = profile != null ? profile.ceilingHeight : 3.0f;

            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                Room room = layout.Rooms[i];
                Vector3 c = ToWorld(room.Center);

                Gizmos.color = room.Role == RoomRole.Spawn ? Color.green
                             : room.Role == RoomRole.Key ? Color.yellow
                             : Color.cyan;
                Gizmos.DrawSphere(c, 0.4f);

                if (room.Shape == RoomShape.Ellipse)
                    DrawWireEllipse(c, (float)room.HalfX, (float)room.HalfZ, 32);
                else
                    Gizmos.DrawWireCube(c + Vector3.up * (ceilingHeight * 0.5f),
                                        new Vector3((float)room.SizeX, ceilingHeight, (float)room.SizeZ));
            }

            Gizmos.color = Color.magenta;
            for (int i = 0; i < layout.Corridors.Count; i++)
            {
                List<Vec2> path = layout.Corridors[i].Path;
                if (path == null) continue;
                for (int p = 1; p < path.Count; p++)
                    Gizmos.DrawLine(ToWorld(path[p - 1]), ToWorld(path[p]));
            }
        }

        static void DrawWireEllipse(Vector3 center, float halfX, float halfZ, int segments)
        {
            Vector3 prev = center + new Vector3(halfX, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float t = 2f * Mathf.PI * i / segments;
                Vector3 next = center + new Vector3(halfX * Mathf.Cos(t), 0f, halfZ * Mathf.Sin(t));
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }
    }
}
