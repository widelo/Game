// Play-mode test of the core loop: three keys, locked exit, next floor.
//
// Loads Main.unity, waits for the first build, then drives the loop by TELEPORTING the player (keys cannot be
// pressed from a test): onto each key in turn, then into the exit gate's escape volume, and checks the
// tracker's counters, the escape, and that the next floor (seed + 1) is built with the player at its spawn.
// The enemy is switched off first so a catch cannot teleport the player away mid-test.
//
// Namespace deliberately does not start with "Game." (see LevelPlayModeTests for why).
using System.Collections;
using System.Linq;
using Game.Objectives;
using LevelGen.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace LevelGen.Tests.PlayMode
{
    public sealed class ObjectivesPlayModeTests
    {
        const string SceneName = "Main";
        const float BuildTimeoutSeconds = 30f;

        [UnityTest]
        public IEnumerator CollectThreeKeys_Escape_BuildsNextFloor()
        {
            LogAssert.ignoreFailingMessages = false;
            SceneManager.LoadScene(SceneName, LoadSceneMode.Single);
            yield return null;

            LevelBuilder builder = null;
            ObjectiveTracker tracker = null;
            float deadline = Time.realtimeSinceStartup + BuildTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                builder = Object.FindAnyObjectByType<LevelBuilder>();
                tracker = Object.FindAnyObjectByType<ObjectiveTracker>();
                if (builder != null && builder.CurrentLayout != null && tracker != null && tracker.Gate != null) break;
                yield return null;
            }
            Assert.IsNotNull(builder, "no LevelBuilder in Main.unity");
            Assert.IsNotNull(builder.CurrentLayout, "LevelBuilder produced no layout");
            Assert.IsNotNull(tracker, "no ObjectiveTracker in Main.unity (regenerate it with CS462/Create Main Scene)");
            Assert.IsNotNull(tracker.Gate, "ObjectiveTracker spawned no ExitGate");

            var fpc = Object.FindAnyObjectByType<Game.Player.FirstPersonController>();
            Assert.IsNotNull(fpc, "no FirstPersonController");
            fpc.SetLocked(false);
            Transform player = fpc.transform;

            var enemies = GameObject.Find("Enemies");
            if (enemies != null) enemies.SetActive(false);

            // ---- three keys ----
            Assert.AreEqual(3, tracker.KeysRequired, "KeysRequired");
            var pickups = Object.FindObjectsByType<KeyPickup>(FindObjectsSortMode.None);
            Assert.AreEqual(3, pickups.Length, "KeyPickup objects in the scene");
            Assert.AreEqual(0, tracker.KeysCollected);

            Vector3[] keyPoints = pickups.Select(k => k.transform.position).ToArray();
            for (int i = 0; i < keyPoints.Length; i++)
            {
                Teleport(player, new Vector3(keyPoints[i].x, 1.1f, keyPoints[i].z));
                yield return new WaitForSeconds(0.5f);
                Assert.AreEqual(i + 1, tracker.KeysCollected, $"KeysCollected after key {i + 1}");
            }
            Assert.IsTrue(tracker.AllKeysCollected);
            Assert.IsTrue(tracker.Gate.Unlocked, "gate did not unlock with every key collected");

            // ---- escape ----
            int oldSeed = builder.CurrentLayout.Seed;
            int oldFloor = tracker.Floor;
            Vector3 trigger = tracker.Gate.TriggerCentre;
            Teleport(player, new Vector3(trigger.x, 1.1f, trigger.z));
            yield return new WaitForSeconds(0.3f);
            Assert.IsTrue(tracker.Escaped, $"not Escaped after teleporting into the escape volume at {trigger}");
            Assert.AreEqual(oldFloor + 1, tracker.Floor, "Floor did not increment");

            // ---- next floor ----
            yield return new WaitForSeconds(2.5f);
            Assert.AreEqual(oldSeed + 1, builder.CurrentLayout.Seed, "next floor was not built with seed + 1");
            Vector3 spawn = builder.PlayerSpawnPoint;
            float d = Vector2.Distance(new Vector2(player.position.x, player.position.z), new Vector2(spawn.x, spawn.z));
            Assert.Less(d, 2f, $"player is {d:F2} m from the new spawn {spawn} (at {player.position})");
            Assert.IsFalse(tracker.Escaped, "Escaped should reset on the new floor");
            Assert.AreEqual(0, tracker.KeysCollected, "keys should reset on the new floor");
        }

        static void Teleport(Transform player, Vector3 pos)
        {
            var cc = player.GetComponent<CharacterController>();
            bool was = cc != null && cc.enabled;
            if (cc != null) cc.enabled = false;
            player.position = pos;
            if (cc != null) cc.enabled = was;
        }
    }
}
