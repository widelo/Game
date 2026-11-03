# Validation report

Source: supplied `Game-AStar-Review.zip`, Unity project version **6000.6.2f1**. These results describe the downloadable patch, not a run on the user's Windows computer.

| Check | Result | Scope |
|---|---|---|
| Original-file preservation | **PASS** | SHA-256 comparison of all **312 original files**: no replacements, edits or removals. |
| Pure C# compilation | **PASS** | Actual `GridGraph.cs`, `IndexedMinHeap.cs`, `AStarSearch.cs`, shared `CoreVerification.cs` and console `Program.cs` compiled with Microsoft's Roslyn compiler from SDK **8.0.426**, explicitly using C# 8 syntax and .NET 8 reference assemblies. |
| Executed algorithm checks | **PASS** | Compiled C# executable ran under **Microsoft.NETCore.App 8.0.32**. Six verification groups passed, including **1,000 seeded random graphs versus independent Dijkstra**. |
| C# source syntax | **PASS** | All **17 added C# files** parsed with the C# Tree-sitter grammar without syntax errors. This is not Unity compilation. |
| Assembly references | **PASS** | New assembly references resolve within the supplied project; complete local assembly graph is acyclic. New runtime does not reference Game.Enemy. |
| Unity metadata | **PASS** | No duplicate GUIDs; new Assets files have matching metadata; demo director/profile GUIDs resolve; demo-local object references resolve. |
| Demo profile isolation | **PASS** | Copied profile matches original data except its name and Bake Nav Mesh = false. Original profile is unchanged. |
| Legacy-enemy isolation | **PASS** | Only the new demo disables the old EnemySpawner/HUD and its spawn-on-build flag. Original Main scene is unchanged. |
| Built-in pathfinding exclusion | **PASS** | Syntax-tree scan of new core/runtime source found no identifiers for NavMeshAgent, NavMesh, NavMeshPath, NavMeshSurface or SetDestination. |
| Archive consistency | **PASS at packaging** | ZIP entries are additions only, with byte-for-byte comparison to the authored files and a successful archive integrity test. |
| Unity import/type checking | **NOT RUN** | Unity Editor and its runtime assemblies are not installed in the execution environment. |
| Unity Play Mode tests | **NOT RUN** | Five physics/behavior tests are supplied. They must run in Unity. |
| Actual Main_AStar gameplay | **NOT RUN** | Spawn placement, generated prefab clearance, rendering, controller collision and balance still require the acceptance checks in Guide.md. |
| Original project tests | **NOT RUN** | Existing files were preserved; preservation is not proof that the whole original project compiles/passes. |

Executed console output:

```text
PASS: six verification groups, including 1,000 seeded Dijkstra comparisons.
```

The SDK command-line launcher could not read process metadata in this environment, so compilation invoked the SDK's Roslyn `csc.dll` directly with .NET reference assemblies, then ran the compiled executable with the .NET host. The tested sources are the ones shipped in this patch, not a Python/JavaScript translation or a separate algorithm. The optional `dotnet run` project is supplied for ordinary local SDK installations.

The six groups checked:

1. Exact unobstructed octile cost and start=goal reconstruction.
2. No diagonal corner cutting, blocked endpoints and out-of-bounds endpoints.
3. Missing edges between walkable cells, unreachable destinations and reuse after failure.
4. Incremental budgets, continuation rather than false failure, and deterministic priority ties.
5. Live edge filtering and independent concurrent search records on one graph.
6. Random graph optimality, reconstructed edge validity/cost, heuristic admissibility and consistency against Dijkstra.

Remaining release gate on your computer: open **Main_AStar.unity** in the project's Editor version, resolve any Console compile errors, run both new test assemblies, then check several generated seeds, corner navigation, attack dodges, quiet escape and R/N/P rebuilds. Runtime behavior is implemented but is **not claimed to be Unity-verified** by this report.
