// Hooks MonsterAnimator onto enemies whose code we do not own. Every scanInterval seconds it looks for
// StalkerBoss (teammate's A* stalker) and EnemyAgent (placeholder) components whose root has no
// MonsterAnimator yet, and adds MonsterAnimator + the matching mood provider. Nothing in the enemy's code
// changes; the scan is two FindObjectsByType calls twice a second.
//
// Bootstrap: one installer is created automatically in the active scene when play starts (and on every
// single-mode scene load), so no scene or prefab edit is needed. Set AutoInstall = false before the scene
// loads to opt out, or call Install(...) yourself.
using CS462.StalkerAI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Enemy
{
    [DisallowMultipleComponent]
    public sealed class MonsterAnimatorInstaller : MonoBehaviour
    {
        public const string ObjectName = "MonsterAnimation";
        /// <summary>When true (default), an installer is created in each single-mode loaded scene at play time.</summary>
        public static bool AutoInstall = true;

        [Min(0.05f)] public float scanInterval = 0.5f;
        float _nextScan;

        void Update()
        {
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + scanInterval;
            Scan();
        }

        /// <summary>One pass: attaches to every un-animated enemy. Returns how many were attached.</summary>
        public int Scan()
        {
            int added = 0;
            foreach (StalkerBoss boss in FindObjectsByType<StalkerBoss>())
                if (boss.GetComponent<MonsterAnimator>() == null) { Attach<StalkerMoodProvider>(boss.gameObject); added++; }
            foreach (EnemyAgent agent in FindObjectsByType<EnemyAgent>())
                if (agent.GetComponent<MonsterAnimator>() == null) { Attach<PlaceholderMoodProvider>(agent.gameObject); added++; }
            return added;
        }

        /// <summary>Adds MonsterAnimator and provider <typeparamref name="T"/> to <paramref name="root"/> if missing.</summary>
        public static MonsterAnimator Attach<T>(GameObject root) where T : MonsterMoodProvider
        {
            var animator = root.GetComponent<MonsterAnimator>();
            if (animator == null) animator = root.AddComponent<MonsterAnimator>();
            if (root.GetComponent<T>() == null) root.AddComponent<T>();
            return animator;
        }

        /// <summary>Returns the installer in <paramref name="scene"/>, creating "MonsterAnimation" there if absent.</summary>
        public static MonsterAnimatorInstaller Install(Scene scene)
        {
            foreach (var existing in FindObjectsByType<MonsterAnimatorInstaller>())
                if (existing.gameObject.scene == scene) return existing;
            var go = new GameObject(ObjectName);
            if (scene.IsValid() && scene.isLoaded && go.scene != scene) SceneManager.MoveGameObjectToScene(go, scene);
            return go.AddComponent<MonsterAnimatorInstaller>();
        }

        /// <summary>Returns the installer under <paramref name="parent"/> (or any scene-root one when null), creating it if absent.</summary>
        public static MonsterAnimatorInstaller Install(Transform parent)
        {
            if (parent == null) return Install(SceneManager.GetActiveScene());
            var existing = parent.GetComponentInChildren<MonsterAnimatorInstaller>(true);
            if (existing != null) return existing;
            var go = new GameObject(ObjectName);
            go.transform.SetParent(parent, false);
            return go.AddComponent<MonsterAnimatorInstaller>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            // -= first: with domain reload disabled this method runs every play session.
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (AutoInstall) Install(SceneManager.GetActiveScene());
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (AutoInstall && mode == LoadSceneMode.Single && Application.isPlaying) Install(scene);
        }
    }
}
