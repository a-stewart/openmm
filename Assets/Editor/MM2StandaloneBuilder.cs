using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using MM2.EditorTools;

namespace MM2.Editor
{
    /// <summary>
    /// Automates building the Open Madness standalone player:
    /// 1. Ensures runtime URP shaders & variants (Lit, Unlit, AlphaClip, Transparent) are preserved in Resources
    ///    so Shader.Find() never strips them in a standalone player build.
    /// 2. Creates a clean `<BuildFolder>/resources/` folder without bundling any `.ar` files.
    /// </summary>
    public class MM2StandaloneBuilder : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        private const string DefaultBuildDirName = "Build";
        private const string ExecutableNameLinux = "OpenMadness.x86_64";
        private const string ExecutableNameWindows = "OpenMadness.exe";

        [MenuItem("Open Madness/Build Standalone Player (Linux x64)", priority = 100)]
        public static void BuildLinuxMenu()
        {
            BuildStandalone(BuildTarget.StandaloneLinux64, runAfterBuild: false);
        }

        [MenuItem("Open Madness/Build && Run Standalone Player (Linux x64)", priority = 101)]
        public static void BuildAndRunLinuxMenu()
        {
            BuildStandalone(BuildTarget.StandaloneLinux64, runAfterBuild: true);
        }

        [MenuItem("Open Madness/Build Standalone Player (Windows x64)", priority = 102)]
        public static void BuildWindowsMenu()
        {
            BuildStandalone(BuildTarget.StandaloneWindows64, runAfterBuild: false);
        }

        /// <summary>
        /// Entry point for CLI batchmode builds (Linux x64):
        /// unity -batchmode -nographics -projectPath . -executeMethod MM2.Editor.MM2StandaloneBuilder.BuildLinuxCommandLine -quit
        /// </summary>
        public static void BuildLinuxCommandLine()
        {
            bool ok = BuildStandalone(BuildTarget.StandaloneLinux64, runAfterBuild: false);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
        }

        /// <summary>
        /// Entry point for CLI batchmode builds (Windows x64):
        /// unity -batchmode -nographics -projectPath . -executeMethod MM2.Editor.MM2StandaloneBuilder.BuildWindowsCommandLine -quit
        /// </summary>
        public static void BuildWindowsCommandLine()
        {
            bool ok = BuildStandalone(BuildTarget.StandaloneWindows64, runAfterBuild: false);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
        }

        public static bool BuildStandalone(BuildTarget target, bool runAfterBuild)
        {
            EnsureBuildScenesConfigured();
            EnsureRuntimeShaderVariantsPreserved();

            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            bool isWindows = target == BuildTarget.StandaloneWindows64 || target == BuildTarget.StandaloneWindows;
            string platformSubDir = isWindows ? "Windows-x64" : "Linux-x64";
            string buildFolder = Path.Combine(projectRoot, DefaultBuildDirName, platformSubDir);
            Directory.CreateDirectory(buildFolder);

            string exeName = isWindows ? ExecutableNameWindows : ExecutableNameLinux;
            string locationPathName = Path.Combine(buildFolder, exeName);

            var scenes = new List<string>();
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (s.enabled && !string.IsNullOrEmpty(s.path))
                {
                    scenes.Add(s.path);
                }
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = locationPathName,
                target = target,
                options = runAfterBuild ? BuildOptions.AutoRunPlayer : BuildOptions.None
            };

            Debug.Log($"[MM2StandaloneBuilder] Starting {target} build -> {locationPathName} ...");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[MM2StandaloneBuilder] Build succeeded ({summary.totalSize / (1024 * 1024)} MB in {summary.totalTime.TotalSeconds:F1}s): {locationPathName}");
                return true;
            }

            Debug.LogError($"[MM2StandaloneBuilder] Build failed with result: {summary.result} ({summary.totalErrors} errors).");
            return false;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            EnsureBuildScenesConfigured();
            EnsureRuntimeShaderVariantsPreserved();
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            string outputExePath = report.summary.outputPath;
            if (string.IsNullOrEmpty(outputExePath)) return;

            string buildDir = Directory.Exists(outputExePath)
                ? outputExePath
                : Path.GetDirectoryName(outputExePath);
            if (string.IsNullOrEmpty(buildDir)) return;

            MM2ResourcesBuildProcessor.PrepareCleanDistributionResources(buildDir);
        }

        private static void EnsureBuildScenesConfigured()
        {
            string[] requiredScenes =
            {
                "Assets/Scenes/MenuSystem.unity",
                "Assets/Scenes/GoDrive.unity"
            };

            var current = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            bool modified = false;

            foreach (string scenePath in requiredScenes)
            {
                if (!File.Exists(Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, scenePath)))
                {
                    continue;
                }

                int idx = current.FindIndex(s => string.Equals(s.path, scenePath, StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                {
                    current.Add(new EditorBuildSettingsScene(scenePath, true));
                    modified = true;
                }
                else if (!current[idx].enabled)
                {
                    current[idx].enabled = true;
                    modified = true;
                }
            }

            if (PlayerSettings.SplashScreen.show || PlayerSettings.SplashScreen.showUnityLogo)
            {
                PlayerSettings.SplashScreen.show = false;
                PlayerSettings.SplashScreen.showUnityLogo = false;
                modified = true;
            }

            if (modified)
            {
                EditorBuildSettings.scenes = current.ToArray();
                AssetDatabase.SaveAssets();
            }
        }

        /// <summary>
        /// Creates reference Materials in Assets/Resources/ShaderKeepAlive so Unity's URP shader stripper
        /// includes Lit (Opaque, AlphaClip, Transparent) and Unlit variants in standalone builds.
        /// </summary>
        private static void EnsureRuntimeShaderVariantsPreserved()
        {
            const string folderPath = "Assets/Resources/ShaderKeepAlive";
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
            }
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                AssetDatabase.CreateFolder("Assets/Resources", "ShaderKeepAlive");
            }

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            Shader urpUnlit = Shader.Find("Universal Render Pipeline/Unlit");
            bool createdAny = false;

            if (urpLit != null)
            {
                createdAny |= EnsureMaterialAsset($"{folderPath}/Keep_URP_Lit_Opaque.mat", urpLit, null);
                createdAny |= EnsureMaterialAsset($"{folderPath}/Keep_URP_Lit_AlphaClip.mat", urpLit, mat =>
                {
                    if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
                    mat.EnableKeyword("_ALPHATEST_ON");
                });
                createdAny |= EnsureMaterialAsset($"{folderPath}/Keep_URP_Lit_Transparent.mat", urpLit, mat =>
                {
                    if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                });
            }

            if (urpUnlit != null)
            {
                createdAny |= EnsureMaterialAsset($"{folderPath}/Keep_URP_Unlit.mat", urpUnlit, null);
            }

            if (createdAny)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        private static bool EnsureMaterialAsset(string assetPath, Shader shader, Action<Material> configure)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null) return false;

            Material mat = new Material(shader);
            configure?.Invoke(mat);
            AssetDatabase.CreateAsset(mat, assetPath);
            return true;
        }
    }
}
