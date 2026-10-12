# Extending the level generator — cookbook

Every recipe below ends with the same step: **run the verify loop** (bottom of this file). If you skip it, the
failure shows up as "the level is broken on some seeds", which is the most expensive kind of bug we have.

Read `docs/HOW-GENERATION-WORKS.md` first if you have not. Layer map is in `docs/ARCHITECTURE.md`.

Quick orientation — who owns what:

| You want to change | You touch |
|---|---|
| *which* rooms exist, how big, how connected | `Assets/LevelGen/DefaultLevelGenProfile.asset` (an asset, not code) |
| a specific kind of room (prefab or forced archetype) | a `RoomTemplateAsset` in `Assets/LevelGen/Templates/` |
| what furniture/lights a room gets | `LevelGen.Core/RoomFurnisher.cs`, or your own `ILevelFurnisher` |
| the look of generated props/lights | the prefab maps on the profile |
| geometry (walls, floors, corridors) | `LevelGen.Unity` (`RoomMeshBuilder`, `WallRingGeometry`) |
| the layout algorithm itself | `LevelGen.Core/LevelGenerator.cs` — PR discussion first |

---

## 1. Add a prefab room (your own hand-built room)

1. Build your room as a prefab. Follow the six authoring rules in the FAQ of
   `docs/HOW-GENERATION-WORKS.md` — origin at the floor centre, floor at `y = 0`, doorways on the axis
   midpoints of the sides you will flag, ≥ 3.2 m wide, colliders on the walls, footprint under 20 m.
2. `Assets/LevelGen/Templates/` → right-click → **Create ▸ CS462 ▸ Room Template**. Name it after the room.
3. Fill it in:
   - **prefab** → your prefab. **prefabOffset** → usually `(0,0,0)`; nudge it only if your origin is off.
     The generator places the prefab's *root* at the room centre, so what matters is where your geometry sits
     relative to the root, not where the root is parked inside the prefab asset. `CS462 > Create Main Scene`
     measures this for the sample template and logs it.
   - Until your prefab has real doorways, keep **maxDoors = 1**. A sealed room as a dead end is harmless; a
     sealed room in the middle of the level cuts it in half. The verifier reports a sealed interior as a
     warning ("prefab authoring") and fails only if the prefab's colliders leak outside the room footprint.
   - **fixedSizeX / fixedSizeZ** → the prefab's footprint in metres, rounded **up** to 0.5 m. Easiest way to
     get this right: copy what `CS462/Create Default Level Gen Assets` logged for `Floor.prefab`
     (`[SceneBootstrap] measured ...`), or just run that measurement logic on yours. Leave both at `0` only if
     you want a procedural footprint.
   - **shape** → `Rectangle` (a prefab room is never an ellipse).
   - **door sides** → turn on only the sides that actually have a doorway. **minDoors / maxDoors** → `1` and
     the number of sides you flagged.
   - **Generate…** → `GenerateFloor = true`, and `GenerateWalls` / `GenerateCeiling` / `GenerateProps` /
     `GenerateLights` **false**, so we put a floor under your room and otherwise leave it alone. Turn
     `GenerateWalls` on if you only built the *interior* and want our wall ring around it.
   - **weight** → relative chance of being picked. The plain `Procedural.asset` template sits at `4`, so a
     weight of `1` means your room shows up regularly without taking over the hospital.
   - **affinity** → `Any`, or `SpawnOnly` / `KeyOnly` / `NeverSpawn` to pin its role.
   - **floorMaterialOverride** → optional, if our concrete does not match your room.
4. Add the asset to `DefaultLevelGenProfile`'s **templates** list.
5. Verify. The headless verifier checks the prefab was actually instantiated under `Room_<id>_…` and that a
   floor exists under it. If your template is never picked, the per-seed `templates={...}` line will show it
   missing — usually a footprint that does not fit a cell, or door flags no cell can satisfy.

## 2. Add a prop prefab for a `PropType`

By default props are grey boxes sized from `PropDims`. To use real art — e.g. the hospital bed at
`Assets/CobleGames/Prefabs/hospital bed.prefab`:

1. Select `Assets/LevelGen/DefaultLevelGenProfile.asset`.
2. In the **prop prefab map**, set the entry for `HospitalBed` to that prefab.
3. Make sure the prefab's **origin is at the centre of its footprint on the floor** (`y = 0` at the bottom)
   and that it faces **local +X**. The generator rotates props in 90° steps about Y; a model that faces -Z
   will spawn facing into the wall.
4. Check the real footprint against `PropDims` in `LevelGen.Core`. The placer reserves the *declared* box, so
   a model wider than its declaration will clip walls and other furniture. If it is genuinely a different size,
   change `PropDims` (a Core change — see §4) rather than scaling the prefab to fit a lie.
5. Give it a collider that matches the footprint. Props are NavMesh obstacles; no collider means the enemy
   walks through your bed.

Same recipe for the chair (`Assets/GeniusCrate_Games/Chair_sofa_set(interior)/Prefab/Chair_sofa_set/Chair_01.prefab`)
on `PropType.Chair`.

## 3. Add a light prefab

1. In the profile's **light prefab map**, set `CeilingLamp` (or `DeskLamp` / `WallSconce` / `CorridorStrip`) to
   e.g. `Assets/PolyKebap/LED Light Essentials Pack/Prefabs/ON/LED_Light_3.prefab`.
2. The prefab should contain the **visible fixture**, with its origin at the **mounting point** (the ceiling
   end for a lamp), hanging down in local -Y.
3. If the prefab contains its own `Light` component, the builder drives that one — intensity, range, colour
   and the dead/flicker behaviour come from the layout, not from the prefab. Author the prefab's light as a
   *point* light; the generated data assumes omni.
4. Keep colliders **off** light prefabs. A collider at ceiling height is the ceiling-bake bug all over again.
5. Watch the budget: a level has 20+ visible real-time lights. The URP asset is set to per-pixel additional
   lights with shadows and a limit of 8 per object (`CS462/Apply URP Settings`). A fixture prefab that adds a
   second or third light each is how you turn a 60 fps level into a 20 fps one.

## 4. Add a new `RoomType` or `PropType`

This is the one change that touches several files in `LevelGen.Core`. In order:

1. **The enum** — `RoomContents.cs`: add your member to `RoomType` or `PropType`. Add it at the **end**;
   inserting in the middle renumbers the enum and silently rewrites every asset that stored the old value.
2. **`PropDims`** (new `PropType` only) — the table at the top of `RoomFurnisher.cs`: declare the footprint and height. This is the box the placer
   reserves and the box the collider gets; get it right before you look at art.
3. **The furnisher** — `RoomFurnisher.cs`: add a `case` for your new `RoomType` describing what it furnishes
   with, or add your new `PropType` to the archetypes that should use it. Keep the existing discipline: push
   against walls, never into a mouth lane, honour `PropSpacing`.
4. **The validator** — `LevelValidator.cs`: if your addition carries a new invariant (e.g. "an operating
   theatre always has exactly one table"), assert it here. The validator is the source of truth for
   navigability and is what the seed-sweep tests run.
5. **Unity side** — add a prefab-map entry on the profile if you want real art (§2 / §3).
6. **Tests will guard it.** The edit-mode tests sweep hundreds of seeds through `LevelValidator`, so a new
   archetype that can produce an unnavigable room *will* fail them rather than reaching a playtest. Expect to
   add a test for your invariant too; that is the point of the layer being pure C#.

## 5. Replace the furnisher

Furnishing is behind an interface, so you can swap it wholesale without touching the layout:

```csharp
public sealed class MyFurnisher : LevelGen.Core.ILevelFurnisher
{
    public void Furnish(LevelLayout layout, System.Random rnd)
    {
        foreach (var room in layout.Rooms)
        {
            // fill room.Props / room.Lights
        }
    }
}
```

Rules you must keep, because downstream code and the validator assume them:

- Use **only** the `System.Random` you are handed. Any other source of randomness destroys determinism, and
  determinism is what the entire test suite rests on.
- Never place a prop in a **mouth lane**, and respect `PropSpacing`. Run `LevelValidator` on your output.
- Every room ends up with **at least one** light entry (it may be `Enabled = false` — a dead fixture is how a
  dark room is expressed, not an absent one).
- Positions are **world XZ**, in the same frame as `Room.Center`. Heights are metres from the floor.

Wrapping is usually better than replacing: call the default `RoomFurnisher` first, then add or remove.

## 6. Subscribe to `OnRoomBuilt` / `OnLevelBuilt`

```csharp
void OnEnable()
{
    builder.OnRoomBuilt     += HandleRoom;       // (Room, GameObject) — per room, as it is built
    builder.OnCorridorBuilt += HandleCorridor;   // (Corridor, GameObject)
    builder.OnLevelBuilt    += HandleLevel;      // (LevelLayout) — once, AFTER the NavMesh bake
}
void OnDisable()
{
    builder.OnRoomBuilt     -= HandleRoom;
    builder.OnCorridorBuilt -= HandleCorridor;
    builder.OnLevelBuilt    -= HandleLevel;
}
```

- **Key pickups** → `OnRoomBuilt`. You get the room (so `room.Role == RoomRole.Key`, its props, its archetype)
  *and* the GameObject to parent your pickup under, which means it is destroyed with the level automatically.
- **Enemy spawning / patrol setup** → `OnLevelBuilt`. This is the only point at which the NavMesh exists.
  Nothing you do before it can call `NavMesh.SamplePosition` and get a truthful answer.
- Both fire on **every** rebuild (`R`, `N`, `P`, the inspector buttons, the verifier's 20 seeds). Unsubscribe
  in `OnDisable`, and never keep a reference to geometry across a rebuild — it is destroyed.
- `builder.RoomObjects` and `builder.LevelRoot` let you find things after the fact if you missed the events.
- `builder.Profile` is the settings asset in use, if you need to read a value rather than hardcode it.

## 7. Tune the profile

`Assets/LevelGen/DefaultLevelGenProfile.asset` holds all generation settings. Change it, press `R` in Play
mode, look. The knobs worth knowing:

| Knob | Effect | Careful |
|---|---|---|
| `minRooms` / `maxRooms` | level size | more rooms = more lights = frame cost |
| `gridWidth` / `gridHeight` | how spread out the hospital is | must be big enough to hold `maxRooms` |
| `cellSize` | distance between room centres | **must stay > `maxRoomSize`** or corridors get zero length |
| `minRoomSize` / `maxRoomSize` | room footprints | `maxRoomSize < cellSize`, always |
| `loopEdgeChance` | 0 = pure tree (dead ends), 1 = maze of loops | |
| `spawnMaxDoors` | `1` = single-entrance spawn room | the verifier asserts `== 1` by default |
| `corridorWidth` | 3.2 m | your prefab doorways are built to this; changing it invalidates them |
| `mouthClearance` | margin on the protected lane | lowering it is how rooms become unnavigable |
| `darkRoomChance` / `flickerChance` | horror dial | |
| `ellipseChance` | round rooms | |
| `templates` | which room templates may be placed | weights are relative, not percentages |

Make one change at a time and verify. Two settings changed at once and a failing seed tells you nothing.

## 8. The verify loop

In rough order of speed:

1. **Test Runner** — `Window > General > Test Runner`, EditMode, Run All. Pure-C# tests over hundreds of
   seeds. Fastest, and catches every Core-layer mistake.
2. **`CS462/Rebuild Level In Scene`** — rebuild in the editor without entering Play. Fastest way to *look* at
   a layout.
3. **`CS462/Headless Verify (20 seeds)`** (or the **Verify 20 seeds** button on the `LevelBuilder`) — builds
   20 seeds, validates each layout, bakes the NavMesh, and path-checks from spawn to every room centre and
   corridor midpoint. This is the gate: if it is green, the level is playable.
4. **Headless from a shell** — the commands in `README.md`. What you run before opening a PR.

When something fails, the seed is in the log line. Set that seed in the builder and reproduce it in the
editor; do not debug from the aggregate.

---

## What NOT to touch

- **`LevelGen.Core/LevelLayout.cs` — the data contract.** `Room`, `Corridor`, `LevelGenSettings` and the
  enums are consumed by the Unity layer, the enemy AI and the test suite simultaneously. Adding a field is
  usually fine; **renaming, removing or reordering anything is a PR discussion first**, because it breaks
  other people's code and silently rewrites serialized assets.
- **`Assets/Scenes/SampleScene.unity`** — the team's scene. Not ours to edit from the level-gen branch.
- **`Assets/Scenes/Main.unity` by hand.** It is generated by `SceneBootstrap.CreateMainScene`. Inspector
  edits survive until someone regenerates it, and then they are gone — put the change in the bootstrap code
  or on the profile asset instead.
- **`Assets/LevelGen/*.asset` YAML by hand.** Create and edit these through the editor (or the `CS462` menu),
  never by typing YAML; a hand-written asset with a wrong `m_Script` GUID is a silent null reference.
- **`EditorBuildSettings` wholesale.** `SceneBootstrap` *appends* `Main.unity`; it must not replace the list,
  or `SampleScene` drops out of the build.
- **`LevelBuilder`'s transform.** It must stay at the world origin with an identity transform — mesh vertices
  are authored in layout coordinates, and `ToWorld` / `RoomWorldCenter` / `PlayerSpawnPoint` are pure
  functions of the layout, so moving the builder desyncs the geometry from every helper.
- **`Active Input Handling`** — it is `Both` because teammates use the Input System controller and the level-gen
  scene uses the legacy one. Switching it breaks one of the two players.
- **`UnityEngine.Random` inside `LevelGen.Core`.** Core has no Unity dependency and must not gain one;
  determinism comes from the seeded `System.Random` being the only source of randomness.
