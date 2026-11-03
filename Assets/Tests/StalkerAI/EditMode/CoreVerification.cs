using System;
using CS462.StalkerAI.Core;

namespace CS462.StalkerAI.Tests
{
    /// <summary>Same checks run from Unity/NUnit and the optional dotnet console harness.</summary>
    public static class CoreVerification
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        static GridGraph Build(int width, int depth, bool[] open)
        {
            var graph = new GridGraph(width, depth, 1.0);
            for (int n = 0; n < graph.Count; n++) graph.SetWalkable(n, open[n]);
            for (int n = 0; n < graph.Count; n++)
                for (int d = 0; d < 8; d++) graph.Connect(n, d);
            return graph;
        }

        static AStarSearch Solve(GridGraph graph, int start, int goal, int budget = 10000, Func<int, int, bool> filter = null)
        {
            var search = new AStarSearch(); search.Begin(graph, start, goal, filter);
            int steps = 0;
            while (search.Status == SearchStatus.Searching)
            {
                search.Step(budget);
                Require(++steps <= graph.Count + 1, "Search failed to terminate.");
            }
            return search;
        }

        public static void EmptyGridAndIdentity()
        {
            var flags = new bool[80]; for (int n = 0; n < flags.Length; n++) flags[n] = true;
            GridGraph graph = Build(10, 8, flags);
            AStarSearch s = Solve(graph, 0, 79);
            Require(s.Status == SearchStatus.Complete, "Open grid should be connected.");
            Require(Math.Abs(s.PathCost - (7 * GridGraph.Diagonal + 2)) < 1e-9, "Octile path cost is wrong.");
            s = Solve(graph, 20, 20);
            Require(s.Path.Count == 1 && s.Path[0] == 20 && s.PathCost == 0, "Identity path is wrong.");
        }

        public static void NoCornerCuttingAndInvalidEndpoints()
        {
            GridGraph graph = Build(2, 2, new[] { true, false, false, true });
            AStarSearch s = Solve(graph, 0, 3);
            Require(s.Status == SearchStatus.NoPath && s.Path.Count == 0, "Diagonal corner cutting allowed.");
            s = Solve(graph, 1, 3);
            Require(s.Status == SearchStatus.InvalidEndpoint && s.Path.Count == 0, "Blocked endpoint accepted.");
            s = Solve(graph, -1, 3);
            Require(s.Status == SearchStatus.InvalidEndpoint, "Off-grid endpoint accepted.");
        }

        public static void EdgesMatterAndSearchCanBeReused()
        {
            var graph = new GridGraph(3, 1, 1.0);
            for (int i = 0; i < 3; i++) graph.SetWalkable(i, true);
            graph.Connect(0, 0); // 1->2 deliberately absent: a thin wall between clear node centres.
            var search = new AStarSearch(); search.Begin(graph, 0, 2);
            while (search.Status == SearchStatus.Searching) search.Step(1);
            Require(search.Status == SearchStatus.NoPath, "Search crossed a missing edge.");
            search.Begin(graph, 0, 1);
            while (search.Status == SearchStatus.Searching) search.Step(1);
            Require(search.PathCost == 1 && search.Path.Count == 2, "Reuse retained stale search state.");
        }

        public static void BudgetPauseAndDeterminism()
        {
            var flags = new bool[400]; for (int n = 0; n < flags.Length; n++) flags[n] = true;
            GridGraph graph = Build(20, 20, flags);
            var paused = new AStarSearch(); paused.Begin(graph, 0, 399);
            Require(paused.Step(1) == SearchStatus.Searching && paused.OpenCount > 0, "Budget exhaustion incorrectly means unreachable.");
            AStarSearch one = Solve(graph, 0, 399, 1), all = Solve(graph, 0, 399);
            Require(one.PathCost == all.PathCost && one.Path.Count == all.Path.Count, "Budget changed the result.");
            for (int i = 0; i < one.Path.Count; i++) Require(one.Path[i] == all.Path[i], "Tie breaks are not deterministic.");
        }

        public static void LiveEdgeFilterAndIndependentSearches()
        {
            var flags = new bool[25]; for (int n = 0; n < flags.Length; n++) flags[n] = true;
            GridGraph graph = Build(5, 5, flags);
            Func<int, int, bool> filter = (a, b) => a != 12 && b != 12;
            AStarSearch s = Solve(graph, 0, 24, 1, filter);
            Require(Math.Abs(s.PathCost - Dijkstra(graph, 0, 24, filter)) < 1e-9, "Filtered graph route isn't optimal.");
            Require(s.PathCost > 4 * GridGraph.Diagonal, "Dynamic blocked centre was ignored.");
            var aSearch = new AStarSearch(); var bSearch = new AStarSearch();
            aSearch.Begin(graph, 0, 24); bSearch.Begin(graph, 4, 20);
            while (aSearch.Status == SearchStatus.Searching || bSearch.Status == SearchStatus.Searching)
            { aSearch.Step(1); bSearch.Step(1); }
            Require(aSearch.PathCost == bSearch.PathCost && aSearch.Path[0] == 0 && bSearch.Path[0] == 4,
                    "Independent search records interfered.");
        }

        public static void RandomizedOptimalityAgainstDijkstra()
        {
            var random = new Random(462);
            for (int trial = 0; trial < 1000; trial++)
            {
                const int width = 13, depth = 11;
                var flags = new bool[width * depth];
                for (int n = 0; n < flags.Length; n++) flags[n] = random.NextDouble() > 0.3;
                int start = random.Next(flags.Length), goal = random.Next(flags.Length);
                flags[start] = flags[goal] = true;
                GridGraph graph = Build(width, depth, flags);
                AStarSearch s = Solve(graph, start, goal, trial % 17 + 1);
                double expected = Dijkstra(graph, start, goal);
                if (double.IsPositiveInfinity(expected))
                { Require(s.Status == SearchStatus.NoPath && s.Path.Count == 0, "Disconnected grid returned a path."); continue; }
                Require(s.Status == SearchStatus.Complete, "Connected grid reported failure.");
                Require(Math.Abs(expected - s.PathCost) < 1e-8, $"Nonoptimal route in trial {trial}.");
                Require(s.Path[0] == start && s.Path[s.Path.Count - 1] == goal, "Wrong endpoints.");
                double reconstructed = 0;
                for (int i = 1; i < s.Path.Count; i++)
                {
                    bool found = false;
                    for (int d = 0; d < 8; d++)
                    {
                        if (!graph.HasEdge(s.Path[i - 1], d) || graph.Neighbour(s.Path[i - 1], d) != s.Path[i]) continue;
                        reconstructed += graph.Cost(d); found = true; break;
                    }
                    Require(found, "Reconstruction contains a non-edge.");
                }
                Require(Math.Abs(reconstructed - expected) < 1e-8, "Reported cost differs from actual path.");
                Require(graph.Heuristic(start, goal) <= expected + 1e-8, "Heuristic overestimates.");
                for (int n = 0; n < graph.Count; n++)
                    for (int d = 0; d < 8; d++)
                        if (graph.HasEdge(n, d)) Require(graph.Heuristic(n, goal)
                            <= graph.Cost(d) + graph.Heuristic(graph.Neighbour(n, d), goal) + 1e-8,
                            "Heuristic is inconsistent.");
            }
        }

        // Independent O(V^2) oracle. No A* heuristic and no shared heap implementation.
        static double Dijkstra(GridGraph graph, int start, int goal, Func<int, int, bool> filter = null)
        {
            var distance = new double[graph.Count]; var visited = new bool[graph.Count];
            for (int n = 0; n < graph.Count; n++) distance[n] = double.PositiveInfinity;
            distance[start] = 0;
            for (int step = 0; step < graph.Count; step++)
            {
                int current = -1;
                for (int n = 0; n < graph.Count; n++)
                    if (!visited[n] && (current < 0 || distance[n] < distance[current])) current = n;
                if (current < 0 || double.IsPositiveInfinity(distance[current])) break;
                if (current == goal) return distance[current];
                visited[current] = true;
                for (int d = 0; d < 8; d++)
                {
                    if (!graph.HasEdge(current, d)) continue;
                    int next = graph.Neighbour(current, d);
                    if (filter != null && !filter(current, next)) continue;
                    distance[next] = Math.Min(distance[next], distance[current] + graph.Cost(d));
                }
            }
            return double.PositiveInfinity;
        }

        public static void RunAll()
        {
            EmptyGridAndIdentity(); NoCornerCuttingAndInvalidEndpoints(); EdgesMatterAndSearchCanBeReused();
            BudgetPauseAndDeterminism(); LiveEdgeFilterAndIndependentSearches(); RandomizedOptimalityAgainstDijkstra();
        }
    }
}
