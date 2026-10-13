// Headless Windows player build, for CI and for "does this actually ship" checks - and the demo hand-off zip.
//
// Run: Unity.exe -batchmode -nographics -quit -projectPath <proj> -executeMethod Game.Editor.BuildPlayer.Windows64
// Output path: the BUILD_OUT env var, else Build/Win64/CS462Game.exe relative to the project root.
// On success it also writes <outDir>/README-DEMO.txt and zips the output folder to
// Build/CS462Game-Win64-demo.zip (next to the output folder).
// Exits 1 on anything other than BuildResult.Succeeded, 0 on success (a zip failure is logged, not fatal:
// the build itself is still good and the folder can be zipped by hand).
//
// NOTE: the sibling namespace Game.Debug shadows the type name Debug, so UnityEngine.Debug is qualified.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Editor
{
    public static class BuildPlayer
    {
        const string DefaultOutput = "Build/Win64/CS462Game.exe";
        const string ZipName = "CS462Game-Win64-demo.zip";

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

            // Demo hand-off: right window/title/backend, and Main.unity FIRST so the exe opens the game.
            DemoSettings.Apply();
            SceneBootstrap.EnsureBuildSceneOrder();

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
                PackageDemo(directory);
                EditorApplication.Exit(0);
                return;
            }

            UnityEngine.Debug.LogError(line);
            EditorApplication.Exit(1);
        }

        // ------------------------------------------------------------------ demo packaging

        /// <summary>README-DEMO.txt into the build folder, then the folder zipped next to it. Never throws.</summary>
        static void PackageDemo(string outDir)
        {
            try
            {
                string readme = Path.Combine(outDir, "README-DEMO.txt");
                File.WriteAllText(readme, DemoReadme().Replace("\n", "\r\n"));
                UnityEngine.Debug.Log($"[BuildPlayer] wrote {readme}");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[BuildPlayer] README-DEMO.txt not written: {e.Message}");
            }

            string zip = Path.Combine(Path.GetDirectoryName(outDir) ?? outDir, ZipName);
            string via = Zip(outDir, zip);
            if (via != null && File.Exists(zip))
                UnityEngine.Debug.Log($"[BuildPlayer] zip={zip} via={via} size={new FileInfo(zip).Length} " +
                                      $"({new FileInfo(zip).Length / (1024.0 * 1024.0):F1} MB)");
            else
                UnityEngine.Debug.LogWarning($"[BuildPlayer] zip NOT created; hand off the folder {outDir} instead");
        }

        /// <summary>
        /// Zips <paramref name="srcDir"/> into <paramref name="zip"/> and returns which path did it, or null.
        /// Primary: System.IO.Compression.ZipArchive (in System.IO.Compression, which the editor compiles
        /// against without any asmdef/csc.rsp change - ZipFile.CreateFromDirectory lives in
        /// System.IO.Compression.FileSystem, which is NOT referenced by default). Fallback: PowerShell Compress-Archive.
        /// Burst's *_DoNotShip debug folders are left out of the managed zip.
        /// </summary>
        static string Zip(string srcDir, string zip)
        {
            try
            {
                if (File.Exists(zip)) File.Delete(zip);
                string root = Path.GetFullPath(srcDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string top = Path.GetFileName(root); // entries live under "Win64/..." so unzipping makes one folder
                using (var fs = new FileStream(zip, FileMode.CreateNew))
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        string rel = file.Substring(root.Length + 1);
                        if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               .Any(part => part.EndsWith("_DoNotShip", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var entry = archive.CreateEntry((top + "/" + rel).Replace('\\', '/'), System.IO.Compression.CompressionLevel.Optimal);
                        using (var es = entry.Open())
                        using (var src = File.OpenRead(file))
                            src.CopyTo(es);
                    }
                }
                return "System.IO.Compression.ZipArchive";
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[BuildPlayer] managed zip failed ({e.GetType().Name}: {e.Message}); trying PowerShell Compress-Archive");
                try { if (File.Exists(zip)) File.Delete(zip); } catch { /* the fallback overwrites with -Force anyway */ }
            }

            try
            {
                string Quote(string s) => "'" + s.Replace("'", "''") + "'";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -Command \"Compress-Archive -Path " + Quote(srcDir) +
                                " -DestinationPath " + Quote(zip) + " -Force\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0) return "PowerShell Compress-Archive";
                    UnityEngine.Debug.LogWarning($"[BuildPlayer] Compress-Archive exit {p.ExitCode}: {err}");
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[BuildPlayer] PowerShell fallback failed: {e.Message}");
            }
            return null;
        }

        static string DemoReadme() =>
$@"CS462 Game - intermediate demo build ({DateTime.Now:yyyy-MM-dd})
==================================================

RUN
  Unzip anywhere and run CS462Game.exe (keep the _Data folder next to it).
  Opens borderless full-screen at your desktop resolution (1600x900 if switched to windowed).
  Windows 64-bit only.

WHAT YOU ARE SEEING
  A procedurally generated hospital floor: rooms, corridors, furniture, lights. Every seed is a new layout.
  Furniture and corridor lights use our teammates' imported models where a mapping exists (hospital beds,
  gurneys, chairs, LED corridor strips); everything else is built from placeholder primitives.
  One room is a teammate's hand-built prefab room placed inside the generated level.
  The monster is a placeholder model with placeholder AI: it spawns in the room farthest from you and
  walks the NavMesh towards you.
  If the monster catches you, you respawn at the green cube.

CONTROLS
  WASD / mouse     move / look
  Shift            sprint
  Space            jump
  Ctrl or C        crouch (sprint + crouch = slide)
  Esc              release / recapture the mouse
  R                rebuild the level with the same seed
  N / P            next / previous seed (new layout)
  L                flat-light everything (see through the dark)
  F1               toggle the HUD
  Tab              overhead map - WASD pans, scroll zooms, Home refits

KNOWN LIMITS
  - No textures on generated geometry (floors, walls, ceilings are flat colours).
  - The teammate's prefab room is sealed: it has no doorway yet, so you cannot get inside it.
  - Placeholder enemy model and behaviour; no sound, no win/lose screen beyond the respawn.
";
    }
}
