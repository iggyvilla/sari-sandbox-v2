using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class SariPerformanceBuild
{
    private const string BuildPathFlag = "-sariPerformanceBuildPath";
    private const string DefaultBuildFolder = "SariCodexPerformance";

    /// <summary>Unity CLI entry point for a repeatable Windows performance-probe player.</summary>
    public static void BuildWindowsPlayer() =>
        BuildPlayer(BuildTarget.StandaloneWindows64, "SariPerformanceProbe.exe");

    /// <summary>Unity CLI entry point for a repeatable macOS performance-probe player.</summary>
    public static void BuildMacPlayer() =>
        BuildPlayer(BuildTarget.StandaloneOSX, "SariPerformanceProbe.app");

    private static void BuildPlayer(BuildTarget target, string defaultFileName)
    {
        string buildPath = CommandLineArgs.Get(BuildPathFlag);
        buildPath = string.IsNullOrWhiteSpace(buildPath)
            ? Path.Combine(Path.GetTempPath(), DefaultBuildFolder, defaultFileName)
            : Path.GetFullPath(buildPath);

        string directory = Path.GetDirectoryName(buildPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Dev Scene.unity" },
            locationPathName = buildPath,
            target = target,
            options = BuildOptions.None
        };

        Debug.Log($"Building Sari performance player at {buildPath}");
        BuildSummary summary = BuildPipeline.BuildPlayer(options).summary;

        Debug.Log(
            $"SARI_PERFORMANCE_BUILD result={summary.result} " +
            $"duration={summary.totalTime} bytes={summary.totalSize} errors={summary.totalErrors}");

        EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
