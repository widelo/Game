// Play-mode proof that every enemy in Main (teammate's A* StalkerBoss and/or the placeholder EnemyAgent) gets
// a MonsterAnimator without any edit to the enemy code, and that the rigid model actually moves (breathing
// scale.y oscillation) without logging errors.
//
// Namespace deliberately does not start with "Game." (see LevelPlayModeTests: Game.Debug shadows Debug).
using System.Collections;
using System.Collections.Generic;
using CS462.StalkerAI;
using Game.Enemy;
using LevelGen.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace LevelGen.Tests.PlayMode
{
    public sealed class MonsterAnimatorTests
    {
        const float BuildTimeoutSeconds = 30f;

        [UnityTest]
        public IEnumerator Enemies_GetMonsterAnimator_AndModelBreathes()
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
            yield return new WaitForSeconds(3f);

            Assert.IsNotNull(Object.FindAnyObjectByType<MonsterAnimatorInstaller>(), "no MonsterAnimatorInstaller bootstrapped");

            var roots = new List<GameObject>();
            foreach (var boss in Object.FindObjectsByType<StalkerBoss>()) roots.Add(boss.gameObject);
            foreach (var agent in Object.FindObjectsByType<EnemyAgent>()) roots.Add(agent.gameObject);
            if (roots.Count == 0) Assert.Ignore("Main spawned no StalkerBoss or EnemyAgent; nothing to animate");

            var animators = new List<MonsterAnimator>();
            foreach (GameObject root in roots)
            {
                var animator = root.GetComponent<MonsterAnimator>();
                Assert.IsNotNull(animator, $"{root.name} has no MonsterAnimator");
                Assert.IsNotNull(root.GetComponent<MonsterMoodProvider>(), $"{root.name} has no mood provider");
                Assert.IsNotNull(animator.Model, $"{root.name}: MonsterAnimator found no visual child");
                animators.Add(animator);
            }

            // Breathing: model scale.y must vary over one second (0.4 Hz * 1.00..1.03 => >= ~0.7 % swing).
            var min = new float[animators.Count];
            var max = new float[animators.Count];
            for (int i = 0; i < animators.Count; i++) { min[i] = float.PositiveInfinity; max[i] = float.NegativeInfinity; }
            float end = Time.time + 1f;
            while (Time.time < end)
            {
                yield return null;
                for (int i = 0; i < animators.Count; i++)
                {
                    if (animators[i] == null) continue; // destroyed by a level rebuild mid-sample
                    float y = animators[i].Model.localScale.y;
                    min[i] = Mathf.Min(min[i], y);
                    max[i] = Mathf.Max(max[i], y);
                }
            }
            for (int i = 0; i < animators.Count; i++)
            {
                if (animators[i] == null) continue;
                if (animators[i].CurrentMood == Mood.Frozen) continue; // weeping-angel beat holds still by design
                Assert.Greater((max[i] - min[i]) / Mathf.Max(1e-6f, max[i]), 0.004f,
                               $"{animators[i].name}: model scale.y did not breathe ({min[i]} .. {max[i]})");
            }

        }
    }
}
