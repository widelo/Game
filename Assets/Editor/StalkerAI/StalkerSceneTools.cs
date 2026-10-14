using Game.Enemy;
using Game.Player;
using LevelGen.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CS462.StalkerAI.Editor
{
    /// <summary>Optional setup for a future teammate scene; the supplied Main_AStar scene is already wired.</summary>
    public static class StalkerSceneTools
    {
        [MenuItem("CS462/A Star/Install Into Current Scene")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Scene scene = SceneManager.GetActiveScene();
            LevelBuilder builder = FindInScene<LevelBuilder>(scene);
            FirstPersonController player = FindInScene<FirstPersonController>(scene);
            if (builder == null || player == null)
            { UnityEngine.Debug.LogError("Open a scene containing the existing LevelBuilder and Game.Player.FirstPersonController."); return; }
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Install A Star Stalker");
            int group = Undo.GetCurrentGroup();
            AStarDirector director = FindInScene<AStarDirector>(scene);
            if (director == null)
            {
                var root = new GameObject("AStarEnemies");
                Undo.RegisterCreatedObjectUndo(root, "Create A Star Director");
                director = Undo.AddComponent<AStarDirector>(root);
            }
            Undo.RecordObject(director, "Configure A Star Director");
            director.builder = builder; director.player = player.transform;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (EnemySpawner legacy in root.GetComponentsInChildren<EnemySpawner>(true))
                {
                    if (director.modelPrefab == null) director.modelPrefab = legacy.modelPrefab;
                    Undo.RecordObject(legacy, "Disable legacy navigation");
                    legacy.spawnOnLevelBuilt = false; legacy.enabled = false;
                }
                foreach (EnemyHud hud in root.GetComponentsInChildren<EnemyHud>(true))
                { Undo.RecordObject(hud, "Disable legacy HUD"); hud.enabled = false; }
            }
            EditorUtility.SetDirty(director);
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = director.gameObject;
            Undo.CollapseUndoOperations(group);
            UnityEngine.Debug.Log("A* installed in the current scene. Save this as a separate scene to preserve Main. No project settings were changed.", director);
        }

        [MenuItem("CS462/A Star/Open Demo Scene")]
        static void OpenDemo()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene("Assets/Scenes/Main_AStar.unity");
        }

        static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T component = root.GetComponentInChildren<T>(true);
                if (component != null) return component;
            }
            return null;
        }
    }
}
