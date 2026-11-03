# CS 462 — A* Stalker AI

This is our team's A* pathfinding and stalker boss implementation for the CS 462 Unity game. It adds a separate enemy system that can navigate the procedurally generated hospital using a grid built from the scene's actual colliders. The new boss does **not** use `NavMeshAgent` or baked NavMesh paths.

The A* demo is separate from the original enemy setup so that existing team systems can continue to be developed independently.

## Running the A* demo

1. Pull the latest changes from the team repository and open the Unity project in **Unity 6000.6.2f1**.
2. Open **`Assets/Scenes/Main_AStar.unity`**. You can also use **CS462 > A Star > Open Demo Scene** in the Unity menu.
3. Press **Play**. The level generates first, followed by the A* navigation grid. Give the grid time to finish building before looking for the enemy.
4. In the Hierarchy, select **`AStarEnemies`** to view the **`AStarDirector`** settings. The default setup attempts to spawn one **`AStarStalker_0`** boss in a reachable location.
5. Select the spawned boss and enable **Gizmos** in the Scene view to inspect its senses and path. Use **L** for brighter debug lighting and **Tab** for the overhead view.

The standard demo controls are **WASD + mouse** to move/look, **Shift** to sprint, **Space** to jump, and **Ctrl/C** to crouch. **R** rebuilds the current seed; **N/P** changes seeds; **F1** toggles the level debug HUD.

## How the pathfinding works

When `LevelBuilder` finishes generating a level, `AStarDirector` builds a navigation grid based on walkable surfaces, walls, furniture, and other colliders. Each valid cell becomes a node in a graph. The search can move to up to eight neighboring cells, but it cannot cut diagonally through blocked corners.

A* evaluates nodes using:

- **G** — the known cost of traveling from the starting node.
- **H** — the estimated cost from that node to the goal, using octile distance for the eight-direction grid.
- **F = G + H** — the estimated total cost used to prioritize which node to explore next.

`AStarSearch` uses a priority queue to explore promising nodes and reconstruct the route. `AStarMotor` then follows that route using a `CharacterController`, checking movement clearance and requesting a new path when needed. Search work is spread over frames rather than running the entire search at once.

**The A* algorithm handles how the enemy gets somewhere. The boss state machine decides where it wants to go and why.**

## Stalker behavior

The boss uses five states:

| State | Behavior |
| --- | --- |
| **Patrol** | Moves between reachable areas while looking and listening for the player. |
| **Investigate** | Checks a last-seen position or the area where a sound was heard. |
| **Stalk** | Repositions around the player, sometimes waiting or using cover before committing to a chase. |
| **Chase** | Pursues the player's observed position and attempts an attack when in range. |
| **Disengage** | Temporarily backs off or waits, allowing the player a chance to escape. |

The enemy uses line of sight, a field of view, movement noises, and limited memory. It should not continuously know the player's position after losing sight. Sprinting and other loud movement can make the player easier to locate.

The demo also includes a **sample** damage system: **100 player health**, **25 damage per confirmed hit**, a **0.9-second attack windup**, and a **2.5-second attack cooldown**. Attacks can miss if the player moves out of range or breaks line of sight during the windup. This sample health system is not a replacement for any shared player-health system the team decides to build.

## Main files

| Path | Purpose |
| --- | --- |
| `Assets/Scripts/StalkerAI/Core/GridGraph.cs` | Navigation nodes, neighbors, edge costs, and heuristic support. |
| `Assets/Scripts/StalkerAI/Core/AStarSearch.cs` | A* search, node costs, path reconstruction, and search status. |
| `Assets/Scripts/StalkerAI/Core/IndexedMinHeap.cs` | Priority queue used by the search. |
| `Assets/Scripts/StalkerAI/Runtime/AStarNavigation.cs` | Builds the walkable grid from Unity physics and checks clearance. |
| `Assets/Scripts/StalkerAI/Runtime/AStarMotor.cs` | Moves the boss along the computed path. |
| `Assets/Scripts/StalkerAI/Runtime/StalkerBoss.cs` | Stalker state machine and attack behavior. |
| `Assets/Scripts/StalkerAI/Runtime/StalkerPerception.cs` | Vision, hearing, and remembered player positions. |
| `Assets/Scripts/StalkerAI/Runtime/AStarDirector.cs` | Connects level generation, navigation, and boss spawning. |
| `Assets/Scenes/Main_AStar.unity` | A* demo scene. |

Other supporting files handle behavior settings, noise emission, sample health, scene setup, and tests.

## Adjusting or integrating the system

Select **`AStarEnemies`** in the demo scene. The `AStarDirector` Inspector contains settings for grid cell size, navigation clearance, search work per frame, enemy count, movement speeds, sight/hearing, chase behavior, attacks, and debug display.

- Enable **Draw Grid** in the navigation settings, with Scene-view Gizmos enabled, to inspect which cells can be used.
- If navigation dimensions or cell size change during Play Mode, use **Rebuild A Star Navigation (Play Mode)** from the Director component's context menu.
- Adjust the settings on the Director while **not** in Play Mode if you want the changes saved. Spawned enemy objects are runtime instances.

The original **`Assets/Scenes/Main.unity`** remains separate. The A* scene uses **`Assets/StalkerAI/AStarLevelGenProfile.asset`** with NavMesh baking disabled, and disables the old `EnemySpawner`/`EnemyHud` only in this demo. Do not replace the team's existing enemy or player code just to try A*.

For another scene, **make a copy first** and use **CS462 > A Star > Install Into Current Scene**. The installer expects the project's `LevelBuilder` and `Game.Player.FirstPersonController`; scenes using another player controller need integration work. Review references and scene changes before saving.

## Testing

In Unity, open **Window > General > Test Runner** and run:

1. **`StalkerAI.EditMode.Tests`** — checks A* behavior, invalid paths, corner blocking, search budgets, and comparisons against Dijkstra's algorithm.
2. **`StalkerAI.PlayMode.Tests`** — checks Unity physics, perception, attacks, and disengagement behavior.

Also test the actual `Main_AStar` scene: verify the boss spawns, navigates around walls/furniture, reacts to sight and noise, can miss a dodged attack, and rebuilds correctly after **R/N/P**.

**Validation status:** Standalone C# algorithm checks passed during the original patch preparation, but Unity compilation, Unity test results, and complete gameplay behavior were **not verified** in that report. Run the Unity tests and gameplay checks before marking the integration complete.

## Current limits

The navigation system targets the game's current **single-level, mostly flat hospital layout**. It does not handle stairs, elevators, stacked floors, or full crowd avoidance. Opening new passages or changing the underlying level geometry may require a navigation-grid rebuild. The current monster is a static model; finished animation, sound, and combat presentation are separate work.

## More documentation

- [Detailed architecture and integration guide](docs/StalkerAI/Guide.md)
- [Validation report and remaining checks](docs/StalkerAI/Validation.md)
- [A* added-file inventory](docs/StalkerAI/Files.md)

These documents include some original patch-development notes; this README describes how to use the code **already present in the shared repository**.
