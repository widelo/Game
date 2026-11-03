using System.Collections;
using System.Collections.Generic;
using CS462.StalkerAI.Core;
using UnityEngine;

namespace CS462.StalkerAI
{
    /// <summary>Samples the real colliders on this project's flat floor. Never reads a NavMesh.</summary>
    public sealed class AStarNavigation
    {
        public NavigationSettings Settings { get; }
        public GridGraph Graph { get; private set; }
        public bool Ready { get; private set; }
        public float BuildProgress { get; private set; }
        public int WalkableCount { get; private set; }
        public string Error { get; private set; }
        public readonly List<int> WalkableNodes = new List<int>();
        public int[] Components { get; private set; }

        readonly Transform playerRoot, actorsRoot;
        readonly Collider[] overlaps = new Collider[128];
        readonly RaycastHit[] hits = new RaycastHit[128];
        Vector3 origin;
        bool warnedOverflow;

        public AStarNavigation(NavigationSettings settings, Transform playerRoot, Transform actorsRoot)
        { Settings = settings.Snapshot(); Settings.Validate(); this.playerRoot = playerRoot; this.actorsRoot = actorsRoot; }

        /// <summary>Call after deferred destruction of the previous level, then Physics.SyncTransforms.</summary>
        public IEnumerator Build(Transform levelRoot)
        {
            Ready = false; Error = null; BuildProgress = 0; WalkableNodes.Clear(); WalkableCount = 0;
            if (levelRoot == null) { Error = "No generated level root."; yield break; }
            Collider[] geometry = levelRoot.GetComponentsInChildren<Collider>();
            Bounds bounds = new Bounds(); bool haveBounds = false;
            foreach (Collider c in geometry)
            {
                if (!c.enabled || c.isTrigger || (Settings.worldMask.value & (1 << c.gameObject.layer)) == 0) continue;
                if (!haveBounds) { bounds = c.bounds; haveBounds = true; } else bounds.Encapsulate(c.bounds);
            }
            if (!haveBounds) { Error = "No solid level colliders match World Mask."; yield break; }
            float cell = Settings.cellSize;
            origin = new Vector3(Mathf.Floor(bounds.min.x / cell) * cell, Settings.floorY,
                                 Mathf.Floor(bounds.min.z / cell) * cell);
            int width = Mathf.CeilToInt((bounds.max.x - origin.x) / cell) + 1;
            int depth = Mathf.CeilToInt((bounds.max.z - origin.z) / cell) + 1;
            if ((long)width * depth > Settings.maxGridCells)
            { Error = $"Grid would contain {(long)width * depth} cells. Raise Cell Size or the deliberate Max Grid Cells limit."; yield break; }
            Graph = new GridGraph(width, depth, cell);
            for (int n = 0; n < Graph.Count; n++)
            {
                bool valid = HasSupport(World(n)) && CapsuleClear(World(n));
                Graph.SetWalkable(n, valid);
                if (valid) WalkableNodes.Add(n);
                if ((n + 1) % Settings.buildCellsPerFrame == 0)
                { BuildProgress = 0.5f * (n + 1f) / Graph.Count; yield return null; }
            }
            WalkableCount = WalkableNodes.Count;
            for (int n = 0; n < Graph.Count; n++)
            {
                if (Graph.IsWalkable(n))
                {
                    for (int d = 0; d < 8; d++)
                    {
                        int next = Graph.Neighbour(n, d);
                        if (next <= n || !Graph.CanConnect(n, d)) continue;
                        if (CanTraverse(World(n), World(next))) Graph.Connect(n, d);
                    }
                }
                if ((n + 1) % Settings.buildCellsPerFrame == 0)
                { BuildProgress = 0.5f + 0.45f * (n + 1f) / Graph.Count; yield return null; }
            }
            // Components exclude sealed prefab rooms even when the logical room graph says they connect.
            Components = new int[Graph.Count];
            for (int n = 0; n < Graph.Count; n++) Components[n] = -1;
            var queue = new Queue<int>(); int component = 0, processed = 0;
            foreach (int seed in WalkableNodes)
            {
                if (Components[seed] >= 0) continue;
                Components[seed] = component; queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int n = queue.Dequeue();
                    for (int d = 0; d < 8; d++)
                    {
                        if (!Graph.HasEdge(n, d)) continue;
                        int next = Graph.Neighbour(n, d);
                        if (Components[next] >= 0) continue;
                        Components[next] = component; queue.Enqueue(next);
                    }
                    if (++processed % Settings.buildCellsPerFrame == 0) yield return null;
                }
                component++;
            }
            if (WalkableCount == 0) { Error = "No supported clear cells. Check Floor Y, body size, colliders and mask."; yield break; }
            BuildProgress = 1f; Ready = true;
        }

        public Vector3 World(int node) => origin + new Vector3(node % Graph.Width * Settings.cellSize, 0f,
                                                               node / Graph.Width * Settings.cellSize);
        public Vector3 OnFloor(Vector3 point) => new Vector3(point.x, Settings.floorY, point.z);

        /// <summary>Local snap only; a checked connector prevents snapping through walls or over voids.</summary>
        public bool TryNode(Vector3 point, float radius, out int node, int component = -1)
        {
            node = -1;
            if (!Ready) return false;
            point = OnFloor(point);
            int x = Mathf.RoundToInt((point.x - origin.x) / Settings.cellSize);
            int z = Mathf.RoundToInt((point.z - origin.z) / Settings.cellSize);
            int r = Mathf.CeilToInt(radius / Settings.cellSize);
            float best = radius * radius + 0.0001f;
            for (int iz = Mathf.Max(0, z - r); iz <= Mathf.Min(Graph.Depth - 1, z + r); iz++)
            for (int ix = Mathf.Max(0, x - r); ix <= Mathf.Min(Graph.Width - 1, x + r); ix++)
            {
                int n = Graph.Index(ix, iz);
                if (!Graph.IsWalkable(n) || (component >= 0 && Components[n] != component)) continue;
                float sq = (World(n) - point).sqrMagnitude;
                if (sq >= best || !CanTraverse(point, World(n))) continue;
                node = n; best = sq;
            }
            return node >= 0;
        }

        public bool LiveEdgeClear(int a, int b) => CanTraverse(World(a), World(b), false);

        bool Ignored(Collider collider)
        {
            if (collider == null || !collider.enabled || collider.isTrigger) return true;
            Transform t = collider.transform;
            return (playerRoot != null && (t == playerRoot || t.IsChildOf(playerRoot)))
                || (actorsRoot != null && (t == actorsRoot || t.IsChildOf(actorsRoot)));
        }

        bool Overflow(int count, int capacity)
        {
            if (count < capacity) return false;
            if (!warnedOverflow)
            {
                warnedOverflow = true;
                UnityEngine.Debug.LogWarning("[A*] Physics query buffer filled. Failing closed; simplify overlapping colliders or increase the 128-entry buffers.");
            }
            return true;
        }

        bool FloorAt(Vector3 point)
        {
            float probe = Settings.floorTolerance + 0.15f;
            int count = Physics.RaycastNonAlloc(point + Vector3.up * probe, Vector3.down, hits, probe * 2f,
                                               Settings.worldMask, QueryTriggerInteraction.Ignore);
            if (Overflow(count, hits.Length)) return false;
            for (int i = 0; i < count; i++)
            {
                if (Ignored(hits[i].collider)) continue;
                if (Mathf.Abs(hits[i].point.y - Settings.floorY) <= Settings.floorTolerance && hits[i].normal.y >= 0.98f)
                    return true;
            }
            return false;
        }

        public bool HasSupport(Vector3 point)
        {
            point = OnFloor(point);
            float r = Settings.bodyRadius;
            return FloorAt(point) && FloorAt(point + Vector3.right * r) && FloorAt(point - Vector3.right * r)
                 && FloorAt(point + Vector3.forward * r) && FloorAt(point - Vector3.forward * r);
        }

        void Capsule(Vector3 feet, out Vector3 bottom, out Vector3 top, out float radius)
        {
            radius = Settings.bodyRadius + Settings.wallMargin;
            feet = OnFloor(feet);
            bottom = feet + Vector3.up * (radius + Settings.groundClearance);
            top = feet + Vector3.up * (Settings.bodyHeight - radius);
        }

        public bool CapsuleClear(Vector3 feet)
        {
            Capsule(feet, out Vector3 bottom, out Vector3 top, out float radius);
            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, overlaps, Settings.worldMask,
                                                      QueryTriggerInteraction.Ignore);
            if (Overflow(count, overlaps.Length)) return false;
            for (int i = 0; i < count; i++) if (!Ignored(overlaps[i])) return false;
            return true;
        }

        public bool CanTraverse(Vector3 from, Vector3 to, bool checkFloor = true)
        {
            from = OnFloor(from); to = OnFloor(to);
            if (!CapsuleClear(from) || !CapsuleClear(to)) return false;
            Vector3 delta = to - from; float distance = delta.magnitude;
            if (distance > 0.001f)
            {
                Capsule(from, out Vector3 bottom, out Vector3 top, out float radius);
                int count = Physics.CapsuleCastNonAlloc(bottom, top, radius, delta / distance, hits, distance,
                                                        Settings.worldMask, QueryTriggerInteraction.Ignore);
                if (Overflow(count, hits.Length)) return false;
                for (int i = 0; i < count; i++) if (!Ignored(hits[i].collider)) return false;
            }
            if (!checkFloor) return true;
            int steps = Mathf.Max(1, Mathf.CeilToInt(distance / Mathf.Min(Settings.cellSize * 0.5f, 0.25f)));
            for (int i = 0; i <= steps; i++) if (!HasSupport(Vector3.Lerp(from, to, (float)i / steps))) return false;
            return true;
        }

        public bool ClearSight(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from; float distance = delta.magnitude;
            if (distance < 0.001f) return true;
            int count = Physics.RaycastNonAlloc(from, delta / distance, hits, distance, Settings.worldMask,
                                               QueryTriggerInteraction.Ignore);
            if (Overflow(count, hits.Length)) return false;
            for (int i = 0; i < count; i++) if (!Ignored(hits[i].collider)) return false;
            return true;
        }

        public void DrawGizmos()
        {
            if (!Ready || !Settings.drawGrid) return;
            int stride = Mathf.Max(1, Mathf.CeilToInt((float)Graph.Count / Settings.maxGizmoNodes));
            for (int n = 0; n < Graph.Count; n += stride)
            {
                bool open = Graph.IsWalkable(n);
                if (!open && !Settings.drawBlockedNodes) continue;
                Gizmos.color = open ? new Color(0f, 1f, 0.4f, 0.5f) : new Color(1f, 0f, 0f, 0.45f);
                Gizmos.DrawWireCube(World(n) + Vector3.up * 0.07f, new Vector3(Settings.cellSize * 0.8f, 0.05f, Settings.cellSize * 0.8f));
            }
        }
    }
}
