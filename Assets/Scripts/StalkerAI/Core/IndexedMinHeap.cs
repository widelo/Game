using System;

namespace CS462.StalkerAI.Core
{
    /// <summary>One entry per open node; decrease-key and pop-min both take O(log V).</summary>
    internal sealed class IndexedMinHeap
    {
        readonly int[] heap, position;
        readonly double[] f, h;
        public int Count { get; private set; }

        public IndexedMinHeap(int capacity, double[] f, double[] h)
        {
            heap = new int[capacity]; position = new int[capacity];
            this.f = f; this.h = h;
            Clear();
        }

        public void Clear()
        {
            Count = 0;
            for (int i = 0; i < position.Length; i++) position[i] = -1;
        }

        bool Less(int a, int b)
        {
            if (f[a] != f[b]) return f[a] < f[b];
            if (h[a] != h[b]) return h[a] < h[b];
            return a < b; // deterministic tie break, without inflating the heuristic
        }

        void Swap(int a, int b)
        {
            int temp = heap[a]; heap[a] = heap[b]; heap[b] = temp;
            position[heap[a]] = a; position[heap[b]] = b;
        }

        public void PushOrDecrease(int node)
        {
            int i = position[node];
            if (i < 0) { i = Count++; heap[i] = node; position[node] = i; }
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (!Less(heap[i], heap[parent])) break;
                Swap(i, parent); i = parent;
            }
        }

        public int Pop()
        {
            if (Count == 0) throw new InvalidOperationException("Empty open set.");
            int answer = heap[0]; position[answer] = -1;
            Count--;
            if (Count == 0) return answer;
            heap[0] = heap[Count]; position[heap[0]] = 0;
            int i = 0;
            while (true)
            {
                int left = i * 2 + 1;
                if (left >= Count) break;
                int right = left + 1;
                int child = right < Count && Less(heap[right], heap[left]) ? right : left;
                if (!Less(heap[child], heap[i])) break;
                Swap(i, child); i = child;
            }
            return answer;
        }
    }
}
