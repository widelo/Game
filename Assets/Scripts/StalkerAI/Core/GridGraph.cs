using System;

namespace CS462.StalkerAI.Core
{
    /// <summary>Eight-neighbour, flat XZ graph. Physics decides which nodes/edges exist.</summary>
    public sealed class GridGraph
    {
        public static readonly int[] DX = { 1, 0, -1, 0, 1, -1, -1, 1 };
        public static readonly int[] DZ = { 0, 1, 0, -1, 1, 1, -1, -1 };
        static readonly int[] Opposite = { 2, 3, 0, 1, 6, 7, 4, 5 };
        public const double Diagonal = 1.4142135623730951;

        readonly bool[] walkable;
        readonly byte[] edges;
        public int Width { get; }
        public int Depth { get; }
        public int Count => walkable.Length;
        public double CellSize { get; }

        public GridGraph(int width, int depth, double cellSize)
        {
            if (width < 1 || depth < 1 || cellSize <= 0 || double.IsNaN(cellSize)
                || double.IsInfinity(cellSize)) throw new ArgumentOutOfRangeException();
            Width = width; Depth = depth; CellSize = cellSize;
            walkable = new bool[checked(width * depth)];
            edges = new byte[walkable.Length];
        }

        public int Index(int x, int z) => x < 0 || z < 0 || x >= Width || z >= Depth ? -1 : z * Width + x;
        public bool IsWalkable(int node) => node >= 0 && node < Count && walkable[node];
        // Build-time only: do not mutate a graph while a search uses it.
        public void SetWalkable(int node, bool value) { walkable[node] = value; }
        public int Neighbour(int node, int direction) => Index(node % Width + DX[direction], node / Width + DZ[direction]);
        public double Cost(int direction) => CellSize * (direction < 4 ? 1.0 : Diagonal);

        public bool CanConnect(int node, int direction)
        {
            int next = Neighbour(node, direction);
            if (!IsWalkable(node) || !IsWalkable(next)) return false;
            if (direction < 4) return true;
            int x = node % Width, z = node / Width;
            // No squeezing diagonally between two blocked cells.
            return IsWalkable(Index(x + DX[direction], z)) && IsWalkable(Index(x, z + DZ[direction]));
        }

        public void Connect(int node, int direction)
        {
            if (!CanConnect(node, direction)) return;
            int next = Neighbour(node, direction);
            edges[node] |= (byte)(1 << direction);
            edges[next] |= (byte)(1 << Opposite[direction]);
        }

        public bool HasEdge(int node, int direction) => (edges[node] & (1 << direction)) != 0;

        /// <summary>Octile distance: exact in an unobstructed grid, a lower bound when edges are removed.</summary>
        public double Heuristic(int a, int b)
        {
            int dx = Math.Abs(a % Width - b % Width), dz = Math.Abs(a / Width - b / Width);
            return CellSize * (Math.Max(dx, dz) + (Diagonal - 1.0) * Math.Min(dx, dz));
        }
    }
}
