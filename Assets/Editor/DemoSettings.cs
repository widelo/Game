// PlayerSettings for the hand-off demo build. Called from SceneBootstrap.CreateMainScene and from
// BuildPlayer.Windows64, so a fresh checkout and a CI build ship the same window/title/backend.
// Idempotent: every value is written only when it differs, and every change is logged.
//
// NOTE: the sibling namespace Game.Debug shadows the type name Debug, so UnityEngine.Debug is qualified.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Game.Editor
{
    public static class DemoSettings
    {
        public const string ProductName = "CS462 Game";
        const int Width = 1600;
        const int Height = 900;

        [MenuItem("CS462/Apply Demo Player Settings")]
        public static void Apply()
        {
            var changes = new List<string>();

            if (PlayerSettings.productName != ProductName)
            { changes.Add($"productName '{PlayerSettings.productName}' -> '{ProductName}'"); PlayerSettings.productName = ProductName; }
            // companyName is deliberately left as the project already has it.

            if (PlayerSettings.defaultScreenWidth != Width || PlayerSettings.defaultScreenHeight != Height)
            {
                changes.Add($"defaultScreen {PlayerSettings.defaultScreenWidth}x{PlayerSettings.defaultScreenHeight} -> {Width}x{Height}");
                PlayerSettings.defaultScreenWidth = Width;
                PlayerSettings.defaultScreenHeight = Height;
            }
            if (PlayerSettings.fullScreenMode != FullScreenMode.FullScreenWindow)
            { changes.Add($"fullScreenMode {PlayerSettings.fullScreenMode} -> FullScreenWindow"); PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow; }
            if (!PlayerSettings.runInBackground)
            { changes.Add("runInBackground -> true"); PlayerSettings.runInBackground = true; }
            if (!PlayerSettings.resizableWindow)
            { changes.Add("resizableWindow -> true"); PlayerSettings.resizableWindow = true; }

            // Mono, not IL2CPP: the demo build should take minutes, not a C++ toolchain round trip.
            var standalone = NamedBuildTarget.Standalone;
            if (PlayerSettings.GetScriptingBackend(standalone) != ScriptingImplementation.Mono2x)
            {
                changes.Add($"Standalone scripting backend {PlayerSettings.GetScriptingBackend(standalone)} -> Mono2x");
                PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.Mono2x);
            }

            // The splash screen can only be hidden on licences that allow it; on others the setter is ignored or
            // throws. Read it back so the log says what actually happened.
            try
            {
                if (PlayerSettings.SplashScreen.show)
                {
                    PlayerSettings.SplashScreen.show = false;
                    changes.Add(PlayerSettings.SplashScreen.show
                        ? "splash: hide refused by licence (still shown)"
                        : "splash -> hidden");
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[DemoSettings] could not hide the splash screen ({e.GetType().Name}: {e.Message}) - left as is");
            }

            UnityEngine.Debug.Log(changes.Count == 0
                ? $"[DemoSettings] already applied (product '{ProductName}', company '{PlayerSettings.companyName}', {Width}x{Height} FullScreenWindow, Mono)"
                : $"[DemoSettings] company '{PlayerSettings.companyName}'; changed: {string.Join("; ", changes)}");
        }
    }
}
