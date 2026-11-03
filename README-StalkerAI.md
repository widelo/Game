# CS 462 — A* stalker implementation

This is an **additive patch for the inspected Game-AStar-Review.zip**, not a standalone Unity project. Copy its `Assets`, `Docs`, and `Tools` folders and this README into `C:\Users\esteb\Game` on a new branch. The patch adds files; it does not replace the supplied player, enemy, level-generation, scene, package, or project-settings files.

1. Close Unity. In **Command Prompt**, run:
   ```bat
   cd /d C:\Users\esteb\Game
   git status --short
   git switch -c feat/esteban-astar-stalker
   ```
   Preserve any existing uncommitted work. Do not reset or clean it.
2. Extract this ZIP to a separate folder. Copy its contents into the repository root, merging folders. If you already have files with these new names, review that earlier installation before overwriting them.
3. Open the project with **Unity 6000.6.2f1**, the version recorded in your ZIP. Allow scripts to compile.
4. Open **Assets/Scenes/Main_AStar.unity** and press Play. Alternatively use **CS462 > A Star > Open Demo Scene**. Watch the top-right grid-build progress before expecting an enemy.
5. Select **AStarEnemies** to tune navigation and behavior. Select its spawned **AStarStalker_0** child to see senses and path Gizmos. Use the existing **L** lighting and **Tab** overhead controls while diagnosing geometry.
6. Open **Window > General > Test Runner**. Run the new **StalkerAI.EditMode.Tests** and **StalkerAI.PlayMode.Tests** assemblies. Read the validation report before treating the integration as verified in Unity.

The original **Main.unity** remains the team's legacy demo. The new scene disables its legacy EnemySpawner/HUD and uses a copied level profile with **Bake Nav Mesh off**. Its boss has a CharacterController and genuine C# A*; no NavMeshAgent is attached by the new system.

The defaults provide 100 health, 25 damage per confirmed hit, a 0.9-second attack windup, 2.5-second attack cooldown, 12-second maximum chase, and a 6-second disengagement. An actual defeat uses the project's existing player-respawn pattern, followed by protection. Tune these through the Inspector.

- [Complete architecture, setup, learning and testing guide](Docs/StalkerAI/Guide.md)
- [Validation results and remaining checks](Docs/StalkerAI/Validation.md)
- [Exact added-file inventory](Docs/StalkerAI/Files.md)

This package was created from the uploaded archive. It has not been copied into your Windows repository, committed, or pushed.
