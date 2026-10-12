# How level generation works

Written for teammates who have **not** read the generator code. No C# below until the FAQ.

If you only need one sentence: *we pick a handful of squares on an invisible grid, put one room in each,
connect neighbouring squares with straight corridors, then fill the rooms with hospital furniture and
flickering lights — and the whole thing is reproducible from a single number called the seed.*

---

## 1. The lattice and the random growth

Picture an invisible 4x4 grid of big squares ("cells"), each 30 m across. Every room sits in the middle of
exactly one cell. Because of that, **two rooms can never overlap** — the grid does the collision avoidance
for us, for free.

To choose which cells get rooms, the generator *grows*:

1. Pick a random starting cell. That one becomes the **spawn room**.
2. Repeatedly pick any cell we already have, pick one of its empty north/east/south/west neighbours, and take
   it too. Each step records the link it grew along as a corridor.
3. Stop after somewhere between 5 and 9 cells.

Because every new cell is attached to a cell we already had, the result is always **one connected blob** —
you can never generate an island you cannot walk to.

Then a second pass adds a few *loops*: any two chosen cells that happen to be side by side but are not yet
connected get a corridor with roughly a 1-in-3 chance. Loops are what stop the level feeling like a
corridor-and-cul-de-sac tree, and they give the enemy more than one way to come at you.

```
   lattice (4x4)              chosen cells + growth         + one loop edge
   . . . .                     . . . .                       . . . .
   . . . .                     . B-C . <- grown east         . B-C .
   . . . .                     . | |                         . | | |      <- the extra
   . . . .                     . A D .                       . A-D .         loop corridor
                                 ^spawn
   A = spawn (grown from),  B,C,D = later cells,  "-" and "|" = corridors
```

A couple of rules fall out of this and are worth knowing:

- A room is always smaller than its cell (max 20 m in a 30 m cell), so **every corridor has real length** —
  rooms never touch.
- The **spawn room is deliberately a dead end**: the generator makes it a leaf of the growth tree and refuses
  to attach loop corridors to it. One door in, one door out, and that door is also your escape (see §5).

## 2. Rooms

Each room gets:

- a **shape** — a rectangle, or an ellipse about 40% of the time (same footprint, rounded walls);
- a **size** — each axis rolled independently between 7 m and 20 m, so rooms are rarely square;
- walls (a continuous mitred ring, so there are no gaps at the corners), a floor at `y = 0`, and a ceiling at
  3 m. The ceiling has **no collider** on purpose — see §6.

Where a corridor meets a room, the wall ring simply has an opening cut in it, 3.2 m wide.

## 3. Corridors

Corridors in this version are **always straight and always axis-aligned**, because they only ever join two
side-by-side cells. Each one is:

- **3.2 m wide** of clear walkable floor (hospital-scale: a gurney plus a person),
- 3 m high with its own ceiling and a line of strip lights every ~7 m,
- occasionally cluttered against the walls with a gurney or a wheelchair, never in the middle.

Inside each room, the strip of floor running from a corridor opening ("mouth") to the room centre is a
protected **mouth lane**: 3.2 m plus a 1 m margin, and **no furniture is ever allowed into it**. This is the
single most important rule in the furniture placer. It is what guarantees that from any doorway you can walk
to the middle of the room, and from the middle out through any other doorway. We learned it the hard way: a
simple "keep a circle clear around the door" rule was not enough and left rooms the AI could not cross.

The corridor data is stored as a *polyline* rather than a straight segment, so bent or curved corridors can be
added later without anything downstream having to change.

## 4. Archetypes, furniture and lights

Every room is assigned a hospital **archetype**, which decides what goes in it:

| Archetype | Typically contains |
|---|---|
| `PatientRoom` | 1–2 beds against the walls, a bedside cabinet, sometimes an IV stand |
| `Ward` | many beds in rows along the long walls |
| `Office` | desk, chair, filing cabinet, desk lamp |
| `NurseStation` | a long counter, chairs, cabinets |
| `Storage` | shelves and cabinets along the walls, dim |
| `Lobby` | sparse — benches and a reception counter (usually the spawn room) |

Furniture is pushed against walls, never into a mouth lane, never overlapping another piece, and always with a
walkable gap (0.6 m) around it. Every piece gets a collider, so the enemy's navigation treats it as a real
obstacle rather than something to walk through.

Lighting is one ceiling lamp per ~45 m² of floor (max 4 per room), plus desk lamps that sit on desks and
counters, plus the corridor strips. Then the horror dial: roughly **15% of rooms are completely dark** (the
fixtures exist, they are just dead) and roughly **25% of lamps flicker**. Fog and near-black ambient light
are applied by the builder, so you genuinely cannot see across a big room.

## 5. Keys and spawn

- The starting cell is the **spawn room**, and it is also the **exit**: the objective is to collect all three
  keys and come back here.
- **Three** of the other rooms are chosen as **key rooms**, preferring rooms at least two corridors away from
  spawn so the first key is never sitting in the doorway.
- Spawn and key positions are exposed as data, so gameplay code places the actual pickups — the generator
  does not own the key objects.

## 6. The NavMesh bake and the enemy-AI contract

After the geometry exists, the generator bakes a **NavMesh** at runtime (package `com.unity.ai.navigation`,
agent radius 0.5 m, height 2 m) by collecting the colliders it just created.

Two details that explain odd-looking decisions in the code:

- **Ceilings have no colliders.** A collider 3 m up is a second walkable surface as far as the bake is
  concerned, and the enemy would happily patrol the ceiling. No collider, no bake.
- The verifier asserts that **no NavMesh vertex sits above y = 0.5 m**, which catches exactly that bug class
  (a bake on a ceiling or on a tabletop).

For the enemy AI, the contract is:

```csharp
builder.OnLevelBuilt += layout => { /* NavMesh is ALREADY baked when this fires */ };
```

and inside it you have:

| What you want | Where it is |
|---|---|
| room positions | `layout.Rooms[i].Center` (plus `SizeX`/`SizeZ`) |
| the room graph for patrol routes | `layout.Neighbours(roomId)` |
| where the player starts | `builder.PlayerSpawnPoint` (a `Vector3`) |
| a room's world centre | `builder.RoomWorldCenter(roomId)` |
| the spawn / key rooms | `layout.SpawnRoom`, `layout.KeyRooms` |
| per-room GameObjects as they appear | `builder.OnRoomBuilt(Room, GameObject)` |

`OnLevelBuilt` fires on **every** rebuild, including when a tester presses `R`, so anything you spawn in
response must be parented under the level or cleaned up — do not assume it fires once.

## 7. Determinism

Everything above is driven by **one integer seed**. Same seed, same settings, byte-identical level, every
time, on every machine. The layout logic is pure C# with no Unity dependency and no reliance on `UnityEngine.Random`,
which is exactly why it can be unit-tested over hundreds of seeds without opening the editor.

Practically: if you see a bad level, **write down the seed from the HUD**. That seed reproduces it exactly,
and it is the only bug report we can act on quickly.

Changing a generation *setting* changes the output for a given seed. The seed is not a level ID across
versions — it is a level ID for a fixed set of settings.

---

# FAQ

### "If we gave you a prefab could we generate a floor under it?"

**Yes.** You make a `RoomTemplateAsset` with your prefab in it, and the generator picks a lattice cell, sizes
the room to the prefab's footprint, generates the floor (and optionally walls and ceiling) under it, cuts
corridor openings only on the sides you allow, and skips furniture and lights so your prefab stays yours.

In other words: we own the *placement* and the *floor*, you own the *contents*. The teammate-built
`Assets/RoomFolder/Prefabs/Floor.prefab` is already wired up this way as a working example
(`Assets/LevelGen/Templates/TeammateRoom.asset`) — its footprint was measured off its own renderers rather
than typed in by hand.

Authoring rules for the prefab, so the generator can line things up:

1. **Origin at the floor centre.** The prefab's root transform must sit at the middle of the room's footprint,
   not at a corner — the generator places the root at the room centre.
2. **Floor at `y = 0`.** The generated floor slab is at `y = 0`; anything authored above or below that gives
   you a visible step or a z-fighting sandwich.
3. **Doorways on the axis midpoints of the sides you flag.** Corridors always arrive at the *middle* of a side
   (the centre of the north wall, the centre of the east wall, …). Put your doorway there, on each side whose
   flag you turn on, and leave the other sides solid.
4. **Doorways at least 3.2 m wide** (that is `CorridorWidth`), and 3 m of headroom. Narrower and the player
   and the enemy will both scrape; much narrower and the NavMesh will not connect through it at all.
5. **Colliders on the walls.** The NavMesh is baked from colliders, so a wall with only a renderer is a wall
   the enemy walks through. Conversely, do not put a collider on your ceiling (see §6).
6. Keep the footprint **under 20 m** on each axis so it still fits in a lattice cell with corridor room to
   spare, and remember the measured size is rounded *up* to the nearest 0.5 m.

The default profile lives at `Assets/LevelGen/Resources/DefaultLevelGenProfile.asset`; if a scene's `LevelBuilder`
has no profile assigned it loads that one by name, so a fresh scene with just a `LevelBuilder` still works.

If you only have a doorway on one side, flag only that side — the generator will then only place your room on
a cell where a single corridor arrives from that direction, and treat it as a dead end.

### "Is it possible for a spawn room with only one entrance?"

**Yes — that is now the default.** `SpawnMaxDoors = 1`: the spawn room is forced to be a leaf of the corridor
tree, and the loop pass is forbidden from attaching a second corridor to it. The headless verifier asserts
`spawnRoom.DoorCount == 1` on every one of its 20 seeds, so it cannot regress quietly.

If you want a *specific* room to be the spawn room, `Assets/LevelGen/Templates/SpawnRoom_OneDoor.asset` shows
how: a template with `affinity = SpawnOnly`, `maxDoors = 1`, and `forcedType = Lobby`. Point its `prefab` field
at your own prefab and your hand-built lobby becomes the spawn room, with one entrance, in every level.
