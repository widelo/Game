// Engine-free helpers over LevelLayout. Kept out of LevelLayout.cs so that file's public surface stays frozen.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LevelGen.Core
{
    public static class CardinalExtensions
    {
        public static Cardinal Opposite(this Cardinal dir)
        {
            switch (dir)
            {
                case Cardinal.North: return Cardinal.South;
                case Cardinal.South: return Cardinal.North;
                case Cardinal.East: return Cardinal.West;
                default: return Cardinal.East;
            }
        }
    }

    public static class LevelLayoutExtensions
    {
        /// <summary>
        /// BFS graph distance (in corridors) from <paramref name="fromRoom"/> to every room.
        /// Unreachable rooms get -1. Deterministic: neighbours are visited in corridor order.
        /// </summary>
        public static int[] BfsDistances(this LevelLayout layout, int fromRoom)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            int n = layout.Rooms.Count;
            var dist = new int[n];
            for (int i = 0; i < n; i++) dist[i] = -1;
            if (fromRoom < 0 || fromRoom >= n) return dist;

            // Adjacency built in corridor order so traversal order is stable.
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();
            foreach (var c in layout.Corridors)
            {
                if (c.RoomA < 0 || c.RoomA >= n || c.RoomB < 0 || c.RoomB >= n) continue;
                adj[c.RoomA].Add(c.RoomB);
                adj[c.RoomB].Add(c.RoomA);
            }

            dist[fromRoom] = 0;
            var queue = new Queue<int>();
            queue.Enqueue(fromRoom);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                foreach (int nb in adj[cur])
                {
                    if (dist[nb] != -1) continue;
                    dist[nb] = dist[cur] + 1;
                    queue.Enqueue(nb);
                }
            }
            return dist;
        }

        /// <summary>Every prop in the level: room furniture first (room order), then corridor props.</summary>
        public static IEnumerable<Prop> AllProps(this LevelLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            foreach (var r in layout.Rooms)
            {
                if (r == null || r.Props == null) continue;
                foreach (var p in r.Props) yield return p;
            }
            foreach (var c in layout.Corridors)
            {
                if (c == null || c.Props == null) continue;
                foreach (var p in c.Props) yield return p;
            }
        }

        /// <summary>Every light in the level: room lights first (room order), then corridor strips.</summary>
        public static IEnumerable<LightSource> AllLights(this LevelLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            foreach (var r in layout.Rooms)
            {
                if (r == null || r.Lights == null) continue;
                foreach (var l in r.Lights) yield return l;
            }
            foreach (var c in layout.Corridors)
            {
                if (c == null || c.Lights == null) continue;
                foreach (var l in c.Lights) yield return l;
            }
        }

        /// <summary>Stable, culture-invariant textual dump of every field. Used by determinism tests.</summary>
        public static string ToDebugString(this LevelLayout layout)
        {
            if (layout == null) return "<null>";
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("seed=").Append(layout.Seed.ToString(ci)).Append('\n');
            sb.Append("rooms=").Append(layout.Rooms.Count.ToString(ci))
              .Append(" corridors=").Append(layout.Corridors.Count.ToString(ci)).Append('\n');
            foreach (var r in layout.Rooms)
            {
                sb.Append("R ").Append(r.Id.ToString(ci))
                  .Append(' ').Append(r.Role.ToString())
                  .Append(' ').Append(r.Shape.ToString())
                  .Append(' ').Append(r.Type.ToString())
                  .Append(" tpl=").Append(r.TemplateName ?? "<null>")
                  .Append(" cell=").Append(r.CellX.ToString(ci)).Append(',').Append(r.CellY.ToString(ci))
                  .Append(" c=").Append(r.Center.X.ToString("R", ci)).Append(',').Append(r.Center.Z.ToString("R", ci))
                  .Append(" s=").Append(r.SizeX.ToString("R", ci)).Append(',').Append(r.SizeZ.ToString("R", ci))
                  .Append(" cor=[");
                for (int i = 0; i < r.CorridorIds.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(r.CorridorIds[i].ToString(ci));
                }
                sb.Append("] props=").Append(r.Props.Count.ToString(ci))
                  .Append(" lights=").Append(r.Lights.Count.ToString(ci)).Append('\n');
                AppendProps(sb, ci, r.Props);
                AppendLights(sb, ci, r.Lights);
            }
            foreach (var c in layout.Corridors)
            {
                sb.Append("C ").Append(c.Id.ToString(ci))
                  .Append(' ').Append(c.RoomA.ToString(ci)).Append("->").Append(c.RoomB.ToString(ci))
                  .Append(' ').Append(c.DirectionFromA.ToString())
                  .Append(" w=").Append(c.Width.ToString("R", ci))
                  .Append(" path=[");
                for (int i = 0; i < c.Path.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(c.Path[i].X.ToString("R", ci)).Append(',').Append(c.Path[i].Z.ToString("R", ci));
                }
                sb.Append("] props=").Append(c.Props.Count.ToString(ci))
                  .Append(" lights=").Append(c.Lights.Count.ToString(ci)).Append('\n');
                AppendProps(sb, ci, c.Props);
                AppendLights(sb, ci, c.Lights);
            }
            return sb.ToString();
        }

        private static void AppendProps(StringBuilder sb, CultureInfo ci, List<Prop> props)
        {
            if (props == null) return;
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i];
                sb.Append("  P ").Append(i.ToString(ci))
                  .Append(' ').Append(p.Type.ToString())
                  .Append(" at=").Append(p.Position.X.ToString("R", ci)).Append(',').Append(p.Position.Z.ToString("R", ci))
                  .Append(" rot=").Append(p.RotationDeg.ToString("R", ci))
                  .Append(" s=").Append(p.SizeX.ToString("R", ci)).Append(',').Append(p.SizeZ.ToString("R", ci))
                  .Append(" h=").Append(p.Height.ToString("R", ci)).Append('\n');
            }
        }

        private static void AppendLights(StringBuilder sb, CultureInfo ci, List<LightSource> lights)
        {
            if (lights == null) return;
            for (int i = 0; i < lights.Count; i++)
            {
                var l = lights[i];
                sb.Append("  L ").Append(i.ToString(ci))
                  .Append(' ').Append(l.Type.ToString())
                  .Append(' ').Append(l.Tint.ToString())
                  .Append(" at=").Append(l.Position.X.ToString("R", ci)).Append(',').Append(l.Position.Z.ToString("R", ci))
                  .Append(" y=").Append(l.Height.ToString("R", ci))
                  .Append(" i=").Append(l.Intensity.ToString("R", ci))
                  .Append(" r=").Append(l.Range.ToString("R", ci))
                  .Append(l.Flicker ? " flicker" : "")
                  .Append(l.Enabled ? " on" : " off")
                  .Append(" prop=").Append(l.AttachedPropIndex.ToString(ci)).Append('\n');
            }
        }
    }
}
