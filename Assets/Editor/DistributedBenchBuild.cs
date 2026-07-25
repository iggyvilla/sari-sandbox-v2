using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// Produces the player used by Distributed Sari Bench.
///
/// The only difference from a normal build is the SARI_DISTRIBUTED_BENCH define, which makes
/// <see cref="WebSocketHandler"/> self-assign a free port, bind on every interface, and register
/// with a coordinator. That define is deliberately NOT checked into ProjectSettings: it would
/// change every build for everyone. It is added here, for this build, and removed again afterwards.
///
/// From the editor: Build > Distributed Sari Bench Player.
/// From CI:
///   Unity -quit -batchmode -projectPath . -executeMethod DistributedBenchBuild.BuildFromCommandLine \
///         -benchBuildPath Builds/SariBench/SariSandbox
/// </summary>
public static class DistributedBenchBuild
{
    private const string Define = "SARI_DISTRIBUTED_BENCH";
    private const string DefaultOutputDirectory = "Builds/SariBench";
    private const string BuildPathArgument = "-benchBuildPath";

    [MenuItem("Build/Distributed Sari Bench Player")]
    public static void BuildFromMenu()
    {
        string extension = DefaultExtension();
        string path = EditorUtility.SaveFilePanel(
            "Build Distributed Sari Bench player",
            DefaultOutputDirectory,
            "SariSandbox" + extension,
            extension.TrimStart('.'));

        if (string.IsNullOrEmpty(path)) return;

        Build(path);
    }

    public static void BuildFromCommandLine()
    {
        string path = ReadArgument(BuildPathArgument)
            ?? System.IO.Path.Combine(DefaultOutputDirectory, "SariSandbox" + DefaultExtension());

        BuildReportSummary summary = Build(path);
        if (summary.Failed) EditorApplication.Exit(1);
    }

    public readonly struct BuildReportSummary
    {
        public readonly bool Failed;
        public readonly string OutputPath;

        public BuildReportSummary(bool failed, string outputPath)
        {
            Failed = failed;
            OutputPath = outputPath;
        }
    }

    public static BuildReportSummary Build(string outputPath)
    {
        NamedBuildTarget namedTarget = NamedBuildTarget.FromBuildTargetGroup(
            BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));

        string originalDefines = PlayerSettings.GetScriptingDefineSymbols(namedTarget);
        try
        {
            PlayerSettings.SetScriptingDefineSymbols(namedTarget, WithBenchDefine(originalDefines));

            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
                throw new InvalidOperationException(
                    "No enabled scenes in Build Settings; the bench player would have nothing to run.");

            UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(
                new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = EditorUserBuildSettings.activeBuildTarget,
                    options = BuildOptions.None
                });

            bool failed = report.summary.result !=
                          UnityEditor.Build.Reporting.BuildResult.Succeeded;

            Debug.Log(failed
                ? $"Distributed Sari Bench build FAILED ({report.summary.totalErrors} error(s))."
                : $"Distributed Sari Bench player written to {outputPath}. Launch each instance with " +
                  "SARI_BENCH_COORDINATOR=ws://<coordinator-host>:9000/sandbox set in its environment.");

            return new BuildReportSummary(failed, outputPath);
        }
        finally
        {
            // Always restore, so a failed bench build never leaves the define behind and silently
            // turns every subsequent normal build into a fleet member.
            PlayerSettings.SetScriptingDefineSymbols(namedTarget, originalDefines);
        }
    }

    private static string WithBenchDefine(string defines)
    {
        List<string> symbols = (defines ?? string.Empty)
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(symbol => symbol.Trim())
            .Where(symbol => symbol.Length > 0)
            .ToList();

        if (!symbols.Contains(Define)) symbols.Add(Define);
        return string.Join(";", symbols);
    }

    private static string DefaultExtension()
    {
        switch (EditorUserBuildSettings.activeBuildTarget)
        {
            case BuildTarget.StandaloneWindows:
            case BuildTarget.StandaloneWindows64:
                return ".exe";
            case BuildTarget.StandaloneOSX:
                return ".app";
            default:
                return string.Empty;
        }
    }

    private static string ReadArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];

        return null;
    }
}
