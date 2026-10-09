// Property tests for the pure-C# layout generator. Engine-free: these run in EditMode but touch no UnityEngine API.
using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace LevelGen.Core.Tests
{
    [TestFixture]
    public class LevelGeneratorTests
    {
        private static string Join(List<string> errors) => string.Join("\n  ", errors.ToArray());

        [Test]
        public void Generate_IsDeterministic_ForSeeds0To49()
        {
            for (int seed = 0; seed < 50; seed++)
            {
                string a = LevelGenerator.Generate(seed).ToDebugString();
                string b = LevelGenerator.Generate(seed).ToDebugString();
                Assert.AreEqual(a, b, "layout differed between two runs of seed " + seed);
            }
        }

        [Test]
        public void Generate_DifferentSeeds_ProduceDifferentLayouts()
        {
            var seen = new HashSet<string>();
            for (int seed = 0; seed < 50; seed++) seen.Add(LevelGenerator.Generate(seed).ToDebugString());
            Assert.Greater(seen.Count, 40, "seeds 0..49 collapsed to " + seen.Count + " distinct layouts");
        }

        [Test]
        public void Generate_PassesValidator_ForSeeds0To999()
        {
            for (int seed = 0; seed < 1000; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                var errors = LevelValidator.Validate(layout);
                Assert.IsEmpty(errors, "seed " + seed + " invalid:\n  " + Join(errors));
            }
        }

        [Test]
        public void Generate_RoomCountCoversWholeRange_OverSeeds0To999()
        {
            var counts = new Dictionary<int, int>();
            var defaults = new LevelGenSettings();
            for (int seed = 0; seed < 1000; seed++)
            {
                int c = LevelGenerator.Generate(seed).Rooms.Count;
                Assert.GreaterOrEqual(c, defaults.MinRooms, "seed " + seed + " room count " + c);
                Assert.LessOrEqual(c, defaults.MaxRooms, "seed " + seed + " room count " + c);
                counts[c] = counts.ContainsKey(c) ? counts[c] + 1 : 1;
            }
            for (int c = defaults.MinRooms; c <= defaults.MaxRooms; c++)
                Assert.IsTrue(counts.ContainsKey(c), "room count " + c + " never produced over seeds 0..999");
        }

        [Test]
        public void Generate_ProducesBothShapes_OverSeeds0To199()
        {
            bool rect = false, ellipse = false;
            for (int seed = 0; seed < 200; seed++)
            {
                foreach (var r in LevelGenerator.Generate(seed).Rooms)
                {
                    if (r.Shape == RoomShape.Rectangle) rect = true;
                    else if (r.Shape == RoomShape.Ellipse) ellipse = true;
                }
            }
            Assert.IsTrue(rect, "no Rectangle room over seeds 0..199");
            Assert.IsTrue(ellipse, "no Ellipse room over seeds 0..199");
        }

        [Test]
        public void Generate_HasOneSpawnAndThreeKeys_ForSeeds0To199()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                Assert.IsNotNull(layout.SpawnRoom, "seed " + seed + " has no spawn room");
                Assert.AreEqual(0, layout.SpawnRoom.Id, "seed " + seed + " spawn is not room 0");
                int keys = 0;
                foreach (var r in layout.KeyRooms) keys++;
                Assert.AreEqual(3, keys, "seed " + seed + " key room count");
            }
        }

        [Test]
        public void Generate_SpawnRoomCentredAtOrigin_ForSeeds0To49()
        {
            for (int seed = 0; seed < 50; seed++)
            {
                var spawn = LevelGenerator.Generate(seed).SpawnRoom;
                Assert.AreEqual(0.0, spawn.Center.X, 1e-9, "seed " + seed);
                Assert.AreEqual(0.0, spawn.Center.Z, 1e-9, "seed " + seed);
            }
        }

        [Test]
        public void Generate_EveryCorridorHasTwoPointsAndPositiveLength_ForSeeds0To199()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                foreach (var c in layout.Corridors)
                {
                    Assert.AreEqual(2, c.Path.Count, "seed " + seed + " corridor " + c.Id);
                    Assert.Greater(c.Length, 0.0, "seed " + seed + " corridor " + c.Id);
                    Assert.AreEqual(layout.Settings.CorridorWidth, c.Width, 1e-12);
                }
            }
        }

        [Test]
        public void BfsDistances_SpawnIsZero_AndAllRoomsReachable()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                int[] dist = layout.BfsDistances(0);
                Assert.AreEqual(0, dist[0]);
                for (int i = 0; i < dist.Length; i++)
                    Assert.GreaterOrEqual(dist[i], 0, "seed " + seed + " room " + i + " unreachable");
            }
        }

        [Test]
        public void Settings_Throw_WhenMaxRoomSizeNotSmallerThanCellSize()
        {
            var bad = new LevelGenSettings { CellSize = 16.0, MaxRoomSize = 16.0 };
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0, bad));

            var worse = new LevelGenSettings { CellSize = 10.0, MaxRoomSize = 16.0 };
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0, worse));
        }

        [Test]
        public void Settings_Throw_WhenRoomCountRangeIsInvalid()
        {
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0, new LevelGenSettings { MinRooms = 0 }));
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0, new LevelGenSettings { MinRooms = 7, MaxRooms = 6 }));
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0, new LevelGenSettings { MinRooms = 1, MaxRooms = 17, GridWidth = 4, GridHeight = 4 }));
        }

        [Test]
        public void CustomSettings_FullThreeByThreeGrid_Validates_ForSeeds0To99()
        {
            for (int seed = 0; seed < 100; seed++)
            {
                var settings = new LevelGenSettings
                {
                    MinRooms = 9,
                    MaxRooms = 9,
                    GridWidth = 3,
                    GridHeight = 3
                };
                var layout = LevelGenerator.Generate(seed, settings);
                Assert.AreEqual(9, layout.Rooms.Count, "seed " + seed);
                var errors = LevelValidator.Validate(layout);
                Assert.IsEmpty(errors, "seed " + seed + " invalid:\n  " + Join(errors));

                string again = LevelGenerator.Generate(seed, new LevelGenSettings
                {
                    MinRooms = 9, MaxRooms = 9, GridWidth = 3, GridHeight = 3
                }).ToDebugString();
                Assert.AreEqual(layout.ToDebugString(), again, "seed " + seed + " not deterministic under custom settings");
            }
        }

        [Test]
        public void CustomSettings_SingleRoom_ClampsKeyRoomsAndValidates()
        {
            var settings = new LevelGenSettings { MinRooms = 1, MaxRooms = 1, GridWidth = 1, GridHeight = 1 };
            for (int seed = 0; seed < 20; seed++)
            {
                var layout = LevelGenerator.Generate(seed, settings);
                Assert.AreEqual(1, layout.Rooms.Count);
                Assert.AreEqual(0, layout.Corridors.Count);
                int keys = 0;
                foreach (var r in layout.KeyRooms) keys++;
                Assert.AreEqual(0, keys, "key rooms should clamp to 0 when only the spawn room exists");
                var errors = LevelValidator.Validate(layout);
                Assert.IsEmpty(errors, "seed " + seed + " invalid:\n  " + Join(errors));
            }
        }

        // =====================================================================================
        // v2: archetypes, furniture, lights
        // =====================================================================================

        [Test]
        public void Generate_SpawnRoomIsLobbyWithEnabledLight_ForSeeds0To199()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                var spawn = layout.SpawnRoom;
                Assert.IsNotNull(spawn, "seed " + seed);
                Assert.AreEqual(RoomType.Lobby, spawn.Type, "seed " + seed + " spawn room type");
                int enabled = 0;
                foreach (var l in spawn.Lights) if (l.Enabled) enabled++;
                Assert.Greater(enabled, 0, "seed " + seed + " spawn room has no enabled light");
            }
        }

        [Test]
        public void Generate_EveryRoomTypeAppears_OverSeeds0To199()
        {
            var seen = new HashSet<RoomType>();
            for (int seed = 0; seed < 200; seed++)
                foreach (var r in LevelGenerator.Generate(seed).Rooms) seen.Add(r.Type);
            foreach (RoomType t in Enum.GetValues(typeof(RoomType)))
                Assert.IsTrue(seen.Contains(t), "RoomType." + t + " never produced over seeds 0..199");
        }

        [Test]
        public void Generate_EveryPropTypeAppears_OverSeeds0To199()
        {
            var seen = new HashSet<PropType>();
            for (int seed = 0; seed < 200; seed++)
                foreach (var p in LevelGenerator.Generate(seed).AllProps()) seen.Add(p.Type);
            foreach (PropType t in Enum.GetValues(typeof(PropType)))
                Assert.IsTrue(seen.Contains(t), "PropType." + t + " never produced over seeds 0..199");
        }

        [Test]
        public void Generate_EveryRoomHasALight_AndEveryCorridorHasAStrip_ForSeeds0To199()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                foreach (var r in layout.Rooms)
                    Assert.Greater(r.Lights.Count, 0, "seed " + seed + " room " + r.Id + " has no light");
                foreach (var c in layout.Corridors)
                {
                    int strips = 0;
                    foreach (var l in c.Lights) if (l.Type == LightType.CorridorStrip) strips++;
                    Assert.Greater(strips, 0, "seed " + seed + " corridor " + c.Id + " has no strip light");
                }
            }
        }

        [Test]
        public void Generate_ProducesDarkRoomFlickerAndDeskLamps_OverSeeds0To199()
        {
            bool dark = false, flicker = false, deskLamp = false, sconce = false, deadStrip = false;
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                foreach (var r in layout.Rooms)
                {
                    int ceiling = 0, on = 0;
                    foreach (var l in r.Lights)
                    {
                        if (l.Type == LightType.CeilingLamp) { ceiling++; if (l.Enabled) on++; }
                        if (l.Type == LightType.DeskLamp) deskLamp = true;
                        if (l.Type == LightType.WallSconce) sconce = true;
                        if (l.Flicker) flicker = true;
                    }
                    if (ceiling > 0 && on == 0) dark = true;
                }
                foreach (var l in layout.AllLights())
                    if (l.Type == LightType.CorridorStrip && !l.Enabled) deadStrip = true;
            }
            Assert.IsTrue(dark, "no dark room over seeds 0..199");
            Assert.IsTrue(flicker, "no flickering light over seeds 0..199");
            Assert.IsTrue(deskLamp, "no desk lamp over seeds 0..199");
            Assert.IsTrue(sconce, "no wall sconce over seeds 0..199");
            Assert.IsTrue(deadStrip, "no dead corridor strip over seeds 0..199");
        }

        [Test]
        public void Generate_MouthLanesStayClear_OverSeeds0To499()
        {
            for (int seed = 0; seed < 500; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                double laneWidth = RoomFurnisher.LaneWidth(layout.Settings);
                foreach (var r in layout.Rooms)
                {
                    var mouths = RoomFurnisher.RoomMouths(layout, r);
                    foreach (var p in r.Props)
                        Assert.IsTrue(RoomFurnisher.ClearsAllLanes(r, mouths, p, laneWidth),
                            "seed " + seed + " room " + r.Id + " (" + r.Type + ") prop " + p.Type
                            + " at " + p.Position + " intrudes on a mouth lane");
                    Assert.IsTrue(RoomWalkability.AnchorsWalkable(r, r.Props, mouths),
                        "seed " + seed + " room " + r.Id + " has a blocked anchor");
                }
            }
        }

        [Test]
        public void Generate_BedsAndDesksAreFlushToAWall_OverSeeds0To499()
        {
            int checked_ = 0;
            for (int seed = 0; seed < 500; seed++)
                foreach (var r in LevelGenerator.Generate(seed).Rooms)
                    foreach (var p in r.Props)
                    {
                        if (!RoomFurnisher.MustBeFlush(r, p.Type)) continue;
                        checked_++;
                        Assert.IsTrue(RoomFurnisher.TouchesAWall(r, RoomFurnisher.PropBox(p)),
                            "seed " + seed + " room " + r.Id + " " + p.Type + " at " + p.Position + " is not flush to a wall");
                    }
            Assert.Greater(checked_, 0, "no beds or desks in rectangular rooms over seeds 0..499");
        }

        [Test]
        public void Regression_Seed4Room1_MouthLanesClearAndCentreReachable()
        {
            var layout = LevelGenerator.Generate(4);
            var room = layout.Rooms[1];
            var mouths = RoomFurnisher.RoomMouths(layout, room);
            double laneWidth = RoomFurnisher.LaneWidth(layout.Settings);
            Assert.Greater(mouths.Count, 0, "seed 4 room 1 has no mouths");

            foreach (var p in room.Props)
                Assert.IsTrue(RoomFurnisher.ClearsAllLanes(room, mouths, p, laneWidth),
                    "seed 4 room 1 " + p.Type + " at " + p.Position + " blocks a mouth lane");

            Assert.IsTrue(RoomWalkability.AnchorsWalkable(room, room.Props, mouths),
                "seed 4 room 1 has a blocked anchor (centre or mouth inside point)");
            Assert.IsTrue(RoomWalkability.IsWalkable(room, mouths),
                "seed 4 room 1 centre is not reachable from every mouth");
            Assert.IsEmpty(LevelValidator.Validate(layout), "seed 4 must validate clean");
        }

        [Test]
        public void Generate_RoomsStayWalkable_OverSeeds0To499()
        {
            for (int seed = 0; seed < 500; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                foreach (var r in layout.Rooms)
                {
                    var mouths = RoomFurnisher.RoomMouths(layout, r);
                    Assert.IsTrue(RoomWalkability.IsWalkable(r, mouths),
                        "seed " + seed + " room " + r.Id + " (" + r.Shape + " " + r.Type + " "
                        + r.SizeX.ToString("F1") + "x" + r.SizeZ.ToString("F1") + ", "
                        + r.Props.Count + " props) is cut into pockets by its furniture");
                }
            }
        }

        [Test]
        public void Walkability_DetectsARingOfPropsAroundTheCentre()
        {
            var layout = LevelGenerator.Generate(4);
            var room = layout.Rooms[1];
            var mouths = RoomFurnisher.RoomMouths(layout, room);
            Assert.IsTrue(RoomWalkability.IsWalkable(room, mouths), "seed 4 room 1 should start walkable");

            // drop a bed right on the room centre: the centre disc is no longer clear
            room.Props.Add(new Prop
            {
                Type = PropType.HospitalBed,
                Position = room.Center,
                RotationDeg = 0,
                SizeX = 0.9, SizeZ = 2.1, Height = 0.6
            });
            Assert.IsFalse(RoomWalkability.IsWalkable(room, mouths), "a prop on the room centre must fail the disc check");
            Assert.IsNotEmpty(LevelValidator.Validate(layout), "the validator must report the blocked room");
        }

        [Test]
        public void Generate_RespectsLightBudget_OverSeeds0To199()
        {
            int worstTotal = 0, worstDesk = 0;
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                foreach (var r in layout.Rooms)
                {
                    int desk = 0;
                    foreach (var l in r.Lights) if (l.Type == LightType.DeskLamp) desk++;
                    if (r.Lights.Count > worstTotal) worstTotal = r.Lights.Count;
                    if (desk > worstDesk) worstDesk = desk;
                    Assert.LessOrEqual(desk, RoomFurnisher.MaxDeskLampsPerRoom,
                        "seed " + seed + " room " + r.Id + " has " + desk + " desk lamps");
                    Assert.LessOrEqual(r.Lights.Count, RoomFurnisher.MaxTotalLightsPerRoom,
                        "seed " + seed + " room " + r.Id + " has " + r.Lights.Count + " lights");
                }
            }
            Assert.Greater(worstTotal, 1, "light budget test never saw a multi-light room");
            Assert.Greater(worstDesk, 0, "light budget test never saw a desk lamp");
        }

        [Test]
        public void Generate_PatientRoomsAverageAtLeastTwoProps_OverSeeds0To199()
        {
            int rooms = 0, props = 0;
            for (int seed = 0; seed < 200; seed++)
                foreach (var r in LevelGenerator.Generate(seed).Rooms)
                    if (r.Type == RoomType.PatientRoom) { rooms++; props += r.Props.Count; }
            Assert.Greater(rooms, 0, "no patient rooms at all over seeds 0..199");
            double avg = (double)props / rooms;
            Assert.GreaterOrEqual(avg, 2.0, "patient rooms average only " + avg + " props over " + rooms + " rooms");
        }

        [Test]
        public void Generate_HasAdminRoom_WhenSixOrMoreRooms_OverSeeds0To199()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                if (layout.Rooms.Count < 6) continue;
                bool admin = false;
                foreach (var r in layout.Rooms)
                    if (r.Type == RoomType.Office || r.Type == RoomType.NurseStation) admin = true;
                Assert.IsTrue(admin, "seed " + seed + " has " + layout.Rooms.Count + " rooms but no Office/NurseStation");
            }
        }

        [Test]
        public void Generate_WardsOnlyInRoomsAtLeastTenMetresOnBothAxes_OverSeeds0To199()
        {
            for (int seed = 0; seed < 200; seed++)
                foreach (var r in LevelGenerator.Generate(seed).Rooms)
                    if (r.Type == RoomType.Ward)
                    {
                        Assert.GreaterOrEqual(r.SizeX, 10.0, "seed " + seed + " room " + r.Id);
                        Assert.GreaterOrEqual(r.SizeZ, 10.0, "seed " + seed + " room " + r.Id);
                    }
        }

        [Test]
        public void AllPropsAndAllLights_MatchPerRoomAndPerCorridorCounts_ForSeeds0To49()
        {
            for (int seed = 0; seed < 50; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                int props = 0, lights = 0;
                foreach (var r in layout.Rooms) { props += r.Props.Count; lights += r.Lights.Count; }
                foreach (var c in layout.Corridors) { props += c.Props.Count; lights += c.Lights.Count; }
                int allProps = 0; foreach (var p in layout.AllProps()) allProps++;
                int allLights = 0; foreach (var l in layout.AllLights()) allLights++;
                Assert.AreEqual(props, allProps, "seed " + seed + " AllProps count");
                Assert.AreEqual(lights, allLights, "seed " + seed + " AllLights count");
            }
        }

        [Test]
        public void PropDims_CoversEveryPropType_AndMatchesPlacedProps()
        {
            foreach (PropType t in Enum.GetValues(typeof(PropType)))
                Assert.IsTrue(RoomFurnisher.PropDims.ContainsKey(t), "PropDims missing " + t);

            for (int seed = 0; seed < 50; seed++)
                foreach (var p in LevelGenerator.Generate(seed).AllProps())
                {
                    var d = RoomFurnisher.PropDims[p.Type];
                    Assert.AreEqual(d.SizeX, p.SizeX, 1e-12, "seed " + seed + " " + p.Type + " SizeX");
                    Assert.AreEqual(d.SizeZ, p.SizeZ, 1e-12, "seed " + seed + " " + p.Type + " SizeZ");
                    Assert.AreEqual(d.Height, p.Height, 1e-12, "seed " + seed + " " + p.Type + " Height");
                    Assert.AreEqual(0.0, p.RotationDeg % 90.0, 1e-12, "rotation must be a multiple of 90");
                }
        }

        [Test]
        public void Settings_Throw_ForV2HospitalScaleKnobs()
        {
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0, new LevelGenSettings { CorridorWidth = 1.5 }));
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0, new LevelGenSettings { MinRoomSize = 7.0, MouthClearance = 4.0 }));
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0, new LevelGenSettings { CeilingHeight = 2.0 }));
        }

        [Test]
        public void Validator_DetectsPropOutsideRoom()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                Room victim = null;
                foreach (var r in layout.Rooms) if (r.Props.Count > 0) { victim = r; break; }
                if (victim == null) continue;
                Assert.IsEmpty(LevelValidator.Validate(layout), "seed " + seed);
                victim.Props[0].Position = new Vec2(victim.Center.X + victim.HalfX + 5.0, victim.Center.Z);
                Assert.IsNotEmpty(LevelValidator.Validate(layout), "a prop shoved outside the floor must be reported");
                return;
            }
            Assert.Fail("no room with props found over seeds 0..199");
        }

        [Test]
        public void Validator_DetectsOverlappingProps_AndBlockedCentre()
        {
            var layout = LevelGenerator.Generate(3);
            Room victim = null;
            foreach (var r in layout.Rooms) if (r.Props.Count >= 2) { victim = r; break; }
            Assert.IsNotNull(victim, "seed 3 has no room with two props");
            Assert.IsEmpty(LevelValidator.Validate(layout));
            victim.Props[1].Position = victim.Props[0].Position;
            Assert.IsNotEmpty(LevelValidator.Validate(layout), "two props at the same position must be reported");

            var layout2 = LevelGenerator.Generate(3);
            Room v2 = null;
            foreach (var r in layout2.Rooms)
                if (r.Props.Count > 0 && r.Type != RoomType.NurseStation) { v2 = r; break; }
            Assert.IsNotNull(v2);
            v2.Props[0].Position = v2.Center;
            Assert.IsNotEmpty(LevelValidator.Validate(layout2), "a prop on the room centre must be reported");
        }

        [Test]
        public void Validator_DetectsDeadSpawnRoomAndBadDeskLamp()
        {
            var layout = LevelGenerator.Generate(11);
            Assert.IsEmpty(LevelValidator.Validate(layout));
            foreach (var l in layout.SpawnRoom.Lights) { l.Enabled = false; l.Flicker = false; }
            Assert.IsNotEmpty(LevelValidator.Validate(layout), "a dead spawn room must be reported");

            var layout2 = LevelGenerator.Generate(11);
            layout2.Rooms[0].Lights.Add(new LightSource
            {
                Type = LightType.DeskLamp,
                Position = layout2.Rooms[0].Center,
                Height = 1.0,
                Enabled = true,
                AttachedPropIndex = 999
            });
            Assert.IsNotEmpty(LevelValidator.Validate(layout2), "a DeskLamp with a bogus AttachedPropIndex must be reported");
        }

        [Test]
        public void Validator_DetectsBlockedCorridorWalkway()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                Corridor victim = null;
                foreach (var c in layout.Corridors) if (c.Props.Count > 0) { victim = c; break; }
                if (victim == null) continue;
                Assert.IsEmpty(LevelValidator.Validate(layout), "seed " + seed);
                // slide the prop onto the corridor centre-line: neither side keeps 1.6 m clear
                var p0 = victim.Path[0];
                var p1 = victim.Path[1];
                bool alongZ = Math.Abs(p0.X - p1.X) <= 1e-9;
                victim.Props[0].Position = alongZ
                    ? new Vec2(p0.X, victim.Props[0].Position.Z)
                    : new Vec2(victim.Props[0].Position.X, p0.Z);
                Assert.IsNotEmpty(LevelValidator.Validate(layout), "a corridor prop on the centre-line must be reported");
                return;
            }
            Assert.Fail("no corridor with props found over seeds 0..199");
        }

        [Test]
        public void Generate_DarkKeyRooms_NeverMoreThanOne_ForSeeds0To999()
        {
            for (int seed = 0; seed < 1000; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                int dark = 0;
                foreach (var r in layout.KeyRooms)
                {
                    int ceiling = 0, on = 0;
                    foreach (var l in r.Lights)
                        if (l.Type == LightType.CeilingLamp) { ceiling++; if (l.Enabled) on++; }
                    if (ceiling > 0 && on == 0) dark++;
                }
                Assert.LessOrEqual(dark, 1, "seed " + seed + " has " + dark + " dark key rooms");
            }
        }

        [Test]
        public void Validator_DetectsBrokenLayout()
        {
            var layout = LevelGenerator.Generate(1234);
            Assert.IsEmpty(LevelValidator.Validate(layout));

            layout.Rooms[1].Center = new Vec2(layout.Rooms[1].Center.X + 0.5, layout.Rooms[1].Center.Z);
            Assert.IsNotEmpty(LevelValidator.Validate(layout), "moving a room should break boundary-point checks");
        }

        [Test]
        public void Validator_DetectsDisconnectedGraph()
        {
            var layout = LevelGenerator.Generate(7);
            layout.Corridors.Clear();
            foreach (var r in layout.Rooms) r.CorridorIds.Clear();
            var errors = LevelValidator.Validate(layout);
            Assert.IsNotEmpty(errors, "a layout with no corridors must be reported as disconnected");
        }
        // =====================================================================================
        // v3: spawn door budget, room templates, pluggable furnisher
        // =====================================================================================

        private sealed class NoOpFurnisher : ILevelFurnisher
        {
            public int Calls;
            public void Furnish(LevelLayout layout, Random rnd) { Calls++; }
        }

        private static LevelGenSettings WithTemplates(params RoomTemplateSpec[] templates)
        {
            var s = new LevelGenSettings();
            s.Templates = new List<RoomTemplateSpec>(templates);
            return s;
        }

        [Test]
        public void Generate_SpawnHasExactlyOneDoor_ForSeeds0To999()
        {
            for (int seed = 0; seed < 1000; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                var spawn = layout.SpawnRoom;
                Assert.AreEqual(0, spawn.Id, "seed " + seed + " spawn is not room 0");
                Assert.AreEqual(1, spawn.DoorCount,
                    "seed " + seed + " spawn has " + spawn.DoorCount + " doors (SpawnMaxDoors = 1)");
            }
        }

        [Test]
        public void Generate_SpawnMaxDoorsFour_LetsSpawnHaveMoreThanOneDoor_OverSeeds0To199()
        {
            bool sawMultiDoorSpawn = false;
            for (int seed = 0; seed < 200; seed++)
            {
                var settings = new LevelGenSettings { SpawnMaxDoors = 4 };
                var layout = LevelGenerator.Generate(seed, settings);
                Assert.LessOrEqual(layout.SpawnRoom.DoorCount, 4, "seed " + seed);
                if (layout.SpawnRoom.DoorCount > 1) sawMultiDoorSpawn = true;
                Assert.IsEmpty(LevelValidator.Validate(layout), "seed " + seed + " invalid:\n  " + Join(LevelValidator.Validate(layout)));
            }
            Assert.IsTrue(sawMultiDoorSpawn, "SpawnMaxDoors = 4 never produced a spawn with more than one door over seeds 0..199");
        }

        [Test]
        public void Generate_SpawnMaxDoorsTwo_CapsSpawnDoors_OverSeeds0To199()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var layout = LevelGenerator.Generate(seed, new LevelGenSettings { SpawnMaxDoors = 2 });
                Assert.LessOrEqual(layout.SpawnRoom.DoorCount, 2,
                    "seed " + seed + " spawn has " + layout.SpawnRoom.DoorCount + " doors (cap 2)");
                Assert.IsEmpty(LevelValidator.Validate(layout), "seed " + seed);
            }
        }

        [Test]
        public void Templates_Empty_LeavesEveryRoomProcedural_ForSeeds0To49()
        {
            for (int seed = 0; seed < 50; seed++)
            {
                var layout = LevelGenerator.Generate(seed);
                foreach (var r in layout.Rooms)
                {
                    Assert.AreEqual("Procedural", r.TemplateName, "seed " + seed + " room " + r.Id);
                    Assert.IsNotNull(r.Template, "seed " + seed + " room " + r.Id + " has a null Template");
                    Assert.AreEqual(r.Template.Name, r.TemplateName);
                }
                int total = 0;
                foreach (var kv in layout.TemplateUsage) total += kv.Value;
                Assert.AreEqual(layout.Rooms.Count, total, "seed " + seed + " TemplateUsage total");
            }
        }

        [Test]
        public void Templates_PrefabRoom_IsPlacedOften_AndKeepsItsFootprint_OverSeeds0To199()
        {
            int rooms = 0, testRooms = 0;
            for (int seed = 0; seed < 200; seed++)
            {
                var settings = WithTemplates(
                    RoomTemplateSpec.Procedural(),
                    Weighted(RoomTemplateSpec.Prefab("TestRoom", 12, 9, DoorSides.All), 3.0));
                var layout = LevelGenerator.Generate(seed, settings);
                Assert.IsEmpty(LevelValidator.Validate(layout), "seed " + seed + " invalid:\n  " + Join(LevelValidator.Validate(layout)));

                foreach (var r in layout.Rooms)
                {
                    rooms++;
                    if (r.TemplateName != "TestRoom") continue;
                    testRooms++;
                    Assert.AreEqual(12.0, r.SizeX, 1e-12, "seed " + seed + " room " + r.Id + " SizeX");
                    Assert.AreEqual(9.0, r.SizeZ, 1e-12, "seed " + seed + " room " + r.Id + " SizeZ");
                    Assert.AreEqual(RoomShape.Rectangle, r.Shape, "seed " + seed + " room " + r.Id + " shape");
                    Assert.AreEqual(0, r.Props.Count, "seed " + seed + " room " + r.Id + " should have no props");
                    Assert.AreEqual(0, r.Lights.Count, "seed " + seed + " room " + r.Id + " should have no lights");
                    Assert.IsFalse(r.Template.GenerateWalls, "prefab rooms skip generated walls");
                    Assert.IsTrue(r.Template.GenerateFloor, "the generator still lays the floor under a prefab room");
                }
            }
            double share = (double)testRooms / rooms;
            Assert.Greater(share, 0.30, "TestRoom used in only " + (share * 100.0).ToString("F1") + "% of " + rooms + " rooms");
        }

        [Test]
        public void Templates_RestrictedDoorPrefab_IsUsedAsSpawn_AndOnlyWithANorthDoor_OverSeeds0To499()
        {
            int spawnUses = 0, uses = 0;
            for (int seed = 0; seed < 500; seed++)
            {
                var settings = WithTemplates(
                    RoomTemplateSpec.Procedural(),
                    RoomTemplateSpec.Prefab("OneDoorNorth", 10, 10, DoorSides.North, 1, RoleAffinity.SpawnOnly));
                var layout = LevelGenerator.Generate(seed, settings);
                Assert.IsEmpty(LevelValidator.Validate(layout), "seed " + seed + " invalid:\n  " + Join(LevelValidator.Validate(layout)));

                foreach (var r in layout.Rooms)
                {
                    if (r.TemplateName != "OneDoorNorth") continue;
                    uses++;
                    if (r.Role == RoomRole.Spawn) spawnUses++;
                    Assert.AreEqual(1, r.DoorCount, "seed " + seed + " room " + r.Id + " door count");
                    Assert.AreEqual(DoorSides.North, LevelGenerator.AttachedDoorSides(layout, r),
                        "seed " + seed + " room " + r.Id + " corridor is not on the North side");
                }
            }
            Assert.Greater(spawnUses, 0, "OneDoorNorth was never placed as the spawn over seeds 0..499");
            Assert.AreEqual(uses, spawnUses, "OneDoorNorth is SpawnOnly but was used elsewhere");
        }

        [Test]
        public void Templates_ForcedTypeIsHonoured_OverSeeds0To99()
        {
            int used = 0;
            for (int seed = 0; seed < 100; seed++)
            {
                var morgue = RoomTemplateSpec.Prefab("Morgue", 11, 8, DoorSides.All);
                morgue.ForcedType = RoomType.Storage;
                morgue.Weight = 4.0;
                var settings = WithTemplates(RoomTemplateSpec.Procedural(), morgue);
                var layout = LevelGenerator.Generate(seed, settings);
                Assert.IsEmpty(LevelValidator.Validate(layout), "seed " + seed + " invalid:\n  " + Join(LevelValidator.Validate(layout)));
                foreach (var r in layout.Rooms)
                    if (r.TemplateName == "Morgue")
                    {
                        used++;
                        Assert.AreEqual(RoomType.Storage, r.Type, "seed " + seed + " room " + r.Id + " forced type");
                    }
            }
            Assert.Greater(used, 0, "the ForcedType template was never placed over seeds 0..99");
        }

        [Test]
        public void Templates_GenerateLightsFalse_ValidatesWithZeroLights_OverSeeds0To99()
        {
            int dark = 0;
            for (int seed = 0; seed < 100; seed++)
            {
                var unlit = RoomTemplateSpec.Procedural();
                unlit.Name = "UnlitProcedural";
                unlit.Weight = 3.0;
                unlit.GenerateLights = false;
                var settings = WithTemplates(RoomTemplateSpec.Procedural(), unlit);
                var layout = LevelGenerator.Generate(seed, settings);
                var errors = LevelValidator.Validate(layout);
                Assert.IsEmpty(errors, "seed " + seed + " invalid:\n  " + Join(errors));
                foreach (var r in layout.Rooms)
                    if (r.TemplateName == "UnlitProcedural")
                    {
                        dark++;
                        Assert.AreEqual(0, r.Lights.Count, "seed " + seed + " room " + r.Id + " should have no lights");
                    }
            }
            Assert.Greater(dark, 0, "the GenerateLights=false template was never placed over seeds 0..99");
        }

        [Test]
        public void CustomFurnisher_IsCalled_AndLeavesRoomsBare()
        {
            var furnisher = new NoOpFurnisher();
            LevelLayout layout = null;
            Assert.DoesNotThrow(() => { layout = LevelGenerator.Generate(42, new LevelGenSettings(), furnisher); });
            Assert.AreEqual(1, furnisher.Calls, "the custom furnisher was not called exactly once");
            Assert.Greater(layout.Rooms.Count, 0);
            foreach (var r in layout.Rooms)
            {
                Assert.AreEqual(0, r.Props.Count, "room " + r.Id + " should be bare");
                Assert.AreEqual(0, r.Lights.Count, "room " + r.Id + " should be unlit");
            }
            // Geometry is still the generator's job and must match the default pipeline room-for-room.
            var defaultLayout = LevelGenerator.Generate(42);
            Assert.AreEqual(defaultLayout.Rooms.Count, layout.Rooms.Count);
            Assert.AreEqual(defaultLayout.Corridors.Count, layout.Corridors.Count);
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                Assert.AreEqual(defaultLayout.Rooms[i].SizeX, layout.Rooms[i].SizeX, 1e-12, "room " + i + " SizeX");
                Assert.AreEqual(defaultLayout.Rooms[i].Center.X, layout.Rooms[i].Center.X, 1e-12, "room " + i + " centre");
            }
        }

        [Test]
        public void Templates_AreDeterministic_ForSeeds0To49()
        {
            for (int seed = 0; seed < 50; seed++)
            {
                string a = LevelGenerator.Generate(seed, WithTemplates(
                    RoomTemplateSpec.Procedural(),
                    Weighted(RoomTemplateSpec.Prefab("TestRoom", 12, 9, DoorSides.All), 3.0))).ToDebugString();
                string b = LevelGenerator.Generate(seed, WithTemplates(
                    RoomTemplateSpec.Procedural(),
                    Weighted(RoomTemplateSpec.Prefab("TestRoom", 12, 9, DoorSides.All), 3.0))).ToDebugString();
                Assert.AreEqual(a, b, "templated layout differed between two runs of seed " + seed);
                Assert.IsTrue(a.Contains("tpl=TestRoom") || a.Contains("tpl=Procedural"),
                    "ToDebugString must name each room's template");
            }
        }

        [Test]
        public void Settings_Throw_WhenTemplateFixedSizeDoesNotLeaveRoomForCorridors()
        {
            // CellSize 30 => a fixed footprint must stay below 28.
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0,
                WithTemplates(RoomTemplateSpec.Prefab("TooBig", 28.0, 9.0, DoorSides.All))));
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0,
                WithTemplates(RoomTemplateSpec.Prefab("TooDeep", 9.0, 40.0, DoorSides.All))));
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0, new LevelGenSettings { SpawnMaxDoors = 0 }));
            Assert.Throws<ArgumentException>(() => LevelGenerator.Generate(0, new LevelGenSettings { SpawnMaxDoors = 5 }));
            // 27.9 still fits.
            Assert.DoesNotThrow(() => LevelGenerator.Generate(0,
                WithTemplates(RoomTemplateSpec.Prefab("JustFits", 27.9, 9.0, DoorSides.All))));
        }

        private static RoomTemplateSpec Weighted(RoomTemplateSpec t, double weight)
        {
            t.Weight = weight;
            return t;
        }
    }
}
