// Procedural audio smoke test: the director installs itself into Main, the drone loop is playing, the scene
// keeps exactly one AudioListener, and synthesizing every clip logs no errors. Nothing here is audible-checked.
using System.Collections;
using Game.Audio;
using LevelGen.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace LevelGen.Tests.PlayMode
{
    public sealed class AudioTests
    {
        const float BuildTimeoutSeconds = 60f;

        [UnityTest]
        public IEnumerator Main_InstallsAudio_DronePlaying_SingleListener_ClipsSynthesizeCleanly()
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
            yield return null;
            yield return null; // AudioDirector.Start

            var director = Object.FindAnyObjectByType<AudioDirector>();
            Assert.IsNotNull(director, "no AudioDirector after loading Main (auto-install did not run)");
            Assert.AreSame(director, AudioDirector.Install(), "Install() must return the existing director");
            Assert.AreEqual(1, Object.FindObjectsByType<AudioDirector>(FindObjectsInactive.Include).Length);

            Assert.IsNotNull(director.Drone, "drone component missing");
            AudioSource drone = director.Drone.Source;
            Assert.IsNotNull(drone.clip, "drone has no clip");
            Assert.GreaterOrEqual(drone.clip.length, 7.9f, "drone loop too short");
            Assert.IsTrue(drone.loop, "drone is not looping");
            Assert.IsTrue(drone.isPlaying, "drone is not playing");

            int listeners = 0;
            foreach (var l in Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include))
                if (l.enabled && l.gameObject.activeInHierarchy) listeners++;
            Assert.AreEqual(1, listeners, "expected exactly one enabled AudioListener");

            ProceduralClips.WarmAll();
            AudioClip chime = ProceduralClips.KeyChime();
            Assert.IsNotNull(chime);
            Assert.Greater(chime.samples, 0);
            Assert.IsNotNull(ProceduralClips.Footstep(true));
            Assert.IsNotNull(ProceduralClips.Creak());
            Assert.Less(ProceduralClips.TotalGenerationMs, 2000.0, "clip synthesis is far slower than budgeted");
        }
    }
}
