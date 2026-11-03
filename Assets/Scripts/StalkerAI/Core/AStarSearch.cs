using System;
using System.Collections.Generic;

namespace CS462.StalkerAI.Core
{
    public enum SearchStatus { Idle, Searching, Complete, NoPath, InvalidEndpoint }

    /// <summary>Resumable, genuine A*. No Unity types or built-in pathfinding calls.</summary>
    public sealed class AStarSearch
    {
        GridGraph graph;
        double[] g, h, f;
        int[] parent;
        bool[] closed;
        IndexedMinHeap open;
        int goal;
        Func<int, int, bool> edgeAllowed;
        readonly List<int> path = new List<int>();

        public SearchStatus Status { get; private set; }
        public IReadOnlyList<int> Path => path;
        public int Expanded { get; private set; }
        public int OpenCount => open == null ? 0 : open.Count;
        public double PathCost { get; private set; } = double.PositiveInfinity;

        public void Begin(GridGraph graph, int start, int goal, Func<int, int, bool> edgeAllowed = null)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            this.graph = graph; this.goal = goal;
            this.edgeAllowed = edgeAllowed;
            path.Clear(); Expanded = 0; PathCost = double.PositiveInfinity;
            if (g == null || g.Length != graph.Count)
            {
                g = new double[graph.Count]; h = new double[graph.Count]; f = new double[graph.Count];
                parent = new int[graph.Count]; closed = new bool[graph.Count];
                open = new IndexedMinHeap(graph.Count, f, h);
            }
            open.Clear();
            for (int i = 0; i < graph.Count; i++)
            { g[i] = double.PositiveInfinity; parent[i] = -1; closed[i] = false; }
            if (!graph.IsWalkable(start) || !graph.IsWalkable(goal))
            { Status = SearchStatus.InvalidEndpoint; return; }
            g[start] = 0;
            h[start] = graph.Heuristic(start, goal); f[start] = h[start];
            open.PushOrDecrease(start);
            Status = SearchStatus.Searching;
        }

        /// <summary>A budget pauses work; it never labels an unfinished search unreachable.</summary>
        public SearchStatus Step(int expansionBudget)
        {
            if (expansionBudget < 1) throw new ArgumentOutOfRangeException(nameof(expansionBudget));
            if (Status != SearchStatus.Searching) return Status;
            while (expansionBudget-- > 0 && open.Count > 0)
            {
                int current = open.Pop();
                if (current == goal)
                {
                    PathCost = g[goal];
                    for (int n = goal; n != -1; n = parent[n]) path.Add(n);
                    path.Reverse();
                    return Status = SearchStatus.Complete;
                }
                closed[current] = true; Expanded++;
                for (int d = 0; d < 8; d++)
                {
                    if (!graph.HasEdge(current, d)) continue;
                    int next = graph.Neighbour(current, d);
                    // Octile h is consistent for these edge costs, so closed nodes need no reopening.
                    if (closed[next]) continue;
                    if (edgeAllowed != null && !edgeAllowed(current, next)) continue;
                    double candidate = g[current] + graph.Cost(d);
                    if (candidate >= g[next]) continue;
                    parent[next] = current; g[next] = candidate;
                    h[next] = graph.Heuristic(next, goal); f[next] = g[next] + h[next];
                    open.PushOrDecrease(next);
                }
            }
            if (open.Count == 0) Status = SearchStatus.NoPath;
            return Status;
        }
    }
}
