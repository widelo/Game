# A* stalker: project review, integration, and explanation

## What the uploaded project actually contains

The source of this review is the uploaded `Game-AStar-Review.zip`. No assumption is made that its contents match a particular GitHub commit; the ZIP contains no commit identifier. The implementation is tailored to this snapshot.

| Area | Observed facts | Consequence for this implementation |
|---|---|---|
| Unity | `ProjectSettings/ProjectVersion.txt`: **6000.6.2f1** | Import with that Editor version first. |
| Packages | AI Navigation **2.0.14**, URP **17.6.0**, Input System **1.20.0**, Test Framework **1.8.0**, Cinemachine **3.1.4** | No new package dependency or manifest edit. |
| Player | `Assets/Scripts/Player/FirstPersonController.cs`, namespace `Game.Player`, uses `CharacterController.Move`, old `Input` APIs, mouse look, jump, crouch, sprint and slide | Reuse its Transform, cameraPivot, velocity, grounded and movement-state properties. |
| Player balance | Walk **5**, sprint **8**, crouch **2.5 m/s**; standing capsule **1.8 m**, radius **0.4 m**, camera **1.6 m** | Boss chase defaults to **6.3 m/s**, so straight-line sprint escape remains possible. |
| Input settings | `activeInputHandler: 2` in ProjectSettings, enabling both input backends | The player's comment saying `activeInputHandler=0` is stale. Do not change the input configuration to follow that comment. |
| Identification | `Main.unity` has a GameObject named `Player`, tagged **Untagged**, with the custom controller | Assign the existing root directly; fallback finds the custom component, not a `Player` tag. |
| Existing enemy | `EnemySpawner`, `EnemyAgent`, `IEnemyBrain/EnemyContext`, placeholder `StalkerBrain`, `EnemyHud` in `Assets/Scripts/Enemy` | Their interfaces and tests are NavMesh-dependent. Leave them intact in this additive branch. |
| Existing behavior | Placeholder uses NavMesh destinations, immediate `caught` at close range, sprint hearing, and a patrol bias toward the player's current room | A brain-only replacement would retain NavMesh navigation and some unfair knowledge unless the wider seam were redesigned. |
| Existing consequence | `EnemyAgent` teleports the player and enemy back to spawn, then waits 3 seconds | No supplied general health/damage system was found. The new optional `StalkerTarget` supplies a small, separate damage contract. |
| Monster | `Main.unity` references `Assets/Models/basicmonster.obj` at 2.8 m model height | Reuse the mesh through `ModelFit.FitToHeight`. An OBJ is a static visual; attack animations and audio are not supplied. |
| Main environment | `LevelBuilder` generates rooms, corridors, walls, ceilings, furniture, markers and lights under `LevelRoot`; raises `OnLevelBuilt` after building | Build the A* graph from actual colliders after this event, and rebuild it after R/N/P. |
| Layout | Core data explicitly uses XZ coordinates and floor Y=0. Default profile: 5–9 rooms, room sizes 7–20 m, 30 m lattice pitch, 3.2 m corridors, 3 m ceilings | A flat eight-neighbour grid is appropriate for the supplied 3D map. This does not implement stairs or stacked floors. |
| Obstacles | Generated floors/walls/ceilings use MeshColliders; furniture uses footprint BoxColliders; teammate prefabs supply their own colliders | Use full capsule clearance and swept edges, not room adjacency alone. |
| Templates | `TeammateRoom` disables generated walls and uses `RoomFolder/Prefabs/Floor.prefab`, containing authored walls and furnishings | A logically connected room may still be physically sealed. Only cells in the player's actual connected component are spawn/patrol candidates. |
| Layers/physics | No custom tags/layers in TagManager; scene actors and generated meshes are Default; collision matrix allows all layer pairs; transform auto-sync is off | Keep masks inclusive and filter player/boss hierarchies explicitly. Synchronize physics after construction/teleports. Ignore triggers explicitly. |
| Scene/test tooling | Main and SampleScene are in build settings; SampleScene is a basic scene. Existing enemy tests and NavProbe/HeadlessVerify tools target legacy navigation | Keep Main and those tests unchanged; use the new scene and tests for A*. |

Starter Assets controllers also exist in the archive, but the inspected Main scene uses **Game.Player.FirstPersonController**, not the similarly named Starter Assets class. The existing `LevelDebugController` owns initial player placement and R/N/P level rebuilding; `OverheadDebugView` provides Tab inspection. These are reused unchanged.

## Why the integration is additive

`EnemyContext.Agent` is explicitly a `NavMeshAgent`; `EnemyAgent` requires that component; the legacy spawner validates spawns with NavMesh queries. Consequently, changing `EnemySpawner.CreateBrain()` alone would not meet your requirement for independent A* navigation.

The new implementation lives in separate `StalkerAI.Core`, `StalkerAI.Runtime`, and `StalkerAI.Editor` assemblies. The original enemy API stays available to teammates. **No existing file needs editing to use this patch.** `Main_AStar.unity` is a new copy of the inspected Main scene. Only in that copy:

- The legacy EnemySpawner and EnemyHud are disabled, and `spawnOnLevelBuilt` is false.
- `AStarDirector` is added to the renamed `AStarEnemies` object.
- LevelBuilder references `Assets/StalkerAI/AStarLevelGenProfile.asset`, copied from the inspected default profile with NavMesh baking disabled.
- Existing player, model, camera, lighting and debug references are preserved.

The original Resources profile, Main scene, package manifest and build settings are untouched. The navigation package remains installed because the team's original level code still references its types. The new boss never asks it for a path.

## Runtime responsibilities

| File | Responsibility |
|---|---|
| `Core/GridGraph.cs` | Eight-neighbour topology, edge costs, no corner squeezing, octile heuristic. |
| `Core/IndexedMinHeap.cs` | Open-set priority queue with decrease-key; deterministic F/H/node-ID tie breaks. |
| `Core/AStarSearch.cs` | G/H/F records, closed set, relaxations, bounded work per Step, status, path reconstruction. |
| `Runtime/AStarNavigation.cs` | Sample actual colliders, verify supported capsule positions and swept edges, label connected components, map local world endpoints, draw grid. |
| `Runtime/AStarMotor.cs` | Request/reuse searches, follow waypoints with CharacterController, apply gravity, check local movement, smooth with safe lookahead, stop/replan when blocked. |
| `Runtime/StalkerPerception.cs` | Vision, occlusion, approximate hearing, last-known location, memory expiry. |
| `Runtime/StalkerBoss.cs` | Five-state FSM, patrol/search/cover choices, bounded chase, windup/hit/recovery subphases. |
| `Runtime/StalkerSettings.cs` | Serializable behavior and navigation settings shown on the Director. |
| `Runtime/AStarDirector.cs` | Observe level lifecycle, own shared graph, bind the player, spawn connected enemies, optional respawn and HUD. |
| `Runtime/NoiseBus.cs` | Explicit position/radius disturbance events, with static-event reset for domain-reload-disabled sessions. |
| `Runtime/PlayerNoiseEmitter.cs` | Adapt existing player movement to footstep/sprint/crouch/slide noises without editing player code. |
| `Runtime/StalkerTarget.cs` | Optional 100-health sample adapter, invulnerability, hit/defeat hooks. |
| `Editor/StalkerSceneTools.cs` | Open the demo or install into a future scene using Undo; no automatic scene saving. |

Core paths are under `Assets/Scripts/StalkerAI/Core`; runtime paths are under `Assets/Scripts/StalkerAI/Runtime`; the editor file is under `Assets/Editor/StalkerAI`. See `Files.md` for every exact path, including metadata, tests, demo assets and tools.

The flow is: **LevelBuilder finishes → Director waits for old geometry destruction → physics grid is sampled → reachable boss spawns → senses update knowledge → FSM chooses a goal/speed → A* returns a route → motor moves the body.** The FSM selects *why and where* to move; A* computes *how to get there*.

## Installation and exact Editor setup

First follow the branch/copy steps in the root README. This archive is an overlay, not a replacement for the repository and not a Unity `.unitypackage`. Copy the included `.meta` files too: the demo's serialized script and profile references use their GUIDs.

Open `Assets/Scenes/Main_AStar.unity`. It is already wired. Before Play, inspect these objects:

| Object/component | Assignment or setting |
|---|---|
| LevelBuilder | Identity position/rotation, unit scale; profile = **AStarLevelGenProfile**; seed copied from Main = **42**. |
| AStarEnemies / AStarDirector | Builder = scene LevelBuilder; Player = root Player Transform; Model Prefab = **basicmonster** imported OBJ; Enemy Count = **1**. |
| AStarEnemies / legacy EnemySpawner | Component disabled and Spawn On Level Built false. |
| AStarEnemies / legacy EnemyHud | Component disabled. |
| Player | Existing custom first-person controller and CharacterController remain enabled. Do not retag it or replace its controller. |
| Profile | Bake Nav Mesh is off in the new profile. The original profile still has it on. |

On Play, the Director temporarily adds `StalkerTarget` and `PlayerNoiseEmitter` to the player if absent and enabled in its options. It creates `AStarStalker_0` with exactly one active body collider: a CharacterController of radius **0.45 m**, height **2.6 m**, centre Y **1.3 m**, skin width **0.025 m**, small step offset **0.05 m**. The visual is fitted to **2.8 m**; its colliders are disabled. Model Rigidbody motion and Animator root motion are disabled so the motor owns movement. No NavMeshAgent is added.

Do not add a second capsule collider or dynamic Rigidbody to the boss root. Keep the Director's hierarchy unit-scaled. Body dimensions determine navigation clearance; a much larger model needs corresponding body settings and a new graph.

**Masks and colliders:** World Mask defaults to Everything because your walls and player share Default. Navigation explicitly ignores the player hierarchy and the Director's actor hierarchy, while checking all other solid colliders. Generated floors must remain in the mask too. Triggers do not block sight or paths. A decorative mesh with no collider is invisible to these physical checks; give a new solid obstacle an appropriate non-trigger collider. The existing collision matrix is unchanged.

**Navigation display:** On AStarEnemies, expand Navigation and enable Draw Grid and optionally Draw Blocked Nodes. Keep that object selected in Scene view with Gizmos enabled. Green cells are supported/clear; red cells are blocked or unsupported. Display is capped at 6,000 sampled cells. Select a spawned boss for yellow detection sphere/FOV rays, blue preferred-distance sphere around the last-known position, magenta memory marker, red attack range and cyan route. The HUD shows build status, health, FSM state, attack cue, expanded nodes and the raw graph path cost.

**Controls already in your project:** WASD/mouse, Shift sprint, Space jump, C/Left Ctrl crouch, Shift+crouch slide; Escape unlocks the cursor. R rebuilds the current seed, N/P step seeds, L changes debug lighting, Tab opens overhead inspection, F1 toggles the existing level HUD. The new HUD has its own Show Hud checkbox.

**Authored patrols:** Create empty transforms at safe floor positions and assign them to Patrol Locations on the Director. Leave the array empty to use reachable room centres. These markers should not be children of the generated Level object if they must survive rebuilds.

**Changing navigation while playing:** Grid geometry settings are snapshotted. After changing cell size, body dimensions, mask or floor height, invoke the Director component's **Rebuild A Star Navigation (Play Mode)** context action. This cancels the old build, recreates the graph and respawns A* enemies. Live gizmo toggles apply immediately. Persist balance changes on the Director while stopped; edits to spawned runtime objects vanish after Play.

**Using a future team scene:** Save a copy of that scene first, then choose **CS462 > A Star > Install Into Current Scene** and inspect the result. It requires the existing LevelBuilder and custom player component. It disables legacy spawners/HUDs in that scene and copies their model reference. It does not overwrite the scene on disk or change its profile automatically; assign the A* profile if you want baking disabled. Undo reverses the installation. The provided Main_AStar scene already has these changes.

Main_AStar is not added to Build Settings automatically. Add it to a dedicated build profile when you deliberately want an executable of this demo. Existing build/headless scripts that load Main continue exercising the original enemy.

## Inspector defaults and their purpose

| Setting | Default | Gameplay/technical effect |
|---|---:|---|
| Cell size | 0.75 m | Resolution versus scan/search work. Try 0.5 m if a visually wide doorway has no valid cells. |
| Body radius + margin | 0.45 + 0.06 m | Physical clearance used by capsule queries; margin exceeds controller skin. |
| Body height | 2.6 m | Clears the supplied 3 m ceilings while representing a tall monster. |
| Floor Y / tolerance | 0 / 0.025 m | Matches the supplied flat-floor geometry; not a terrain solver. |
| Build cells per frame | 400 | Spreads scan/link work over frames. This is a work count, not a millisecond guarantee. |
| Maximum cells | 80,000 | Rejects unexpectedly large bounds instead of allocating an unlimited grid. |
| Expansions per frame | 64 per boss | Resumable search budget; a budget pause is not failure. |
| Repath interval | 0.4 s | Minimum interval between new searches. Static goals retain their path. |
| Patrol / investigate / stalk / chase / retreat speed | 2 / 3 / 2.6 / 6.3 / 3.8 m/s | Stalking is visibly slower; sprinting player can outrun pursuit in open space. |
| Sight / FOV / sense interval | 19 m / 110° / 0.15 s | Horizontal view angle plus actual camera-height LOS; crouch can hide the camera behind cover. |
| Detection delay | 0.6 s | Minimum investigation phase before visible player causes Stalk; also entry delay before chase attacks. |
| Hearing / cooldown / occluded multiplier | 18 m / 0.5 s / 0.45 | Occlusion reduces effective noise reach, rather than giving wall-penetrating sight. |
| Hearing uncertainty | 1.2 m | Searches near a sound source instead of obtaining an exact live target lock. |
| Memory / lost-sight delay | 8 / 2.2 s | Stops following new hidden motion; transitions to investigation before memory expires. |
| Patrol pause / leg timeout | 1–3 / 30 s | Stops to look, then picks another room; abandons failed or stuck legs. |
| Investigation / local search | 7 / 3 s | Bounded travel/search; limited local checks around the observation. |
| Preferred stalking distance | 8 ± 1.5 m | Samples positions around the remembered location; geography may make exact spacing impossible. |
| Stalk before chase | 8–15 s | A variable tension period while the target remains visible. |
| Watch duration / chance | 1.2–3 s / 35% | Pauses at decision intervals when not already too close. |
| Reposition interval / candidates | 3 s / 12 | Bounded cover-position evaluation. |
| Close aggro distance | 3.2 m | Visible players approaching the creature can trigger a chase earlier. |
| Max chase / chase cooldown | 12 / 10 s | Pursuit ends; reacquisition cannot immediately start another chase. |
| Disengagement | 6 s | Retreat or wait; ignores senses/noises during this interval. |
| Attack range / windup | 1.65 m / 0.9 s | Body stops and locks aim before a fresh hit test. |
| Attack cooldown / recovery | 2.5 / 0.75 s | Cooldown begins at resolution; recovery is a stopped subphase inside Chase. |
| Attack arc / vertical tolerance | ±45° / 1.25 m | Stepping sideways or changing elevation can defeat the attack. |
| Damage / health | 25 / 100 | Four unprotected confirmed hits defeat the sample target. Contact alone causes no damage. |
| Respawn protection | 5 s | Applied at initial graph readiness and after sample respawn. |

Behavior random choices use a private seeded `System.Random` per enemy, so they do not consume the game's shared Unity random stream. Procedural-layout seeds and behavior seeds are different controls.

## FSM transitions and fairness

| State | What happens | Main transitions |
|---|---|---|
| Patrol | Choose reachable rooms without reading the player's hidden location; travel and occasionally pause/scan. | A sight or audible-noise observation → Investigate. |
| Investigate | Move to the last observation; scan and check nearby points on arrival/failure. | Visible target after delay → Stalk. Search/time/memory exhaustion → Disengage. |
| Stalk | Choose a reachable ring position near preferred range; prefer torso cover with a visible head position, sometimes watch. | Visible proximity or stalk timer with chase cooldown ready → Chase. Lost sight → Investigate. Forgotten target → Disengage. |
| Chase | Replan toward the latest **observed** position, run faster, use attack subphases when in reach. | Lost sight → Investigate; duration/memory exhaustion or target defeat → Disengage. |
| Disengage | Move away from a frozen threat position if a retreat is available; otherwise stop safely. | After cooldown, forget observations and return to Patrol. |

A boss may use known map topology to patrol and choose possible cover; that is different from knowing where a hidden player currently is. The live player transform is read by vision tests, audible noise emission, and immediate attack checks gated by fresh sight. Navigation and ring-position decisions consume stored observations. During disengagement the boss does not update those observations at all.

`StalkerPerception` caches sight at its configured cadence. LOS targets the existing player's cameraPivot, so crouch height matters. If sight is lost, LastKnown freezes unless an actual new sound is heard. Hearing stores an uncertain event location; it does not attach the enemy to the sound source's transform.

Attacks are **subphases of Chase**, not a sixth FSM state. At windup the enemy stops, faces the observed player, stores its attack direction, and invokes `onAttackWindup`. At resolution it checks fresh LOS, current horizontal range, vertical separation, attack arc, health and invulnerability. Moving behind a wall, outside range, or outside the locked attack arc can produce a miss. `onAttackHit`/`onAttackMiss` and the sample target's hit/defeat UnityEvents are integration hooks. The sample HUD displays a windup warning; production animation/audio cues should be attached to these events before hiding that HUD.

The sample target adapter is intentionally small. A future team health system should become the damage endpoint inside `StalkerBoss.CanHitNow/TickAttack`, or be wrapped by an agreed adapter. Do not run two independent health systems that both apply the same attack. Disabling Add Sample Health without supplying a target allows navigation/perception but prevents damage.

For a thrown object or door sound, a component in an assembly that references StalkerAI.Runtime can call:

```csharp
CS462.StalkerAI.NoiseBus.Emit(transform.position, 12f, transform);
```

The radius is the sound's maximum hearing range, capped by the boss's hearing setting and attenuated by occlusion. No sound is emitted just because the boss happens to know a player's Transform. Keep the new footstep adapter separate from Game.Player: adding a reverse `Game.Player -> StalkerAI.Runtime` reference would create a cycle because the runtime already references Game.Player.

## Understanding the A* implementation

For a node n:

- **G(n)** is the cheapest route cost found so far from the start to n.
- **H(n)** is a lower bound on the remaining cost to the goal.
- **F(n) = G(n) + H(n)** estimates the total route cost through n.

Cells are eight-connected. A cardinal step costs `cellSize`; a diagonal costs `sqrt(2) * cellSize`. The heuristic is octile distance:

```text
H = cellSize * (max(dx,dz) + (sqrt(2)-1) * min(dx,dz))
```

Here dx/dz are differences in **cell indices**, not world metres. For dx=3, dz=2 at unit cell size, an unobstructed route uses two diagonal steps and one straight step, costing about 3.828. Manhattan distance would incorrectly estimate 5 for that route and therefore would not be admissible with these cheap diagonals.

`Begin()` clears per-search records, gives the start G=0, and inserts it in the open heap. Each expansion:

1. Pop the open node with minimum F. Ties use lower H, then node ID; the heuristic is never artificially inflated.
2. If it is the goal, follow parent indices back to the start and reverse the list.
3. Otherwise close it and examine existing neighbouring edges.
4. Compute `candidateG = G(current) + edgeCost`.
5. If this improves a neighbour, update its parent/G/H/F and insert it or decrease its heap key.

Walls and missing floor remove nodes or edges; they do not reduce the geometric step costs. Octile H remains a lower bound and is **consistent** for those costs. Thus, on a fixed graph, when the goal is popped its G is optimal, and closed nodes need not be reopened. Different search objects maintain their own records, so multiple enemies can share the immutable graph.

The heap keeps one entry per open node and an index back to each node's slot. Push/decrease-key bubble upward; pop replaces the root with the last entry and bubbles downward. This avoids sorting a List on every expansion or accumulating duplicate queue entries.

**Optimality scope:** the shortest path is between the selected start and goal nodes on the sampled graph. The grid discretizes the real world and endpoint connectors are separately checked. It does not prove the globally shortest continuous-space path. Cover preference chooses a goal before A*; it does not alter the heuristic. The motor can safely shortcut a few visible waypoints, so the HUD's graph path cost describes the raw A* route, not exact distance walked after smoothing.

**Failure:** blocked/off-grid endpoints produce InvalidEndpoint or a safe local mapping failure. Different connected components are rejected early. An exhausted open set produces NoPath and an empty route. An expansion budget merely returns Searching; later frames resume the same search. There is no fallback that moves directly through a wall when a route fails.

**Physics validity:** graph construction checks a supported five-point foot footprint, full capsule clearance, diagonal side-cell availability, and a swept capsule plus intermediate floor probes for each edge. Local snapping requires a physical connector, so it cannot snap across a wall merely because another cell is nearby. Movement uses CharacterController.Move and separately applies gravity; it validates its intended horizontal segment before moving. Lookahead uses the same clearance/support test rather than a spline that could cut a corner.

**Dynamic geometry:** searches can re-check current collider clearance on existing edges. A newly closed passage can be rejected and another existing route found; the motor also stops for a new obstacle between searches. If geometry changes while a search spans frames, there is no single frozen-world optimality guarantee; safe movement checks remain the last defense. Opened walls, removed floors, new walkable regions or moved map geometry require a graph rebuild because the cached topology/support/components cannot invent new edges. The supplied project has no door-state navigation API to hook automatically.

**Complexity:** let V be sampled grid cells and E valid directed edges. Heap A* costs `O((V+E) log V)` time in the worst case and `O(V)` space, including records, heap and parents; this bounded-degree grid has `E <= 8V`, so `O(V log V)` is the usual simplification. Begin also clears O(V) records. Per-agent search arrays are O(V); the graph and component labels are shared. Building/labeling the graph is O(V+E) in graph operations, plus physics-query costs and the fixed-resolution floor samples. Unity physics costs depend on scene complexity. The work budgets reduce spikes but are not real-time deadlines.

A* fits this project because a good heuristic directs search toward moving destinations while still finding an optimal fixed-graph route. The FSM adds deliberate behavior; A* alone cannot create tension, decide to watch, or grant escape opportunities.

## Systematic validation

Read `Validation.md` for what was actually executed in the delivery environment. Source parsing is not Unity compilation, and pure core tests do not establish physics behavior.

**Automated core tests:** in Test Runner, run `StalkerAI.EditMode.Tests`. The six checks cover unobstructed octile cost, start=goal, invalid/blocked endpoints, corner cutting, a missing edge between open cells, unreachable/reused searches, one-expansion budgets, deterministic heap ties, independent simultaneous searches, live edge filtering, and **1,000 fixed-seed random graphs compared with an independent O(V²) Dijkstra oracle**. Reconstructed edges/costs and heuristic consistency are also checked. The oracle does not use the A* heap or heuristic.

Optional standalone verification, if .NET 8 SDK is installed:

```bat
cd /d C:\Users\esteb\Game
dotnet run --project Tools\StalkerAI\CoreChecks.csproj -c Release
```

**Automated physics/behavior tests:** run `StalkerAI.PlayMode.Tests`. Five fixtures exercise a thin wall between otherwise open cell centres, void support/new obstacles, FOV/LOS/frozen and expired memory, an attack that can be dodged and then deals exactly 25 damage, and lost-target disengagement that ignores noise. Fixtures build their own physical test space away from the demo. These tests require Unity and are separate from the team's original EnemyPlayModeTests.

For an Editor installed at Unity Hub's usual path, these optional Command Prompt commands produce result XML. Adjust the Editor path if yours differs. Close the project in the interactive Editor before opening that same project in batch mode.

```bat
"C:\Program Files\Unity\Hub\Editor\6000.6.2f1\Editor\Unity.exe" -batchmode -runTests -projectPath "C:\Users\esteb\Game" -testPlatform EditMode -assemblyNames StalkerAI.EditMode.Tests -testResults "%TEMP%\stalker-edit.xml" -logFile "%TEMP%\stalker-edit.log"
"C:\Program Files\Unity\Hub\Editor\6000.6.2f1\Editor\Unity.exe" -batchmode -runTests -projectPath "C:\Users\esteb\Game" -testPlatform PlayMode -assemblyNames StalkerAI.PlayMode.Tests -testResults "%TEMP%\stalker-play.xml" -logFile "%TEMP%\stalker-play.log"
```

**Actual-scene acceptance checks:**

| Check | Procedure | Expected result |
|---|---|---|
| Initial integration | Open Main_AStar, Play, wait for graph progress. Inspect enemy components. | One new stalker for the default seed if a safe room exists; no legacy EnemyAgent or NavMeshAgent spawned; no Console errors. |
| Genuine A* | Keep Bake Nav Mesh off in the demo profile; inspect raw Core source and HUD expansion/path-cost data. | Enemy navigation operates without any baked navigation data. |
| Obstacles | Use L/Tab, move around beds and corridor corners; inspect path Gizmos. | Path goes through doorways/around colliders; body does not phase through thin walls. |
| Senses | Stand behind the boss outside FOV; crouch behind a solid prop; then enter view. | Sight requires range/FOV/clear camera ray; debug state changes follow observations. |
| Memory | Break LOS, change direction behind cover, then stay quiet. | Magenta point stays at last observation; search expires instead of following hidden motion. |
| Hearing | Compare crouch, walking, sprinting and sliding behind cover; also emit a test NoiseBus event. | Effective hearing varies with emitted radius/occlusion; quiet distant motion does not reveal you. |
| Stalking | Remain visible outside close aggro range and watch for 10–15 seconds. | Slow repositioning and pauses precede a chase; room shape can limit cover choices. |
| Dodge | Approach to attack range and leave it during the HUD windup, or get behind a solid wall. | MISS and unchanged health. Contact during windup does not damage. |
| Survive | Let one attack connect with full health. | Health becomes 75, then recovery/cooldown. |
| Defeat | Let four spaced, unprotected hits connect. | Sample player respawn, health reset and protection; bosses disengage. |
| Escape | Sprint around two corners and stop making loud noise. | Chase becomes investigation/disengagement and eventually patrol; it does not track hidden updates forever. |
| Rebuild | Press R/N/P repeatedly, including while the grid is still scanning. | Old enemies deactivate, prior scan cancels, graph/spawns correspond to newest level. No multiplied listeners or duplicate bosses. |
| Unreachable | Close a corridor with a solid wall or use a sealed room. | No direct-motion fallback; motor stops/replans, state timeouts remain bounded. |
| Dynamic topology | Add a new blocker, then remove a wall that originally blocked graph cells. | Closing edges can be rejected live. Explicit graph rebuild is required to exploit the newly opened region. |
| Regression | Open original Main and run its original tests. | Its original NavMesh-based enemy and existing setup are preserved. |

## Limits, performance and team boundaries

This implementation targets the **observed flat map**. It does not handle stacked floors, stairs, elevators, jumping navigation, arbitrary slopes, off-mesh links, flying, or continuous acoustic simulation. The five support rays sample ground; they are not a mathematical proof against holes smaller than the probe spacing. Static generated floor meshes are the intended input. CharacterController remains the collision backstop.

Use one boss initially. Several CharacterControllers can block each other in a narrow corridor; this implementation has no multi-agent reservation or crowd-avoidance solver. It detects stalled motion and replans, but cannot guarantee crowd deadlock resolution. Spawn selection prefers distinct connected rooms and can spawn fewer enemies than requested if no safe room is available; the Console warns.

The grid is shared, but searches and live clearance probes are per enemy. Very small cells, large maps, too many actors, complex MeshColliders, or dense overlapping prefabs can cost substantial CPU. Inspect the Unity Profiler during grid construction and chase; decrease build/expansion budgets or raise cell size as needed. Allocations are mainly graph/search construction and the returned route, not a new priority queue every frame. The HUD interpolates strings in OnGUI; turn it off for final profiling. Draw Grid is off by default and its node count is capped.

Physics non-alloc buffers hold 128 results. Saturation fails closed with a warning rather than silently overlooking obstacles. If you actually hit this warning, simplify colliders or increase the buffer sizes in AStarNavigation. Ensure all blocking geometry is in World Mask and your floor is within Floor Y tolerance. Renderer dimensions alone do not create collision.

The supplied monster is a static OBJ. Walking/attack animation, footstep audio, visual hit feedback and camera effects are separate presentation work. The stopped windup and sample HUD expose timing now; do not remove all warning cues before adding animation/audio equivalents.

Teammate ownership is preserved: no edits to Player, LevelGen, Enemy, Debug, packages, project settings, original scenes, original profiles or tests. Existing `SceneBootstrap` can regenerate Main; it does not propagate future changes to this copied demo. After teammates substantially revise Main or the default level profile, rebase and deliberately refresh the A* demo/profile or run the scene installer on a new scene copy. Do not silently copy stale settings back over their files.

The new optional Editor installer references Game.Enemy only to disable the old components. The runtime does not depend on Game.Enemy. If the team later wants a single enemy abstraction, agree on a navigation-neutral motor/context contract rather than adding A* methods around EnemyContext.Agent. Keep noise integration in adapters or a future shared events assembly to avoid circular references.

## Git workflow and review scope

Create the feature branch **before** importing this patch. A new branch does not automatically save or hide uncommitted work; inspect `git status` first and preserve existing changes. These commands are for your Windows machine, not actions already performed for you.

After Editor validation, stage only the added paths:

```bat
cd /d C:\Users\esteb\Game
git add Assets/Scripts/StalkerAI Assets/Scripts/StalkerAI.meta Assets/Editor/StalkerAI Assets/Editor/StalkerAI.meta Assets/Tests/StalkerAI Assets/Tests/StalkerAI.meta Assets/StalkerAI Assets/StalkerAI.meta Assets/Scenes/Main_AStar.unity Assets/Scenes/Main_AStar.unity.meta Docs/StalkerAI Tools/StalkerAI README-StalkerAI.md
git diff --cached --name-status
git diff --cached --stat
git commit -m "Add isolated A-star stalker AI and demo scene"
```

The staged list should contain additions for this package, plus only deliberate adjustments you reviewed. Unity may offer package upgrades or rewrite settings on opening a different Editor version; those are not part of this feature. Avoid broad `git add .` when teammates' unrelated local changes are present.

When ready to share the branch:

```bat
git push -u origin feat/esteban-astar-stalker
```

Use a pull request describing the independent A* backend, new demo scene, sample damage adapter, validation results and the flat-floor limitation. Do not merge into the team's shared branch before your project-specific Play Mode checks pass. To compare behavior, open original Main. After committing the feature branch, switching back to the prior branch removes these tracked additions normally; no hard reset or cleanup command is needed.

## API references used during implementation

The project-specific findings above come from your files. These official Unity references support the physics/test API usage:

- [CharacterController.Move](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/CharacterController.Move.html): applies requested movement with collision handling; gravity is supplied separately in the motor.
- [Physics.CapsuleCast](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.CapsuleCast.html): body-sized swept obstruction queries. Initial overlap is separately checked in this implementation.
- [Physics.SyncTransforms](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Physics.SyncTransforms.html): synchronize authored transform changes before scene queries.
- [Test Framework command-line arguments](https://docs.unity3d.com/Packages/com.unity.test-framework@1.4/manual/reference-command-line.html): test platform, assembly filters and XML result options. Your installed package is 1.8.0; the UI Test Runner is the primary workflow here.
