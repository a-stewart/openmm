#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MM2.EditorTools
{
    /// <summary>
    /// Prepares the standalone build output directory (`<BuildFolder>/resources/`) without bundling
    /// any copyrighted `.ar` data archives or personal savegame files.
    /// Creates an empty `resources/` and `resources/players/` folder with a `README.txt` and copies
    /// the project's `README.md` and open-source license notices alongside the executable.
    /// </summary>
    public class MM2ResourcesBuildProcessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 100;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report == null) return;

            string outputPath = report.summary.outputPath;
            if (string.IsNullOrEmpty(outputPath)) return;

            string buildDir = Directory.Exists(outputPath)
                ? outputPath
                : Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(buildDir)) return;

            PrepareCleanDistributionResources(buildDir);
        }

        public static void PrepareCleanDistributionResources(string buildDir)
        {
            if (string.IsNullOrEmpty(buildDir)) return;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string destResourcesDir = Path.Combine(buildDir, "resources");
            string destPlayersDir = Path.Combine(destResourcesDir, "players");

            Directory.CreateDirectory(destResourcesDir);
            Directory.CreateDirectory(destPlayersDir);

            string resourcesReadmePath = Path.Combine(destResourcesDir, "README.txt");
            string resourcesReadmeContent =
                "Open Madness - Resources Directory\n" +
                "==================================\n\n" +
                "Open Madness does not bundle any original game assets.\n" +
                "To play, copy the .ar archive files from your own legally owned copy of:\n\n" +
                "  • Midtown Madness 2 (mm2core.ar, mm2tex.ar, mm2aud.ar, mm2audex.ar, etc.)\n" +
                "  • Midtown Madness 1 (core.ar, ui.ar, audio.ar, etc.)\n" +
                "  • Community mod .ar archives (cities, vehicles, missions)\n\n" +
                "directly into this 'resources/' folder.\n\n" +
                "When the game starts, it will automatically initialize a clean zero-state driver\n" +
                "profile inside 'resources/players/' if none exists.\n";
            File.WriteAllText(resourcesReadmePath, resourcesReadmeContent);

            string readmeSrc = Path.Combine(projectRoot, "README.md");
            if (File.Exists(readmeSrc))
            {
                File.Copy(readmeSrc, Path.Combine(buildDir, "README.md"), true);
            }

            string thirdPartySrc = Path.Combine(projectRoot, "THIRD_PARTY_NOTICES.md");
            if (File.Exists(thirdPartySrc))
            {
                File.Copy(thirdPartySrc, Path.Combine(buildDir, "THIRD_PARTY_NOTICES.md"), true);
            }

            string oflSrc = Path.Combine(Application.dataPath, "Resources", "Fonts", "OFL.txt");
            if (File.Exists(oflSrc))
            {
                File.Copy(oflSrc, Path.Combine(buildDir, "OFL.txt"), true);
            }

            // Strip Unity's non-distributable Burst/backup debug folders if generated
            foreach (string subDir in Directory.GetDirectories(buildDir))
            {
                string dirName = Path.GetFileName(subDir);
                if (dirName.EndsWith("_BurstDebugInformation_DoNotShip", StringComparison.OrdinalIgnoreCase) ||
                    dirName.EndsWith("_BackUpThisFolder_ButDontShipItWithYourGame", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.Delete(subDir, true);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[MM2ResourcesBuildProcessor] Could not remove debug folder '{subDir}': {ex.Message}");
                    }
                }
            }

            Debug.Log($"[MM2ResourcesBuildProcessor] Prepared clean distribution directory at '{destResourcesDir}' (no .ar archives bundled).");
        }
    }
}
#endif
