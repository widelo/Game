using System.Collections;
using CS462.StalkerAI.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CS462.StalkerAI.Tests
{
    public sealed class StalkerPhysicsTests
    {
        GameObject fixture, level, actors, player;
        AStarNavigation world;
        readonly Vector3 offset = new Vector3(2000f, 0f, 2000f);

        void CreateFixture(bool floor = true)
        {
            // Remote coordinates avoid the open demo scene without changing its contents.
            fixture = new GameObject("AStarPhysicsFixture");
            level = new GameObject("Geometry"); level.transform.SetParent(fixture.transform);
            actors = new GameObject("Actors"); actors.transform.SetParent(fixture.transform);
            player = new GameObject("Target"); player.transform.SetParent(fixture.transform);
            player.transform.position = offset + Vector3.forward * 4f;
            if (floor) Box("Floor", new Vector3(0f, -0.5f, 0f), new Vector3(12f, 1f, 12f));
        }

        GameObject Box(string name, Vector3 center, Vector3 size)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name; box.transform.SetParent(level.transform);
            box.transform.position = offset + center; box.transform.localScale = size;
            return box;
        }

        IEnumerator Build(float radius = 0.3f)
        {
            var settings = new NavigationSettings
            {
                cellSize = 1f, bodyRadius = radius, bodyHeight = 1.8f, wallMargin = 0.03f,
                floorTolerance = 0.02f, groundClearance = 0.05f, buildCellsPerFrame = 10000,
                repathInterval = 0.1f, searchExpansionsPerFrame = 100
            };
            Physics.SyncTransforms();
            world = new AStarNavigation(settings, player.transform, actors.transform);
            yield return world.Build(level.transform);
            Assert.IsTrue(world.Ready, world.Error);
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (fixture != null) Object.Destroy(fixture);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ThinWallBetweenClearNodesBlocksTheEdge()
        {
            CreateFixture();
            Box("Thin wall", new Vector3(0.5f, 1.4f, 0f), new Vector3(0.04f, 2.8f, 12f));
            yield return Build(0.1f);
            Assert.IsTrue(world.TryNode(offset, 0.1f, out int a));
            Assert.IsTrue(world.TryNode(offset + Vector3.right, 0.1f, out int b));
            Assert.IsTrue(world.Graph.IsWalkable(a) && world.Graph.IsWalkable(b));
            Assert.IsFalse(world.Graph.HasEdge(a, 0), "Capsule sweep must catch walls between nodes.");
            var search = new AStarSearch(); search.Begin(world.Graph, a, b);
            while (search.Status == SearchStatus.Searching) search.Step(100);
            Assert.AreEqual(SearchStatus.NoPath, search.Status);
        }

        [UnityTest]
        public IEnumerator MissingFloorAndFreshObstacleAreRejected()
        {
            CreateFixture(false);
            Box("Left floor", new Vector3(-3.5f, -0.5f, 0f), new Vector3(5f, 1f, 10f));
            Box("Right floor", new Vector3(3.5f, -0.5f, 0f), new Vector3(5f, 1f, 10f));
            yield return Build();
            Assert.IsFalse(world.HasSupport(offset), "Void cannot be a walkable node.");
            Assert.IsFalse(world.CanTraverse(offset + Vector3.left * 2f, offset + Vector3.right * 2f));
            Vector3 from = offset + new Vector3(-4f, 0f, 0f), to = offset + new Vector3(-2f, 0f, 0f);
            Assert.IsTrue(world.CanTraverse(from, to));
            Box("New blocker", new Vector3(-3f, 1.3f, 0f), new Vector3(0.1f, 2.6f, 6f));
            Physics.SyncTransforms();
            Assert.IsFalse(world.CanTraverse(from, to), "Movement must reject an obstacle added after the graph build.");
        }

        [UnityTest]
        public IEnumerator VisionUsesFovOcclusionAndExpiringMemory()
        {
            CreateFixture(); yield return Build();
            var observer = new GameObject("Observer"); observer.transform.SetParent(actors.transform);
            observer.transform.position = offset - Vector3.forward * 4f;
            var settings = new StalkerSettings { eyeHeight = 1.5f, senseInterval = 0.05f, memoryDuration = 0.3f };
            var senses = new StalkerPerception(observer.transform, player.transform, world, settings, new System.Random(462));
            senses.Sample(); Assert.IsTrue(senses.Visible);
            Vector3 remembered = senses.LastKnown;
            player.transform.position = offset - Vector3.forward * 5f;
            Assert.IsFalse(senses.CanSeePlayerNow(), "Target behind the observer should be outside FOV.");
            player.transform.position = offset + Vector3.forward * 4f;
            Box("Sight wall", new Vector3(0f, 1.5f, 0f), new Vector3(12f, 3f, 0.2f));
            Physics.SyncTransforms();
            yield return new WaitForSeconds(0.07f);
            senses.Sample(); Assert.IsFalse(senses.Visible); Assert.IsTrue(senses.HasMemory);
            player.transform.position += Vector3.right * 2f;
            yield return new WaitForSeconds(0.07f); senses.Sample();
            Assert.AreEqual(remembered, senses.LastKnown, "Hidden target motion leaked into memory.");
            yield return new WaitForSeconds(0.35f);
            Assert.IsFalse(senses.HasMemory, "Memory must expire.");
            senses.Hear(new NoiseSignal(offset + Vector3.forward, 18f, player.transform));
            Assert.IsTrue(senses.HasMemory, "An audible disturbance should produce a location observation.");
            Assert.LessOrEqual(Vector3.Distance(senses.LastKnown, offset + Vector3.forward), settings.hearingUncertainty + 0.01f);
        }

        StalkerBoss SpawnBoss(StalkerSettings settings)
        {
            var body = new GameObject("Test stalker"); body.transform.SetParent(actors.transform);
            body.transform.position = offset + Vector3.up * 0.03f;
            body.AddComponent<AStarMotor>();
            var boss = body.AddComponent<StalkerBoss>();
            StalkerTarget target = player.GetComponent<StalkerTarget>();
            if (target == null) target = player.AddComponent<StalkerTarget>();
            target.hitInvulnerability = 0f;
            boss.Initialise(world, player.transform, target, new[] { offset }, settings, 462);
            return boss;
        }

        [UnityTest]
        public IEnumerator AttackHasDodgeableWindupAndNonlethalDamage()
        {
            CreateFixture(); yield return Build();
            player.transform.position = offset + Vector3.forward * 1.25f;
            var settings = new StalkerSettings
            {
                eyeHeight = 1.5f, senseInterval = 0.05f, detectionDelay = 0.05f,
                attackWindup = 0.35f, attackCooldown = 0.3f, attackRecovery = 0.1f,
                attackDamage = 25f, logStateChanges = false
            };
            StalkerBoss boss = SpawnBoss(settings);
            StalkerTarget target = player.GetComponent<StalkerTarget>();
            float deadline = Time.time + 4f;
            while (!boss.WindingUp && Time.time < deadline) yield return null;
            Assert.IsTrue(boss.WindingUp, "Natural Patrol->Investigate->Stalk->Chase should reach windup.");
            Assert.AreEqual(100f, target.Health, "Proximity or starting an attack cannot instantly damage.");
            player.transform.position = offset + Vector3.right * 4f; Physics.SyncTransforms();
            yield return new WaitForSeconds(settings.attackWindup + 0.05f);
            Assert.AreEqual(100f, target.Health, "Leaving range during windup should dodge.");
            Assert.AreEqual("MISS", boss.LastAttack);
            player.transform.position = boss.transform.position + boss.transform.forward * 1.25f;
            Physics.SyncTransforms();
            deadline = Time.time + 4f;
            while (target.Health == 100f && Time.time < deadline) yield return null;
            Assert.AreEqual(75f, target.Health, "A confirmed hit should apply exactly 25 damage.");
            Assert.IsTrue(target.Alive);
        }

        [UnityTest]
        public IEnumerator LostTargetLeavesChaseAndDisengageIgnoresNoise()
        {
            CreateFixture(); yield return Build();
            var settings = new StalkerSettings
            {
                eyeHeight = 1.5f, senseInterval = 0.05f, detectionDelay = 0.05f,
                stalkBeforeChase = new Vector2(0.1f, 0.1f), closeAggroDistance = 5f,
                loseSightDelay = 0.15f, memoryDuration = 0.6f, investigationDuration = 0.4f,
                chaseDuration = 1f, disengagementCooldown = 0.5f, logStateChanges = false
            };
            StalkerBoss boss = SpawnBoss(settings);
            float deadline = Time.time + 3f;
            while (boss.State != StalkerState.Chase && Time.time < deadline) yield return null;
            Assert.AreEqual(StalkerState.Chase, boss.State);
            Box("Escape wall", new Vector3(0f, 1.5f, 2f), new Vector3(12f, 3f, 0.2f));
            Physics.SyncTransforms();
            deadline = Time.time + 2f;
            while (boss.State != StalkerState.Disengage && Time.time < deadline) yield return null;
            Assert.AreEqual(StalkerState.Disengage, boss.State, "Lost target must not cause endless chase.");
            float observed = boss.Senses.ObservedTime;
            NoiseBus.Emit(boss.transform.position, 100f, player.transform);
            Assert.AreEqual(observed, boss.Senses.ObservedTime, "Disengagement must ignore immediate reacquisition noise.");
        }
    }
}
