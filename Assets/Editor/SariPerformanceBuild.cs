using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class SariPerformanceBuild
{
    private const string BuildPathFlag = "-sariPerformanceBuildPath";
    private const string DefaultBuildFolder = "SariCodexPerformance";

    /// <summary>
    /// Unity CLI entry point for a repeatable Windows performance-probe player.
    /// </summary>
    public static void BuildWindowsPlayer()
    {
        string buildPath = GetArgument(BuildPathFlag);
        if (string.IsNullOrWhiteSpace(buildPath))
            buildPath = Path.Combine(
                Path.GetTempPath(),
                DefaultBuildFolder,
                "SariPerformanceProbe.exe");
        else
            buildPath = Path.GetFullPath(buildPath);

        string directory = Path.GetDirectoryName(buildPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Dev Scene.unity" },
            locationPathName = buildPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        Debug.Log($"Building Sari performance player at {buildPath}");
        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        Debug.Log(
            $"SARI_PERFORMANCE_BUILD result={summary.result} " +
            $"duration={summary.totalTime} bytes={summary.totalSize} errors={summary.totalErrors}");

        EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    private static string GetArgument(string flag)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
