// Headless Windows player build, for CI and for "does this actually ship" checks.
//
// Run: Unity.exe -batchmode -nographics -quit -projectPath <proj> -executeMethod Game.Editor.BuildPlayer.Windows64
// Output path: the BUILD_OUT env var, else Build/Win64/CS462Game.exe relative to the project root.
// Exits 1 on anything other than BuildResult.Succeeded, 0 on success.
//
// NOTE: the sibling namespace Game.Debug shadows the type name Debug, so UnityEngine.Debug is qualified.
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Editor
{
    public static class BuildPlayer
    {
        const string DefaultOutput = "Build/Win64/CS462Game.exe";

        [MenuItem("CS462/Build Windows64 Player")]
        public static void Windows64()
        {
            string output = System.Environment.GetEnvironmentVariable("BUILD_OUT");
            if (string.IsNullOrWhiteSpace(output)) output = DefaultOutput;

            // Relative paths are resolved against the project root, which is the editor's working directory
            // in batch mode; making it absolute means the log line is unambiguous either way.
            string absolute = Path.IsPathRooted(output)
                ? output
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), output));

            string directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            UnityEngine.Debug.Log($"[BuildPlayer] target=StandaloneWindows64 out={absolute} " +
                                  $"scenes={scenes.Length} [{string.Join(", ", scenes)}]");

            if (scenes.Length == 0)
            {
                UnityEngine.Debug.LogError("[BuildPlayer] result=Failed errors=1 size=0 " +
                                           "(no enabled scenes in Build Settings)");
                EditorApplication.Exit(1);
                return;
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = absolute,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                // Deliberately not BuildOptions.Development: this is the shippable player, so no script
                // debugging, no profiler and no development console.
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            long size = (long)summary.totalSize;
            if (size == 0 && File.Exists(absolute)) size = new FileInfo(absolute).Length;

            string line = $"[BuildPlayer] result={summary.result} errors={summary.totalErrors} size={size}";
            if (summary.result == BuildResult.Succeeded)
            {
                UnityEngine.Debug.Log(line);
                EditorApplication.Exit(0);
                return;
            }

            UnityEngine.Debug.LogError(line);
            EditorApplication.Exit(1);
        }
    }
}
