// Structural invariants for a generated LevelLayout. Engine-free; used by edit-mode property tests
// and by the runtime builder as a cheap sanity gate.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace LevelGen.Core
{
    public static class LevelValidator
    {
        private const double Eps = 1e-9;

        public static List<string> Validate(LevelLayout layout)
        {
            var errors = new List<string>();
            var ci = CultureInfo.InvariantCulture;

            if (layout == null) { errors.Add("layout is null"); return errors; }
            if (layout.Rooms == null) { errors.Add("layout.Rooms is null"); return errors; }
            if (layout.Corridors == null) { errors.Add("layout.Corridors is null"); return errors; }

            var s = layout.Settings;
            if (s == null) { errors.Add("layout.Settings is null"); return errors; }

            int n = layout.Rooms.Count;

            // --- room count --------------------------------------------------------
            if (n < s.MinRooms || n > s.MaxRooms)
                errors.Add($"room count {n} outside [{s.MinRooms}, {s.MaxRooms}]");
            if (n == 0) return errors;

            // --- ids, roles, sizes, cells -----------------------------------------
            int spawnCount = 0, keyCount = 0;
            var cellSeen = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
            {
                var r = layout.Rooms[i];
                if (r == null) { errors.Add($"Rooms[{i}] is null"); continue; }
                if (r.Id != i) errors.Add($"Rooms[{i}].Id == {r.Id}, expected {i}");
                if (r.Role == RoomRole.Spawn) spawnCount++;
                if (r.Role == RoomRole.Key) keyCount++;

                // --- v3: templates ------------------------------------------------
                var t = r.Template;
                if (t == null)
                {
                    errors.Add($"Rooms[{i}].Template is null");
                }
                else
                {
                    if (r.TemplateName != t.Name)
                        errors.Add($"Rooms[{i}].TemplateName '{r.TemplateName}' != Template.Name '{t.Name}'");

                    // A fixed footprint overrides the settings size range; a procedural one must stay in it.
                    if (t.FixedSizeX > 0)
                    {
                        if (Math.Abs(r.SizeX - t.FixedSizeX) > 1e-9)
                            errors.Add($"Rooms[{i}].SizeX {r.SizeX.ToString("R", ci)} != template '{t.Name}' FixedSizeX {t.FixedSizeX}");
                    }
                    else if (r.SizeX < s.MinRoomSize - Eps || r.SizeX > s.MaxRoomSize + Eps)
                        errors.Add($"Rooms[{i}].SizeX {r.SizeX.ToString("R", ci)} outside [{s.MinRoomSize}, {s.MaxRoomSize}]");

                    if (t.FixedSizeZ > 0)
                    {
                        if (Math.Abs(r.SizeZ - t.FixedSizeZ) > 1e-9)
                            errors.Add($"Rooms[{i}].SizeZ {r.SizeZ.ToString("R", ci)} != template '{t.Name}' FixedSizeZ {t.FixedSizeZ}");
                    }
                    else if (r.SizeZ < s.MinRoomSize - Eps || r.SizeZ > s.MaxRoomSize + Eps)
                        errors.Add($"Rooms[{i}].SizeZ {r.SizeZ.ToString("R", ci)} outside [{s.MinRoomSize}, {s.MaxRoomSize}]");

                    if ((t.FixedSizeX > 0 || t.FixedSizeZ > 0) && r.Shape != RoomShape.Rectangle)
                        errors.Add($"Rooms[{i}] uses fixed-footprint template '{t.Name}' but Shape == {r.Shape} (expected Rectangle)");

                    if (t.ForcedType.HasValue && r.Type != t.ForcedType.Value)
                        errors.Add($"Rooms[{i}].Type == {r.Type} but template '{t.Name}' forces {t.ForcedType.Value}");

                    if (!LevelGenerator.AffinityAllows(t.Affinity, r.Role))
                        errors.Add($"Rooms[{i}] is {r.Role} but template '{t.Name}' has Affinity {t.Affinity}");

                    if (r.DoorCount > t.MaxDoors)
                        errors.Add($"Rooms[{i}] has {r.DoorCount} doors, template '{t.Name}' allows at most {t.MaxDoors}");
                    // MinDoors cannot be met by a one-room level (it has no corridors at all).
                    if (n > 1 && r.DoorCount < t.MinDoors)
                        errors.Add($"Rooms[{i}] has {r.DoorCount} doors, template '{t.Name}' needs at least {t.MinDoors}");

                    var attached = LevelGenerator.AttachedDoorSides(layout, r);
                    if ((attached & ~t.AllowedDoors) != DoorSides.None)
                        errors.Add($"Rooms[{i}] has corridors on {attached}, template '{t.Name}' only allows {t.AllowedDoors}");

                    if (!t.GenerateProps && r.Props != null && r.Props.Count > 0)
                        errors.Add($"Rooms[{i}] uses template '{t.Name}' (GenerateProps false) but has {r.Props.Count} props");
                    if (!t.GenerateLights && r.Lights != null && r.Lights.Count > 0)
                        errors.Add($"Rooms[{i}] uses template '{t.Name}' (GenerateLights false) but has {r.Lights.Count} lights");
                }

                // --- v3: spawn door budget ----------------------------------------
                if (r.Role == RoomRole.Spawn && r.DoorCount > s.SpawnMaxDoors)
                    errors.Add($"Rooms[{i}] is the spawn room with {r.DoorCount} doors, SpawnMaxDoors is {s.SpawnMaxDoors}");

                if (r.CellX < 0 || r.CellY < 0 || r.CellX >= s.GridWidth || r.CellY >= s.GridHeight)
                    errors.Add($"Rooms[{i}] cell ({r.CellX},{r.CellY}) outside lattice {s.GridWidth}x{s.GridHeight}");

                string key = r.CellX + ":" + r.CellY;
                int other;
                if (cellSeen.TryGetValue(key, out other))
                    errors.Add($"Rooms[{i}] shares cell ({r.CellX},{r.CellY}) with Rooms[{other}]");
                else
                    cellSeen[key] = i;
            }

            if (spawnCount != 1) errors.Add($"expected exactly 1 Spawn room, found {spawnCount}");

            int expectedKeys = LevelGenerator.ClampKeyRooms(s.KeyRooms, n);
            if (keyCount != expectedKeys)
                errors.Add($"expected exactly {expectedKeys} Key rooms (requested {s.KeyRooms}, clamped for {n} rooms), found {keyCount}");

            // --- bounding boxes must not overlap -----------------------------------
            for (int i = 0; i < n; i++)
            {
                var a = layout.Rooms[i];
                if (a == null) continue;
                for (int j = i + 1; j < n; j++)
                {
                    var b = layout.Rooms[j];
                    if (b == null) continue;
                    double ox = Math.Min(a.Center.X + a.HalfX, b.Center.X + b.HalfX)
                              - Math.Max(a.Center.X - a.HalfX, b.Center.X - b.HalfX);
                    double oz = Math.Min(a.Center.Z + a.HalfZ, b.Center.Z + b.HalfZ)
                              - Math.Max(a.Center.Z - a.HalfZ, b.Center.Z - b.HalfZ);
                    if (ox > Eps && oz > Eps)
                        errors.Add($"Rooms[{i}] and Rooms[{j}] bounding boxes overlap by ({ox.ToString("R", ci)}, {oz.ToString("R", ci)})");
                }
            }

            // --- corridors ---------------------------------------------------------
            int m = layout.Corridors.Count;
            for (int i = 0; i < m; i++)
            {
                var c = layout.Corridors[i];
                if (c == null) { errors.Add($"Corridors[{i}] is null"); continue; }
                if (c.Id != i) errors.Add($"Corridors[{i}].Id == {c.Id}, expected {i}");

                if (c.RoomA < 0 || c.RoomA >= n || c.RoomB < 0 || c.RoomB >= n)
                {
                    errors.Add($"Corridors[{i}] references rooms ({c.RoomA}, {c.RoomB}) outside [0, {n - 1}]");
                    continue;
                }
                if (c.RoomA == c.RoomB)
                {
                    errors.Add($"Corridors[{i}] is a self-loop on room {c.RoomA}");
                    continue;
                }
                if (c.Width <= 0)
                    errors.Add($"Corridors[{i}].Width {c.Width.ToString("R", ci)} must be > 0");

                if (c.Path == null || c.Path.Count != 2)
                {
                    errors.Add($"Corridors[{i}].Path has {(c.Path == null ? 0 : c.Path.Count)} points, expected 2");
                    continue;
                }

                var p0 = c.Path[0];
                var p1 = c.Path[1];
                bool sameX = Math.Abs(p0.X - p1.X) <= Eps;
                bool sameZ = Math.Abs(p0.Z - p1.Z) <= Eps;
                if (!sameX && !sameZ)
                    errors.Add($"Corridors[{i}] is not axis-aligned: {p0} -> {p1}");
                if (c.Length <= Eps)
                    errors.Add($"Corridors[{i}].Length {c.Length.ToString("R", ci)} must be > 0");

                var ra = layout.Rooms[c.RoomA];
                var rb = layout.Rooms[c.RoomB];
                if (ra == null || rb == null) continue;

                var expA = ra.BoundaryPoint(c.DirectionFromA);
                var expB = rb.BoundaryPoint(c.DirectionFromA.Opposite());
                if (Math.Abs(p0.X - expA.X) > Eps || Math.Abs(p0.Z - expA.Z) > Eps)
                    errors.Add($"Corridors[{i}].Path[0] {p0} != Rooms[{c.RoomA}].BoundaryPoint({c.DirectionFromA}) {expA}");
                if (Math.Abs(p1.X - expB.X) > Eps || Math.Abs(p1.Z - expB.Z) > Eps)
                    errors.Add($"Corridors[{i}].Path[1] {p1} != Rooms[{c.RoomB}].BoundaryPoint({c.DirectionFromA.Opposite()}) {expB}");

                var dir = LevelGenerator.DirectionBetween(ra.CellX, ra.CellY, rb.CellX, rb.CellY);
                if (dir == null)
                    errors.Add($"Corridors[{i}] joins non-adjacent cells ({ra.CellX},{ra.CellY}) and ({rb.CellX},{rb.CellY})");
                else if (dir.Value != c.DirectionFromA)
                    errors.Add($"Corridors[{i}].DirectionFromA == {c.DirectionFromA}, cells imply {dir.Value}");

                if (!ra.CorridorIds.Contains(i))
                    errors.Add($"Rooms[{c.RoomA}].CorridorIds missing corridor {i}");
                if (!rb.CorridorIds.Contains(i))
                    errors.Add($"Rooms[{c.RoomB}].CorridorIds missing corridor {i}");
            }

            // --- CorridorIds contain nothing extra ---------------------------------
            for (int i = 0; i < n; i++)
            {
                var r = layout.Rooms[i];
                if (r == null || r.CorridorIds == null) continue;
                var seen = new HashSet<int>();
                foreach (int cid in r.CorridorIds)
                {
                    if (cid < 0 || cid >= m)
                    {
                        errors.Add($"Rooms[{i}].CorridorIds contains out-of-range corridor {cid}");
                        continue;
                    }
                    if (!seen.Add(cid))
                        errors.Add($"Rooms[{i}].CorridorIds contains corridor {cid} twice");
                    var c = layout.Corridors[cid];
                    if (c != null && c.RoomA != i && c.RoomB != i)
                        errors.Add($"Rooms[{i}].CorridorIds lists corridor {cid}, which joins {c.RoomA} and {c.RoomB}");
                }
            }

            // --- v2: props and lights ---------------------------------------------
            ValidateContents(layout, s, errors, ci);

            // --- connectivity ------------------------------------------------------
            int[] dist = layout.BfsDistances(0);
            var unreachable = new List<int>();
            for (int i = 0; i < n; i++) if (dist[i] < 0) unreachable.Add(i);
            if (unreachable.Count > 0)
                errors.Add($"graph not connected: {unreachable.Count} room(s) unreachable from room 0 ({string.Join(",", unreachable.ConvertAll(x => x.ToString(ci)).ToArray())})");

            return errors;
        }

        // =============================================================================================
        // v2: furniture + lighting invariants. Mirrors the hard rules RoomFurnisher places against.
        // =============================================================================================
        private static void ValidateContents(LevelLayout layout, LevelGenSettings s, List<string> errors, CultureInfo ci)
        {
            int n = layout.Rooms.Count;
            int darkKeyRooms = 0;

            for (int i = 0; i < n; i++)
            {
                var r = layout.Rooms[i];
                if (r == null) continue;
                if (r.Props == null) { errors.Add($"Rooms[{i}].Props is null"); continue; }
                if (r.Lights == null) { errors.Add($"Rooms[{i}].Lights is null"); continue; }

                bool forcedType = r.Template != null && r.Template.ForcedType.HasValue;
                if (r.Role == RoomRole.Spawn && r.Type != RoomType.Lobby && !forcedType)
                    errors.Add($"Rooms[{i}] is the spawn room but Type == {r.Type} (expected Lobby)");

                var mouths = RoomFurnisher.RoomMouths(layout, r);
                double laneWidth = RoomFurnisher.LaneWidth(s);
                double centreClear = r.Role == RoomRole.Spawn
                    ? RoomFurnisher.SpawnCentreClearance
                    : RoomFurnisher.CentreClearance;

                // --- props ---------------------------------------------------------
                for (int a = 0; a < r.Props.Count; a++)
                {
                    var p = r.Props[a];
                    if (p == null) { errors.Add($"Rooms[{i}].Props[{a}] is null"); continue; }
                    if (Math.Abs(p.RotationDeg % 90.0) > 1e-9)
                        errors.Add($"Rooms[{i}].Props[{a}] rotation {p.RotationDeg.ToString("R", ci)} is not a multiple of 90");

                    var box = RoomFurnisher.PropBox(p);
                    if (!RoomFurnisher.BoxInRoom(r, box, 1e-6))
                        errors.Add($"Rooms[{i}].Props[{a}] ({p.Type}) is not fully inside the room floor");

                    for (int b = a + 1; b < r.Props.Count; b++)
                    {
                        if (r.Props[b] == null) continue;
                        double gap = RoomFurnisher.BoxGap(box, RoomFurnisher.PropBox(r.Props[b]));
                        if (gap < s.PropSpacing - 1e-6)
                            errors.Add($"Rooms[{i}].Props[{a}] and Props[{b}] are only {gap.ToString("R", ci)} m apart (PropSpacing {s.PropSpacing})");
                    }

                    // Lane rule: a clear walking lane from every mouth to the room centre.
                    if (!RoomFurnisher.ClearsAllLanes(r, mouths, p, laneWidth))
                        errors.Add($"Rooms[{i}].Props[{a}] ({p.Type}) intrudes on a corridor mouth lane ({laneWidth.ToString("R", ci)} m wide, prop inflated by {RoomFurnisher.LaneInflate})");

                    // Beds and desks must read as pushed flush against a wall.
                    if (RoomFurnisher.MustBeFlush(r, p.Type) && !RoomFurnisher.TouchesAWall(r, box))
                        errors.Add($"Rooms[{i}].Props[{a}] ({p.Type}) is not flush against a wall (within {RoomFurnisher.WallTouchEps} m)");

                    bool nurseCounter = r.Type == RoomType.NurseStation && p.Type == PropType.Counter;
                    if (!nurseCounter)
                    {
                        double dc = RoomFurnisher.PointBoxDistance(r.Center, box);
                        if (dc < centreClear - 1e-6)
                            errors.Add($"Rooms[{i}].Props[{a}] ({p.Type}) is {dc.ToString("R", ci)} m from the room centre (needs {centreClear})");
                    }
                }

                if (!RoomWalkability.AnchorsWalkable(r, r.Props, mouths))
                    errors.Add($"Rooms[{i}] ({r.Type}, {r.Props.Count} props) has a blocked anchor: the room centre or a mouth's {RoomWalkability.MouthInset} m inside point is inside a prop inflated by {RoomWalkability.PropInflate}");
                if (!RoomWalkability.IsWalkable(r, mouths))
                    errors.Add($"Rooms[{i}] ({r.Type}, {r.Props.Count} props) is cut into disconnected pockets by its furniture: not every corridor mouth and the room centre share one walkable component (or the centre's {RoomWalkability.CentreDiscRadius} m disc is blocked)");

                // --- lights --------------------------------------------------------
                // v3: a template with GenerateLights false is lit by its prefab, not by us.
                bool wantsLights = r.Template == null || r.Template.GenerateLights;
                if (wantsLights && r.Lights.Count < 1)
                    errors.Add($"Rooms[{i}] has no light sources");

                if (r.Lights.Count > RoomFurnisher.MaxTotalLightsPerRoom)
                    errors.Add($"Rooms[{i}] has {r.Lights.Count} lights, at most {RoomFurnisher.MaxTotalLightsPerRoom} allowed");

                int ceilingLamps = 0, enabledCeiling = 0, enabledAny = 0, deskLamps = 0;
                for (int a = 0; a < r.Lights.Count; a++)
                {
                    var l = r.Lights[a];
                    if (l == null) { errors.Add($"Rooms[{i}].Lights[{a}] is null"); continue; }
                    if (l.Height <= 0 || l.Height > s.CeilingHeight + 1e-9)
                        errors.Add($"Rooms[{i}].Lights[{a}] height {l.Height.ToString("R", ci)} outside (0, {s.CeilingHeight}]");
                    if (!RoomFurnisher.PointInRoom(r, l.Position, 1e-6))
                        errors.Add($"Rooms[{i}].Lights[{a}] ({l.Type}) at {l.Position} is outside the room floor");
                    if (l.Type == LightType.CeilingLamp)
                    {
                        ceilingLamps++;
                        if (l.Enabled) enabledCeiling++;
                    }
                    if (l.Enabled) enabledAny++;
                    if (l.Flicker && !l.Enabled)
                        errors.Add($"Rooms[{i}].Lights[{a}] flickers but is disabled");

                    if (l.Type == LightType.DeskLamp)
                    {
                        deskLamps++;
                        if (l.AttachedPropIndex < 0 || l.AttachedPropIndex >= r.Props.Count)
                            errors.Add($"Rooms[{i}].Lights[{a}] is a DeskLamp with AttachedPropIndex {l.AttachedPropIndex} (room has {r.Props.Count} props)");
                        else
                        {
                            var host = r.Props[l.AttachedPropIndex];
                            if (host == null || (host.Type != PropType.Desk && host.Type != PropType.Counter
                                                 && host.Type != PropType.BedsideCabinet))
                                errors.Add($"Rooms[{i}].Lights[{a}] is a DeskLamp attached to {(host == null ? "null" : host.Type.ToString())}");
                        }
                    }
                    else if (l.AttachedPropIndex != -1)
                        errors.Add($"Rooms[{i}].Lights[{a}] ({l.Type}) has AttachedPropIndex {l.AttachedPropIndex}, expected -1");
                }

                if (deskLamps > RoomFurnisher.MaxDeskLampsPerRoom)
                    errors.Add($"Rooms[{i}] has {deskLamps} desk lamps, at most {RoomFurnisher.MaxDeskLampsPerRoom} allowed");

                bool dark = ceilingLamps > 0 && enabledCeiling == 0;
                if (dark && r.Role == RoomRole.Spawn)
                    errors.Add($"Rooms[{i}] is the spawn room and all its ceiling lamps are dead");
                if (dark && r.Role == RoomRole.Key) darkKeyRooms++;
                if (wantsLights && r.Role == RoomRole.Spawn && enabledAny < 1)
                    errors.Add($"Rooms[{i}] is the spawn room and has no enabled light");
            }

            if (darkKeyRooms > 1)
                errors.Add($"{darkKeyRooms} key rooms are dark, at most 1 is allowed");

            // --- corridors ---------------------------------------------------------
            for (int i = 0; i < layout.Corridors.Count; i++)
            {
                var c = layout.Corridors[i];
                if (c == null || c.Path == null || c.Path.Count < 2) continue;
                if (c.Props == null) { errors.Add($"Corridors[{i}].Props is null"); continue; }
                if (c.Lights == null) { errors.Add($"Corridors[{i}].Lights is null"); continue; }

                var cbox = RoomFurnisher.CorridorBox(c);
                var p0 = c.Path[0];
                var p1 = c.Path[c.Path.Count - 1];
                bool alongZ = Math.Abs(p0.X - p1.X) <= 1e-9;
                double len = c.Length;

                for (int a = 0; a < c.Props.Count; a++)
                {
                    var p = c.Props[a];
                    if (p == null) { errors.Add($"Corridors[{i}].Props[{a}] is null"); continue; }
                    var box = RoomFurnisher.PropBox(p);
                    if (box.MinX < cbox.MinX - 1e-6 || box.MaxX > cbox.MaxX + 1e-6
                        || box.MinZ < cbox.MinZ - 1e-6 || box.MaxZ > cbox.MaxZ + 1e-6)
                        errors.Add($"Corridors[{i}].Props[{a}] ({p.Type}) is not inside the corridor floor");

                    for (int b = a + 1; b < c.Props.Count; b++)
                    {
                        if (c.Props[b] == null) continue;
                        if (RoomFurnisher.BoxGap(box, RoomFurnisher.PropBox(c.Props[b])) < -1e-6)
                            errors.Add($"Corridors[{i}].Props[{a}] and Props[{b}] overlap");
                    }

                    // the walkway on one side of the prop must stay >= CorridorWalkway
                    double lo = alongZ ? box.MinX : box.MinZ;
                    double hi = alongZ ? box.MaxX : box.MaxZ;
                    double cMin = alongZ ? cbox.MinX : cbox.MinZ;
                    double cMax = alongZ ? cbox.MaxX : cbox.MaxZ;
                    double free = Math.Max(cMax - hi, lo - cMin);
                    if (free < RoomFurnisher.CorridorWalkway - 1e-6)
                        errors.Add($"Corridors[{i}].Props[{a}] ({p.Type}) leaves only {free.ToString("R", ci)} m of walkway (needs {RoomFurnisher.CorridorWalkway})");

                    // and it must stay clear of both ends
                    double along = alongZ ? Math.Abs(p.Position.Z - p0.Z) : Math.Abs(p.Position.X - p0.X);
                    double halfAlong = (alongZ ? box.MaxZ - box.MinZ : box.MaxX - box.MinX) * 0.5;
                    double endGap = Math.Min(along - halfAlong, len - along - halfAlong);
                    if (endGap < RoomFurnisher.CorridorPropEndGap - 1e-6)
                        errors.Add($"Corridors[{i}].Props[{a}] ({p.Type}) is {endGap.ToString("R", ci)} m from a corridor end (needs {RoomFurnisher.CorridorPropEndGap})");
                }

                if (c.Lights.Count < 1)
                    errors.Add($"Corridors[{i}] has no light sources");
                for (int a = 0; a < c.Lights.Count; a++)
                {
                    var l = c.Lights[a];
                    if (l == null) { errors.Add($"Corridors[{i}].Lights[{a}] is null"); continue; }
                    if (l.Height <= 0 || l.Height > s.CeilingHeight + 1e-9)
                        errors.Add($"Corridors[{i}].Lights[{a}] height {l.Height.ToString("R", ci)} outside (0, {s.CeilingHeight}]");
                    if (l.Position.X < cbox.MinX - 1e-6 || l.Position.X > cbox.MaxX + 1e-6
                        || l.Position.Z < cbox.MinZ - 1e-6 || l.Position.Z > cbox.MaxZ + 1e-6)
                        errors.Add($"Corridors[{i}].Lights[{a}] at {l.Position} is outside the corridor floor");
                }
            }
        }
    }
}
