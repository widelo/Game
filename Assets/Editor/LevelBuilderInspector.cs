// Inspector buttons for LevelBuilder. "Editor" is qualified because namespace Game.Editor shadows the type.
//
// v3: all generation settings now live on a LevelGenProfile asset instead of mirrored fields on the builder,
// so the first thing this inspector has to answer is "is a profile assigned?". The profile is read through
// the serialized property rather than a typed accessor so this file does not have to move in lockstep with
// the LevelGen.Unity assembly.
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    [CustomEditor(typeof(LevelGen.Unity.LevelBuilder))]
    public sealed class LevelBuilderInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var builder = (LevelGen.Unity.LevelBuilder)target;
            EditorGUILayout.Space();

            DrawProfileSection();

            EditorGUILayout.Space();
            if (GUILayout.Button("Build (seed)"))
            {
                var seedProp = serializedObject.FindProperty("seed");
                builder.Build(seedProp != null ? seedProp.intValue : 0);
            }

            if (GUILayout.Button("Build random"))
            {
                int seed = Random.Range(int.MinValue, int.MaxValue);
                var seedProp = serializedObject.FindProperty("seed");
                if (seedProp != null)
                {
                    seedProp.intValue = seed;
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                }
                builder.Build(seed);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Verify 20 seeds"))
            {
                // Run(false) so a failure logs and returns instead of killing the editor.
                bool ok = HeadlessVerify.Run(false);
                UnityEngine.Debug.Log("[LevelBuilderInspector] verify " + (ok ? "PASSED" : "FAILED - see console"));
            }
        }

        /// <summary>
        /// Shows which profile drives this builder, and offers to create/assign the default one when the slot is
        /// empty - a null profile is the single most likely reason a fresh checkout generates nothing.
        /// </summary>
        void DrawProfileSection()
        {
            serializedObject.Update();
            var profileProp = serializedObject.FindProperty("profile");
            if (profileProp == null)
            {
                EditorGUILayout.HelpBox("This LevelBuilder has no serialized 'profile' field (pre-v3 builder).",
                                        MessageType.None);
                return;
            }

            var assigned = profileProp.objectReferenceValue;
            if (assigned != null)
            {
                EditorGUILayout.LabelField("Profile", assigned.name);
                if (GUILayout.Button("Select profile asset")) Selection.activeObject = assigned;
                return;
            }

            EditorGUILayout.HelpBox("No LevelGenProfile assigned - the builder has no generation settings and " +
                                    "Build() will do nothing useful.", MessageType.Warning);
            if (!GUILayout.Button("Create/assign default profile")) return;

            var profile = SceneBootstrap.EnsureLevelGenAssets();
            if (profile == null)
            {
                UnityEngine.Debug.LogError("[LevelBuilderInspector] could not create a profile - see the warnings above.");
                return;
            }
            profileProp.objectReferenceValue = profile;
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            UnityEngine.Debug.Log($"[LevelBuilderInspector] assigned profile '{profile.name}' " +
                                  $"({SceneBootstrap.ProfileAssetPath})");
        }
    }
}
