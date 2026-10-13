// Play-mode proof that "enemy + NavMesh + generated level" works end to end: the spawner places
// enemyCount enemies on the baked NavMesh with a complete path to the player, and an enemy that can see the
// player closes the distance. Main.unity may not contain an EnemySpawner yet, so the test adds one if absent.
//
// Namespace deliberately does not start with "Game." (see LevelPlayModeTests: Game.Debug shadows Debug).
using System.Collections;
using Game.Enemy;
using LevelGen.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace LevelGen.Tests.PlayMode
{
    public sealed class EnemyPlayModeTests
    {
        const float BuildTimeoutSeconds = 30f;
        static readonly float[] ApproachDistances = { 6f, 5f, 4f };

        [UnityTest]
        public IEnumerator Enemy_SpawnsOnNavMesh_HasPathToPlayer_AndClosesIn()
        {
            LogAssert.ignoreFailingMessages = false;
            SceneManager.LoadScene("Main", LoadSceneMode.Single);
            yield return null;

            LevelBuilder builder = null;
            float deadline = Time.realtimeSinceStartup + BuildTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                builder = Object.FindAnyObjectByType<LevelBuilder>();
                if (builder != null && builder.CurrentLayout != null) break;
                yield return null;
            }
            Assert.IsNotNull(builder?.CurrentLayout, "level did not build");

            var fpc = Object.FindAnyObjectByType<Game.Player.FirstPersonController>();
            Assert.IsNotNull(fpc, "no FirstPersonController in Main");
            fpc.SetLocked(false);
            Transform player = fpc.transform;

            var spawner = Object.FindAnyObjectByType<EnemySpawner>();
            if (spawner == null)
            {
                spawner = new GameObject("EnemySpawner (test)").AddComponent<EnemySpawner>();
                spawner.builder = builder;
                spawner.player = player;
            }

            // Spawner.Start runs next frame; give the player a second to land on the floor too.
            yield return new WaitForSeconds(1f);

            Assert.AreEqual(spawner.enemyCount, spawner.Enemies.Count, "spawner list size");
            Assert.AreEqual(spawner.enemyCount, Object.FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None).Length,
                            "EnemyAgent instances in scene");

            Assert.IsTrue(NavMesh.SamplePosition(player.position, out NavMeshHit playerHit, 2.5f, NavMesh.AllAreas),
                          $"player at {player.position} is not near the NavMesh");
            var path = new NavMeshPath();
            foreach (EnemyAgent e in spawner.Enemies)
            {
                Assert.IsTrue(NavMesh.SamplePosition(e.transform.position, out NavMeshHit hit, 1f, NavMesh.AllAreas),
                              $"{e.name} at {e.transform.position} is not on the NavMesh");
                Assert.IsTrue(NavMesh.CalculatePath(hit.position, playerHit.position, NavMesh.AllAreas, path),
                              $"{e.name}: CalculatePath failed");
                Assert.AreEqual(NavMeshPathStatus.PathComplete, path.status, $"{e.name}: path to player incomplete");
            }

            // Put the player in clear view of the first enemy and check it closes in.
            EnemyAgent enemy = spawner.Enemies[0];
            Assert.IsTrue(TryFindVisibleSpot(enemy.transform.position, out Vector3 spot),
                          "no open NavMesh spot 4-6 m from the enemy with line of sight");
            var cc = player.GetComponent<CharacterController>();
            cc.enabled = false;
            player.position = spot + Vector3.up * 1.1f;
            cc.enabled = true;

            float start = Vector3.Distance(enemy.transform.position, player.position);
            float min = start;
            int caughtBefore = enemy.CaughtCount;
            float end = Time.time + 3f;
            while (Time.time < end && enemy.CaughtCount == caughtBefore)
            {
                yield return null;
                min = Mathf.Min(min, Vector3.Distance(enemy.transform.position, player.position));
            }

            UnityEngine.Debug.Log($"[EnemyPlayModeTests] start {start:F2} m, min {min:F2} m, state {enemy.StateName}, " +
                                  $"caught {enemy.CaughtCount - caughtBefore}");
            Assert.IsTrue(enemy.CaughtCount > caughtBefore || min < start - 1f,
                          $"enemy did not close in: start {start:F2} m, closest {min:F2} m (state {enemy.StateName})");
        }

        /// <summary>A NavMesh point 4-6 m away reachable in a straight line and visible from the enemy's eye.</summary>
        static bool TryFindVisibleSpot(Vector3 enemyPos, out Vector3 spot)
        {
            Vector3 eye = enemyPos + Vector3.up * 2.2f;
            foreach (float d in ApproachDistances)
            {
                for (int i = 0; i < 16; i++)
                {
                    Vector3 dir = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
                    Vector3 target = enemyPos + dir * d;
                    if (NavMesh.Raycast(enemyPos, target, out _, NavMesh.AllAreas)) continue; // blocked on the mesh
                    if (Physics.Linecast(eye, target + Vector3.up * 1.6f, Physics.DefaultRaycastLayers,
                                         QueryTriggerInteraction.Ignore)) continue;
                    spot = target;
                    return true;
                }
            }
            spot = default;
            return false;
        }
    }
}
