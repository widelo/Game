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
        public static void Windows64() => Build(BuildTarget.StandaloneWindows64, DefaultOutput, ZipName);

        /// <summary>
        /// macOS player (Intel + Apple Silicon universal). Needs the "Mac Build Support (Mono)" module installed
        /// for this editor version; without it the build is refused up front with a clear message.
        /// The .app is unsigned: the sister team must right-click > Open the first time (Gatekeeper).
        /// </summary>
        [MenuItem("CS462/Build macOS Player")]
        public static void MacOS() => Build(BuildTarget.StandaloneOSX, "Build/macOS/CS462Game.app", "CS462Game-macOS-demo.zip");

        /// <summary>WebGL player: runs in any desktop browser, so it is the platform-neutral fallback.</summary>
        [MenuItem("CS462/Build WebGL Player")]
        public static void WebGL() => Build(BuildTarget.WebGL, "Build/WebGL", "CS462Game-WebGL-demo.zip");

        static void Build(BuildTarget target, string defaultOutput, string zipName)
        {
            string output = System.Environment.GetEnvironmentVariable("BUILD_OUT");
            if (string.IsNullOrWhiteSpace(output)) output = defaultOutput;

            BuildTargetGroup group = target == BuildTarget.WebGL ? BuildTargetGroup.WebGL : BuildTargetGroup.Standalone;
            if (target == BuildTarget.StandaloneOSX)
            {
                // Universal (Intel + Apple Silicon). String API so this compiles even without the Mac module.
                EditorUserBuildSettings.SetPlatformSettings("OSXUniversal", "Architecture", "x64ARM64");
                UnityEngine.Debug.Log("[BuildPlayer] macOS architecture -> x64ARM64 (universal)");
            }
            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                UnityEngine.Debug.LogError($"[BuildPlayer] result=Failed errors=1 size=0 (build module for {target} is not " +
                                           "installed in this editor; add it through Unity Hub > Installs > Add modules)");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

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

            UnityEngine.Debug.Log($"[BuildPlayer] target={target} out={absolute} " +
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
                target = target,
                targetGroup = group,
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
                // WebGL's output path IS the folder to ship; for an .exe or .app the folder that holds it is.
                string demoDir = target == BuildTarget.WebGL ? absolute : directory;
                PackageDemo(demoDir, zipName);
                EditorApplication.Exit(0);
                return;
            }

            UnityEngine.Debug.LogError(line);
            EditorApplication.Exit(1);
        }

        // ------------------------------------------------------------------ demo packaging

        /// <summary>README-DEMO.txt into the build folder, then the folder zipped next to it. Never throws.</summary>
        static void PackageDemo(string outDir, string zipName)
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

            string zip = Path.Combine(Path.GetDirectoryName(outDir) ?? outDir, zipName);
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
                               .Any(part => part.EndsWith("_DoNotShip", StringComparison.OrdinalIgnoreCase) || part.EndsWith("_BackUpThisFolder_ButDontShipItWithYourGame", StringComparison.OrdinalIgnoreCase)))
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

PLATFORM NOTES
  Windows: unzip, run CS462Game.exe. If SmartScreen warns, choose ""More info"" > ""Run anyway"" (unsigned build).
  macOS:   extract the .tar.gz (double-click), then right-click CS462Game.app > Open the first time
           (unsigned, not notarized). If macOS says the app is damaged, run in Terminal from the folder:
           xattr -dr com.apple.quarantine CS462Game.app && chmod +x CS462Game.app/Contents/MacOS/*
           Universal binary: runs natively on Intel and Apple Silicon.
  WebGL:   must be served over http, not opened from the file system. In the unzipped folder run
           python3 -m http.server 8000   then open http://localhost:8000 in Chrome/Edge/Firefox.

KNOWN LIMITS
  - No textures on generated geometry (floors, walls, ceilings are flat colours).
  - The teammate's prefab room is sealed: it has no doorway yet, so you cannot get inside it.
  - Placeholder enemy model and behaviour; no sound, no win/lose screen beyond the respawn.
";
    }
}
