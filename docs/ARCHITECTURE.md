# CS 462 game — level generation architecture (v1)

Owner: Carson (room randomizing engine / key objective / key spawn). Enemy AI: Esteban. Engine: Unity 6000.6.2f1, URP (Forward+), Active Input Handling = Both (level-gen scripts use the legacy Input Manager; Starter Assets use the Input System).

## Layers (each is its own asmdef)

| asmdef | Path | Depends on | Purpose |
|---|---|---|---|
| `LevelGen.Core` | `Assets/Scripts/LevelGen/Core/` | nothing (no UnityEngine) | Pure C#. Graph layout + room shapes + corridors. Deterministic from an int seed. Unit-tested. |
| `LevelGen.Unity` | `Assets/Scripts/LevelGen/Runtime/` | Core, UnityEngine, Unity.AI.Navigation | Turns a `LevelLayout` into meshes + colliders, bakes NavMesh at runtime, places spawn/key markers, fires `OnLevelBuilt`. |
| `Game.Player` | `Assets/Scripts/Player/` | UnityEngine | First-person CharacterController: WASD + mouse look. |
| `Game.Debug` | `Assets/Scripts/Debug/` | Core, LevelGen.Unity | Regenerate on R, seed HUD, gizmo graph overlay. |
| `Game.Editor` | `Assets/Editor/` | all (Editor only) | `SceneBootstrap.CreateMainScene()` builds `Assets/Scenes/Main.unity` and registers it in Build Settings. Runnable via `-executeMethod`. |
| `LevelGen.Core.Tests` | `Assets/Tests/EditMode/` | Core, NUnit | Property tests across many seeds. |

## Algorithm (v1 — orthogonal only, by design)

1. Lattice of `GridWidth x GridHeight` cells, pitch `CellSize`. Each room sits centred in exactly one cell, so rooms can never overlap and every corridor between adjacent cells is a straight axis-aligned segment.
2. Pick `N ∈ [MinRooms, MaxRooms]` cells by random walk / growth from a random start cell (each step adds a random unvisited 4-neighbour of any visited cell). Result is a connected set.
3. Edges: the growth tree edges are always kept (guarantees connectivity). Every other pair of orthogonally adjacent chosen cells becomes a corridor with probability `LoopEdgeChance`.
4. Room sizes: per axis uniform in `[MinRoomSize, MaxRoomSize]`; shape Ellipse with `EllipseChance`, else Rectangle. Invariant: `MaxRoomSize < CellSize` so every corridor has positive length (`CellSize - HalfA - HalfB > 0`).
5. Corridor path = `[A.BoundaryPoint(dir), B.BoundaryPoint(opposite(dir))]`. Polyline now so curved corridors later are a Core-only change.
6. Roles: spawn = the start cell. 3 key rooms = random distinct non-spawn rooms (prefer graph-distance ≥ 2 from spawn when N allows).

## Contract for enemy AI (Esteban)

- Subscribe to `LevelBuilder.OnLevelBuilt(LevelLayout layout)`; NavMesh is already baked when it fires.
- `layout.Rooms[i].Center` / `layout.Neighbours(id)` give a room graph for patrol logic.
- `LevelBuilder.PlayerSpawnPoint` (Vector3) and `LevelBuilder.RoomWorldCenter(roomId)` are the helper accessors.
- NavMesh is baked via `NavMeshSurface` (package `com.unity.ai.navigation`) collecting all generated geometry (walkable floor, wall obstacles). Agent radius 0.5, height 2.

## Verification (headless, from WSL)

```
UNITY="/mnt/c/Program Files/Unity/Hub/Editor/6000.6.2f1/Editor/Unity.exe"
PROJ='C:\Users\schmi\projs\cs462-game'
# compile check
"$UNITY" -batchmode -nographics -quit -projectPath "$PROJ" -logFile "$PROJ\Logs\compile.log"
# edit-mode tests
"$UNITY" -batchmode -nographics -projectPath "$PROJ" -runTests -testPlatform EditMode -testResults "$PROJ\Logs\results.xml" -logFile "$PROJ\Logs\tests.log"
# build scene
"$UNITY" -batchmode -nographics -quit -projectPath "$PROJ" -executeMethod Game.Editor.SceneBootstrap.CreateMainScene -logFile "$PROJ\Logs\scene.log"
```

## Status 2026-10-05

- All six assemblies compile with zero warnings in Unity 6000.6.2f1 (headless).
- 15/15 edit-mode NUnit tests pass (`Assets/Tests/EditMode/LevelGeneratorTests.cs`): determinism, validator over seeds 0..999, room-count coverage 5..9, both shapes, settings validation.
- `Game.Editor.HeadlessVerify.BuildLevels` passes for seeds 0..19: `LevelValidator` clean, NavMesh baked, `NavMesh.CalculatePath` complete from spawn to every room centre and corridor midpoint.
- Not yet exercised: Play mode by a human (movement feel, visuals, wall normals, z-fighting where corridor floors overlap rooms by 0.6 m by design).
- Known constraint: `LevelBuilder` must stay at the world origin with identity transform (vertices are authored in layout coordinates).

## Status 2026-10-05 (v2)

- v2 adds hospital archetypes, furniture, lights, ceilings, mitred wall rings (no corner gaps), fog/ambient, jump/crouch/slide.
- 37/37 edit-mode tests; `HeadlessVerify` passes seeds 0..19 incl. NavMesh floor-only (max y 0.08) and full reachability.
- Lesson recorded: radial clearance around a corridor mouth does not keep a room navigable; the rule is now a clear lane from mouth to centre (`RoomFurnisher.MouthLane`) plus a 0.25 m walkability grid gate during placement. Core prop AABBs and Unity BoxColliders were verified identical, so Core's validator is the source of truth for navigability.
- Scene file stores LevelBuilder settings; after changing Core defaults run `SceneBootstrap.CreateMainScene` or the stale serialized values win.

## Status 2026-10-09 (v3, team repo)

- Moved into the team repository on branch `carson/level-generation`; ported to URP (URP Lit materials, Forward+, additional light shadows).
- Room templates (`RoomTemplateSpec` / `RoomTemplateAsset`): prefab-backed rooms get a generated floor and corridor openings only on allowed sides; door-count constraints; `SpawnMaxDoors = 1` makes the spawn a dead end.
- `LevelGenProfile` ScriptableObject holds every setting, template list, prop/light prefab maps and materials. The default lives in `Assets/LevelGen/Resources/` and `LevelBuilder` loads it by name when its scene reference is empty.
- Pluggable `ILevelFurnisher`; `OnRoomBuilt` / `OnCorridorBuilt` hooks; `RoomObjects` lookup.
- Verified headlessly: 48/48 tests, 20/20 seeds (spawn has exactly one door, prefab room placed, NavMesh floor-only and fully reachable, prefab colliders inside their footprint).
- Lessons: prefab footprints must be measured in the prefab root's local space (a root parked at z = -10 inside the asset moved the whole room 10 m); a sealed prefab (no doorways yet) is kept to one door so it can never cut the level, and its interior is reported as an authoring warning rather than a failure.
