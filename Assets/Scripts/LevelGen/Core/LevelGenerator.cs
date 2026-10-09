// Deterministic, engine-free level layout generator. See docs/ARCHITECTURE.md "Algorithm (v1)".
// Determinism contract: Generate(seed, settings) performs its System.Random draws in a fixed order,
// so the same (seed, settings) always yields a byte-identical LevelLayout.
//
// v3 adds (a) a spawn door budget (LevelGenSettings.SpawnMaxDoors) and (b) room templates
// (LevelGenSettings.Templates), which let a teammate hand us a hand-built room prefab and have the
// generator own its footprint, its lattice position, its doors and the floor under it.
using System;
using System.Collections.Generic;

namespace LevelGen.Core
{
    public static class LevelGenerator
    {
        // Fixed direction order. Every deterministic scan over neighbours uses this order.
        private static readonly Cardinal[] Dirs =
        {
            Cardinal.North, Cardinal.East, Cardinal.South, Cardinal.West
        };

        /// <summary>The furnishing stage used by the two-arg Generate. Swap it per call with the three-arg overload.</summary>
        private static readonly ILevelFurnisher DefaultFurnisher = new RoomFurnisher();

        /// <summary>Minimum corridor length kept on BOTH sides of a room: a template's fixed size must leave this much.</summary>
        public const double MinCorridorLength = 1.0;

        /// <summary>Wall kept on each side of a corridor mouth. A room axis must be >= CorridorWidth + 2*DoorJamb.</summary>
        public const double DoorJamb = 1.0;

        /// <summary>Absolute floor on MinRoomSize, independent of corridor width.</summary>
        public const double MinRoomSizeFloor = 3.0;

        private static void StepOf(Cardinal dir, out int dx, out int dy)
        {
            switch (dir)
            {
                case Cardinal.North: dx = 0; dy = 1; break;
                case Cardinal.East: dx = 1; dy = 0; break;
                case Cardinal.South: dx = 0; dy = -1; break;
                default: dx = -1; dy = 0; break;
            }
        }

        /// <summary>Direction from cell A to the orthogonally adjacent cell B, or null if not adjacent.</summary>
        internal static Cardinal? DirectionBetween(int ax, int ay, int bx, int by)
        {
            if (ax == bx && by == ay + 1) return Cardinal.North;
            if (ax == bx && by == ay - 1) return Cardinal.South;
            if (ay == by && bx == ax + 1) return Cardinal.East;
            if (ay == by && bx == ax - 1) return Cardinal.West;
            return null;
        }

        public static void ValidateSettings(LevelGenSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (settings.MaxRoomSize >= settings.CellSize)
                throw new ArgumentException(
                    $"MaxRoomSize ({settings.MaxRoomSize}) must be < CellSize ({settings.CellSize}) so every corridor has positive length.",
                    nameof(settings));
            if (settings.MinRoomSize <= 0)
                throw new ArgumentException($"MinRoomSize ({settings.MinRoomSize}) must be > 0.", nameof(settings));
            if (settings.MinRoomSize > settings.MaxRoomSize)
                throw new ArgumentException(
                    $"MinRoomSize ({settings.MinRoomSize}) must be <= MaxRoomSize ({settings.MaxRoomSize}).",
                    nameof(settings));
            if (settings.MinRooms < 1)
                throw new ArgumentException($"MinRooms ({settings.MinRooms}) must be >= 1.", nameof(settings));
            if (settings.MaxRooms < settings.MinRooms)
                throw new ArgumentException(
                    $"MaxRooms ({settings.MaxRooms}) must be >= MinRooms ({settings.MinRooms}).", nameof(settings));
            if (settings.GridWidth < 1 || settings.GridHeight < 1)
                throw new ArgumentException(
                    $"GridWidth/GridHeight ({settings.GridWidth}x{settings.GridHeight}) must both be >= 1.",
                    nameof(settings));
            if (settings.MaxRooms > settings.GridWidth * settings.GridHeight)
                throw new ArgumentException(
                    $"MaxRooms ({settings.MaxRooms}) must fit in the lattice ({settings.GridWidth}x{settings.GridHeight} = {settings.GridWidth * settings.GridHeight} cells).",
                    nameof(settings));
            if (settings.CorridorWidth <= 0)
                throw new ArgumentException($"CorridorWidth ({settings.CorridorWidth}) must be > 0.", nameof(settings));

            // --- v2: hospital scale knobs -------------------------------------------------------
            if (settings.CorridorWidth < 2.0)
                throw new ArgumentException(
                    $"CorridorWidth ({settings.CorridorWidth}) must be >= 2.0 m: a hospital corridor must take a gurney plus a walkway.",
                    nameof(settings));
            if (settings.MouthClearance > settings.MinRoomSize * 0.5)
                throw new ArgumentException(
                    $"MouthClearance ({settings.MouthClearance}) must be <= MinRoomSize/2 ({settings.MinRoomSize * 0.5}), else the smallest rooms cannot hold any furniture.",
                    nameof(settings));
            if (settings.CeilingHeight < 2.4)
                throw new ArgumentException(
                    $"CeilingHeight ({settings.CeilingHeight}) must be >= 2.4 m (ceiling lamps hang at CeilingHeight - 0.4).",
                    nameof(settings));

            // A doorway needs a wall to sit in: one metre of wall either side of the corridor mouth.
            if (settings.MinRoomSize < MinRoomSizeFloor)
                throw new ArgumentException(
                    $"MinRoomSize ({settings.MinRoomSize}) must be >= {MinRoomSizeFloor} m; smaller rooms are not navigable.",
                    nameof(settings));
            if (settings.MinRoomSize < settings.CorridorWidth + 2.0 * DoorJamb)
                throw new ArgumentException(
                    $"MinRoomSize ({settings.MinRoomSize}) must be >= CorridorWidth + 2*{DoorJamb} ({settings.CorridorWidth + 2.0 * DoorJamb}): a corridor may not be wider than the wall it pierces.",
                    nameof(settings));

            if (settings.PropSpacing < 0)
                throw new ArgumentException(
                    $"PropSpacing ({settings.PropSpacing}) must be >= 0: a negative gap would let furniture overlap.",
                    nameof(settings));

            if (settings.LightAreaPerLamp <= 0)
                throw new ArgumentException($"LightAreaPerLamp ({settings.LightAreaPerLamp}) must be > 0 (square metres of floor per ceiling lamp).", nameof(settings));
            if (settings.CorridorLightSpacing < RoomFurnisher.MinCorridorLightSpacing)
                throw new ArgumentException(
                    $"CorridorLightSpacing ({settings.CorridorLightSpacing}) must be >= {RoomFurnisher.MinCorridorLightSpacing} m, else a corridor is packed with an unbounded number of strip lights.",
                    nameof(settings));

            // CeilingOffsets only knows ceiling-lamp layouts for 1..4 lamps, so a larger cap is a lie.
            if (settings.MaxLightsPerRoom < 0 || settings.MaxLightsPerRoom > RoomFurnisher.MaxCeilingLampsPerRoom)
                throw new ArgumentException(
                    $"MaxLightsPerRoom ({settings.MaxLightsPerRoom}) must be in [0, {RoomFurnisher.MaxCeilingLampsPerRoom}]: RoomFurnisher only has ceiling-lamp layouts up to {RoomFurnisher.MaxCeilingLampsPerRoom} lamps.",
                    nameof(settings));

            // --- v3: spawn door budget + templates ----------------------------------------------
            if (settings.SpawnMaxDoors < 1 || settings.SpawnMaxDoors > 4)
                throw new ArgumentException(
                    $"SpawnMaxDoors ({settings.SpawnMaxDoors}) must be in [1, 4]: a lattice cell has at most 4 orthogonal neighbours.",
                    nameof(settings));

            if (settings.Templates != null)
            {
                // A fixed footprint must still leave MinCorridorLength of corridor on BOTH sides of the cell.
                double maxFixed = settings.CellSize - 2.0 * MinCorridorLength;
                for (int i = 0; i < settings.Templates.Count; i++)
                {
                    var t = settings.Templates[i];
                    if (t == null)
                        throw new ArgumentException($"Templates[{i}] is null.", nameof(settings));
                    if (string.IsNullOrEmpty(t.Name))
                        throw new ArgumentException($"Templates[{i}].Name must be non-empty.", nameof(settings));
                    if (t.FixedSizeX < 0 || t.FixedSizeZ < 0)
                        throw new ArgumentException(
                            $"Template '{t.Name}' fixed size ({t.FixedSizeX} x {t.FixedSizeZ}) must not be negative (0 means 'roll it procedurally').",
                            nameof(settings));
                    double minFixed = settings.CorridorWidth + 2.0 * DoorJamb;
                    if ((t.FixedSizeX > 0 && t.FixedSizeX < minFixed) || (t.FixedSizeZ > 0 && t.FixedSizeZ < minFixed))
                        throw new ArgumentException(
                            $"Template '{t.Name}' fixed size ({t.FixedSizeX} x {t.FixedSizeZ}) must be >= CorridorWidth + 2*{DoorJamb} ({minFixed}) on both axes: a room narrower than that cannot take a doorway.",
                            nameof(settings));
                    if (t.FixedSizeX >= maxFixed || t.FixedSizeZ >= maxFixed)
                        throw new ArgumentException(
                            $"Template '{t.Name}' fixed size ({t.FixedSizeX} x {t.FixedSizeZ}) must be < CellSize - 2*{MinCorridorLength} ({maxFixed}) so every corridor into it has positive length.",
                            nameof(settings));
                    if (t.MinDoors > t.MaxDoors)
                        throw new ArgumentException(
                            $"Template '{t.Name}' has MinDoors ({t.MinDoors}) > MaxDoors ({t.MaxDoors}).", nameof(settings));
                }
            }
        }

        /// <summary>Number of key rooms actually placed for a layout of <paramref name="roomCount"/> rooms.</summary>
        public static int ClampKeyRooms(int requested, int roomCount)
        {
            int max = roomCount - 1; // spawn is never a key room
            if (max < 0) max = 0;
            if (requested < 0) return 0;
            return requested > max ? max : requested;
        }

        public static LevelLayout Generate(int seed, LevelGenSettings settings = null)
        {
            return Generate(seed, settings, DefaultFurnisher);
        }

        /// <summary>
        /// Full entry point: <paramref name="furnisher"/> replaces the default RoomFurnisher stage.
        /// It is handed the same Random the layout was generated with, so a deterministic furnisher
        /// keeps the whole pipeline deterministic.
        /// </summary>
        public static LevelLayout Generate(int seed, LevelGenSettings settings, ILevelFurnisher furnisher)
        {
            if (settings == null) settings = new LevelGenSettings();
            if (furnisher == null) furnisher = DefaultFurnisher;
            ValidateSettings(settings);

            var rnd = new Random(seed);
            var layout = new LevelLayout { Seed = seed, Settings = settings };

            // --- 1. how many rooms -------------------------------------------------
            int roomCount = settings.MinRooms + rnd.Next(settings.MaxRooms - settings.MinRooms + 1);

            // --- 2. growth from a random start cell --------------------------------
            int startIndex = rnd.Next(settings.GridWidth * settings.GridHeight);
            int startCellX = startIndex % settings.GridWidth;
            int startCellY = startIndex / settings.GridWidth;

            var cellX = new List<int>(roomCount) { startCellX };
            var cellY = new List<int>(roomCount) { startCellY };
            var occupied = new Dictionary<long, int> { { CellKey(startCellX, startCellY), 0 } };

            // Growth-tree edges as (parentCellIndex, childCellIndex), in creation order.
            var treeEdges = new List<KeyValuePair<int, int>>(roomCount);

            var candFrom = new List<int>();
            var candX = new List<int>();
            var candY = new List<int>();

            while (cellX.Count < roomCount)
            {
                candFrom.Clear(); candX.Clear(); candY.Clear();
                for (int i = 0; i < cellX.Count; i++)
                {
                    for (int d = 0; d < Dirs.Length; d++)
                    {
                        int dx, dy;
                        StepOf(Dirs[d], out dx, out dy);
                        int nx = cellX[i] + dx, ny = cellY[i] + dy;
                        if (nx < 0 || ny < 0 || nx >= settings.GridWidth || ny >= settings.GridHeight) continue;
                        if (occupied.ContainsKey(CellKey(nx, ny))) continue;
                        candFrom.Add(i); candX.Add(nx); candY.Add(ny);
                    }
                }
                if (candFrom.Count == 0) break; // lattice exhausted (cannot happen while MaxRooms <= cells)
                int pick = rnd.Next(candFrom.Count);
                int newIndex = cellX.Count;
                cellX.Add(candX[pick]);
                cellY.Add(candY[pick]);
                occupied[CellKey(candX[pick], candY[pick])] = newIndex;
                treeEdges.Add(new KeyValuePair<int, int>(candFrom[pick], newIndex));
            }

            int n = cellX.Count;

            // --- 2b. v3: the spawn cell is NOT automatically the growth start ------------------
            // The spawn/exit room has a door budget (SpawnMaxDoors). The tree edges are mandatory, so
            // the spawn must sit on a cell whose TREE degree already fits that budget: with the default
            // SpawnMaxDoors == 1 that means a leaf of the growth tree, picked uniformly among leaves.
            // (A tree always has at least two leaves, so the candidate set is never empty for cap >= 1.)
            int spawnCap = settings.SpawnMaxDoors;
            var treeDegree = new int[n];
            for (int i = 0; i < treeEdges.Count; i++)
            {
                treeDegree[treeEdges[i].Key]++;
                treeDegree[treeEdges[i].Value]++;
            }
            var spawnCandidates = new List<int>();
            for (int i = 0; i < n; i++) if (treeDegree[i] <= spawnCap) spawnCandidates.Add(i);
            int spawnCell = spawnCandidates.Count > 0 ? spawnCandidates[rnd.Next(spawnCandidates.Count)] : 0;

            // --- 2c. re-index so Rooms[0] is still the spawn, rest in growth order -------------
            var newToOld = new int[n];
            var oldToNew = new int[n];
            newToOld[0] = spawnCell;
            oldToNew[spawnCell] = 0;
            int nextId = 1;
            for (int i = 0; i < n; i++)
            {
                if (i == spawnCell) continue;
                newToOld[nextId] = i;
                oldToNew[i] = nextId;
                nextId++;
            }
            var cx = new int[n];
            var cy = new int[n];
            for (int i = 0; i < n; i++) { cx[i] = cellX[newToOld[i]]; cy[i] = cellY[newToOld[i]]; }
            int spawnCellX = cx[0], spawnCellY = cy[0];

            // --- 3. edges: tree edges always, extra adjacent pairs with LoopEdgeChance ---------
            // Door budget: 4 for an ordinary room (a lattice cell has 4 sides), SpawnMaxDoors for the spawn.
            // A loop edge that would push either end over its budget is skipped.
            var doorLimit = new int[n];
            for (int i = 0; i < n; i++) doorLimit[i] = 4;
            doorLimit[0] = spawnCap;

            var degree = new int[n];
            var treeEdgeSet = new HashSet<long>();
            var edges = new List<KeyValuePair<int, int>>(treeEdges.Count);
            for (int i = 0; i < treeEdges.Count; i++)
            {
                int a = oldToNew[treeEdges[i].Key], b = oldToNew[treeEdges[i].Value];
                int lo = a < b ? a : b, hi = a < b ? b : a;
                treeEdgeSet.Add(EdgeKey(lo, hi));
                edges.Add(new KeyValuePair<int, int>(lo, hi));
                degree[a]++; degree[b]++;
            }

            for (int a = 0; a < n; a++)
            {
                for (int b = a + 1; b < n; b++)
                {
                    if (DirectionBetween(cx[a], cy[a], cx[b], cy[b]) == null) continue;
                    if (treeEdgeSet.Contains(EdgeKey(a, b))) continue;
                    if (rnd.NextDouble() >= settings.LoopEdgeChance) continue;
                    if (degree[a] >= doorLimit[a] || degree[b] >= doorLimit[b]) continue;
                    edges.Add(new KeyValuePair<int, int>(a, b));
                    degree[a]++; degree[b]++;
                }
            }

            // --- 4. rooms: sizes + shapes -----------------------------------------
            double sizeSpan = settings.MaxRoomSize - settings.MinRoomSize;
            for (int i = 0; i < n; i++)
            {
                double sx = settings.MinRoomSize + rnd.NextDouble() * sizeSpan;
                double sz = settings.MinRoomSize + rnd.NextDouble() * sizeSpan;
                bool ellipse = rnd.NextDouble() < settings.EllipseChance;
                layout.Rooms.Add(new Room
                {
                    Id = i,
                    Role = i == 0 ? RoomRole.Spawn : RoomRole.Normal,
                    Shape = ellipse ? RoomShape.Ellipse : RoomShape.Rectangle,
                    CellX = cx[i],
                    CellY = cy[i],
                    Center = new Vec2((cx[i] - spawnCellX) * settings.CellSize,
                                      (cy[i] - spawnCellY) * settings.CellSize),
                    SizeX = sx,
                    SizeZ = sz
                });
            }

            // --- 5. corridors ------------------------------------------------------
            for (int i = 0; i < edges.Count; i++)
            {
                int a = edges[i].Key, b = edges[i].Value;
                var roomA = layout.Rooms[a];
                var roomB = layout.Rooms[b];
                Cardinal? dirOpt = DirectionBetween(roomA.CellX, roomA.CellY, roomB.CellX, roomB.CellY);
                if (dirOpt == null)
                    throw new InvalidOperationException($"Edge {a}->{b} is not between adjacent cells.");
                Cardinal dir = dirOpt.Value;

                var corridor = new Corridor
                {
                    Id = layout.Corridors.Count,
                    RoomA = a,
                    RoomB = b,
                    DirectionFromA = dir,
                    Width = settings.CorridorWidth
                };
                corridor.Path.Add(roomA.BoundaryPoint(dir));
                corridor.Path.Add(roomB.BoundaryPoint(dir.Opposite()));
                layout.Corridors.Add(corridor);
                roomA.CorridorIds.Add(corridor.Id);
                roomB.CorridorIds.Add(corridor.Id);
            }

            // --- 6. roles: key rooms ----------------------------------------------
            int keyCount = ClampKeyRooms(settings.KeyRooms, n);
            if (keyCount > 0)
            {
                int[] dist = layout.BfsDistances(0);
                var far = new List<int>();
                var near = new List<int>();
                for (int i = 1; i < n; i++)
                {
                    if (dist[i] >= 2) far.Add(i);
                    if (dist[i] >= 1) near.Add(i);
                }
                var pool = far.Count >= keyCount ? far : near;
                // Deterministic partial Fisher-Yates over the ordered pool.
                var shuffled = new List<int>(pool);
                for (int i = 0; i < keyCount && i < shuffled.Count; i++)
                {
                    int j = i + rnd.Next(shuffled.Count - i);
                    int tmp = shuffled[i]; shuffled[i] = shuffled[j]; shuffled[j] = tmp;
                    layout.Rooms[shuffled[i]].Role = RoomRole.Key;
                }
            }

            // --- 7. v3: templates --------------------------------------------------
            // Deliberate ordering: the graph (tree + loops + roles) is FINAL before any template is
            // chosen, so template constraints only ever FILTER - they never steer the lattice. The
            // consequence, and it is a real limitation: a prefab room whose AllowedDoors names a single
            // side is only placed where the lattice already agrees (we never rotate prefabs). Teammates
            // who want their prefab placed often should use DoorSides.All, or at least two sides.
            AssignTemplates(layout, settings, rnd);

            // Template footprints may differ from the rolled sizes, so corridor endpoints are rebuilt
            // from the final room boxes.
            RebuildCorridorPaths(layout);

            // --- 8. v2/v3: archetypes, furniture, lights (same rnd stream => still deterministic) ---
            furnisher.Furnish(layout, rnd);

            return layout;
        }

        // =============================================================================================
        // v3: template selection
        // =============================================================================================

        /// <summary>Flags for every side of <paramref name="room"/> that has a corridor attached.</summary>
        public static DoorSides AttachedDoorSides(LevelLayout layout, Room room)
        {
            DoorSides sides = DoorSides.None;
            if (layout == null || room == null || room.CorridorIds == null) return sides;
            for (int i = 0; i < room.CorridorIds.Count; i++)
            {
                int cid = room.CorridorIds[i];
                if (cid < 0 || cid >= layout.Corridors.Count) continue;
                var c = layout.Corridors[cid];
                if (c == null) continue;
                Cardinal side = c.RoomA == room.Id ? c.DirectionFromA : c.DirectionFromA.Opposite();
                sides |= side.ToFlag();
            }
            return sides;
        }

        /// <summary>True when <paramref name="affinity"/> allows a room in <paramref name="role"/>.</summary>
        public static bool AffinityAllows(RoleAffinity affinity, RoomRole role)
        {
            switch (affinity)
            {
                case RoleAffinity.Any: return true;
                case RoleAffinity.SpawnOnly: return role == RoomRole.Spawn;
                case RoleAffinity.KeyOnly: return role == RoomRole.Key;
                case RoleAffinity.NormalOnly: return role == RoomRole.Normal;
                default: return role != RoomRole.Spawn; // NeverSpawn
            }
        }

        /// <summary>
        /// True when <paramref name="t"/> may be placed on <paramref name="room"/> as the graph now stands:
        /// the role matches, the door count is inside the template's bounds, and every attached corridor
        /// leaves through a side the template allows.
        /// </summary>
        public static bool TemplateFits(RoomTemplateSpec t, Room room, DoorSides attached)
        {
            if (t == null || room == null) return false;
            if (!AffinityAllows(t.Affinity, room.Role)) return false;
            if (room.DoorCount < t.MinDoors || room.DoorCount > t.MaxDoors) return false;
            if ((attached & ~t.AllowedDoors) != DoorSides.None) return false;
            return true;
        }

        private static void AssignTemplates(LevelLayout layout, LevelGenSettings settings, Random rnd)
        {
            layout.TemplateUsage.Clear();
            var templates = settings.Templates;
            bool any = templates != null && templates.Count > 0;

            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                var room = layout.Rooms[i];
                RoomTemplateSpec chosen = null;

                if (any)
                {
                    DoorSides attached = AttachedDoorSides(layout, room);
                    // Spawn room: SpawnOnly templates get the first pass.
                    if (room.Role == RoomRole.Spawn)
                        chosen = Pick(templates, room, attached, rnd, true);
                    if (chosen == null)
                        chosen = Pick(templates, room, attached, rnd, false);
                }

                if (chosen == null)
                {
                    // Nothing eligible (or no templates configured): stay fully procedural, keeping the
                    // size and shape already rolled for this room.
                    room.Template = RoomTemplateSpec.Procedural();
                    room.TemplateName = room.Template.Name;
                }
                else
                {
                    room.Template = chosen;
                    room.TemplateName = chosen.Name;
                    bool fixedFootprint = chosen.FixedSizeX > 0 || chosen.FixedSizeZ > 0;
                    if (chosen.FixedSizeX > 0) room.SizeX = chosen.FixedSizeX;
                    if (chosen.FixedSizeZ > 0) room.SizeZ = chosen.FixedSizeZ;
                    if (fixedFootprint) room.Shape = RoomShape.Rectangle;
                    else if (chosen.OverrideShape) room.Shape = chosen.Shape;
                    if (chosen.ForcedType.HasValue) room.Type = chosen.ForcedType.Value;
                }

                int used;
                layout.TemplateUsage.TryGetValue(room.TemplateName, out used);
                layout.TemplateUsage[room.TemplateName] = used + 1;
            }
        }

        /// <summary>Weighted random pick among the eligible templates. Consumes exactly one draw, or none when nothing fits.</summary>
        private static RoomTemplateSpec Pick(List<RoomTemplateSpec> templates, Room room, DoorSides attached,
                                             Random rnd, bool spawnOnlyPass)
        {
            double total = 0;
            for (int i = 0; i < templates.Count; i++)
            {
                var t = templates[i];
                if (t == null || t.Weight <= 0) continue;
                if (spawnOnlyPass && t.Affinity != RoleAffinity.SpawnOnly) continue;
                if (!TemplateFits(t, room, attached)) continue;
                total += t.Weight;
            }
            if (total <= 0) return null;

            double pick = rnd.NextDouble() * total;
            double acc = 0;
            RoomTemplateSpec last = null;
            for (int i = 0; i < templates.Count; i++)
            {
                var t = templates[i];
                if (t == null || t.Weight <= 0) continue;
                if (spawnOnlyPass && t.Affinity != RoleAffinity.SpawnOnly) continue;
                if (!TemplateFits(t, room, attached)) continue;
                last = t;
                acc += t.Weight;
                if (pick < acc) return t;
            }
            return last; // floating-point tail
        }

        /// <summary>Re-derives every corridor's two endpoints from the current room footprints.</summary>
        public static void RebuildCorridorPaths(LevelLayout layout)
        {
            if (layout == null) return;
            for (int i = 0; i < layout.Corridors.Count; i++)
            {
                var c = layout.Corridors[i];
                if (c == null) continue;
                var ra = layout.Rooms[c.RoomA];
                var rb = layout.Rooms[c.RoomB];
                c.Path.Clear();
                c.Path.Add(ra.BoundaryPoint(c.DirectionFromA));
                c.Path.Add(rb.BoundaryPoint(c.DirectionFromA.Opposite()));
            }
        }

        private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;
        private static long EdgeKey(int a, int b)
        {
            int lo = a < b ? a : b, hi = a < b ? b : a;
            return ((long)lo << 32) ^ (uint)hi;
        }
    }
}
