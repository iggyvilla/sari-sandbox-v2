using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Opt-in, command-line performance probe used to compare standalone player builds.
///
/// This has no effect during normal runs. Pass -sariPerformanceProbe to enable it and
/// -sariPerformanceReport &lt;path&gt; to select the JSON output file.
/// </summary>
public sealed class SariPerformanceProbe : MonoBehaviour
{
    private const string ProbeFlag = "-sariPerformanceProbe";
    private const string ReportPathFlag = "-sariPerformanceReport";
    private const string LabelFlag = "-sariPerformanceLabel";
    private const string WarmupSecondsFlag = "-sariPerformanceWarmup";
    private const string SampleSecondsFlag = "-sariPerformanceDuration";
    private const string VariantFlag = "-sariPerformanceVariant";
    private const string ScreenshotPathFlag = "-sariPerformanceScreenshot";
    private const string TargetScene = "Dev Scene";

    private static bool _installed;

    [Serializable]
    private sealed class MetricSummary
    {
        public int samples;
        public double average;
        public double minimum;
        public double p50;
        public double p90;
        public double p95;
        public double p99;
        public double maximum;
    }

    [Serializable]
    private sealed class SceneSnapshot
    {
        public int gameObjects;
        public int renderers;
        public int colliders;
        public int rigidbodies;
        public int behaviours;
        public int lights;
        public int shadowCastingLights;
        public int cameras;
        public int batchInstancers;
        public int gpuInstances;
        public int indirectDrawCommands;
        public int activeItemBBoxes;
        public int virtualItemBBoxes;
        public int pooledItemBBoxes;
        public string[] rendererDetails;
        public string[] shaderDetails;
        public string[] batchPositionDetails;
        public string[] cameraDetails;
        public string[] lightDetails;
    }

    [Serializable]
    private sealed class ProbeReport
    {
        public string label;
        public string timestampUtc;
        public string unityVersion;
        public string scene;
        public string platform;
        public string operatingSystem;
        public string processor;
        public string graphicsDevice;
        public string graphicsApi;
        public string renderPipeline;
        public string qualityLevel;
        public string occlusionMode;
        public string screen;
        public bool developmentBuild;
        public int targetFrameRate;
        public int vSyncCount;
        public float warmupSeconds;
        public float sampleSeconds;
        public int sampledFrames;
        public double averageFps;
        public double onePercentLowFps;
        public double pointOnePercentLowFps;
        public MetricSummary frameMs;
        public MetricSummary cpuFrameMs;
        public MetricSummary gpuFrameMs;
        public MetricSummary mainThreadMs;
        public MetricSummary renderThreadMs;
        public MetricSummary gcAllocatedBytes;
        public MetricSummary batches;
        public MetricSummary drawCalls;
        public MetricSummary setPassCalls;
        public MetricSummary triangles;
        public MetricSummary vertices;
        public MetricSummary frustumCullingMs;
        public MetricSummary frustumCullingDispatches;
        public MetricSummary occlusionCullingMs;
        public MetricSummary occlusionCullingDispatches;
        public MetricSummary behaviourUpdateMs;
        public MetricSummary physicsSimulateMs;
        public SceneSnapshot sceneSnapshot;
        public string[] availableProfilerCounters;
        public string notes;
    }

    private sealed class CounterSampler : IDisposable
    {
        private readonly ProfilerRecorder _recorder;
        private readonly double _scale;

        public bool Valid => _recorder.Valid;

        public CounterSampler(ProfilerCategory category, string name, double scale)
        {
            _scale = scale;
            try
            {
                _recorder = ProfilerRecorder.StartNew(
                    category,
                    name,
                    1,
                    ProfilerRecorderOptions.StartImmediately);
            }
            catch (Exception)
            {
                _recorder = default;
            }
        }

        public void Sample(List<double> destination)
        {
            if (_recorder.Valid && _recorder.Count > 0)
                destination.Add(_recorder.LastValue * _scale);
        }

        public void Dispose()
        {
            if (_recorder.Valid)
                _recorder.Dispose();
        }
    }

    private sealed class MarkerSampler : IDisposable
    {
        private readonly UnityEngine.Profiling.Recorder _recorder;

        public MarkerSampler(string markerName)
        {
            try
            {
                _recorder = UnityEngine.Profiling.Recorder.Get(markerName);
                if (_recorder != null)
                    _recorder.enabled = true;
            }
            catch (Exception)
            {
                _recorder = null;
            }
        }

        public void SampleTime(List<double> destination)
        {
            if (_recorder != null)
                destination.Add(_recorder.elapsedNanoseconds * 0.000001d);
        }

        public void SampleCount(List<double> destination)
        {
            if (_recorder != null)
                destination.Add(_recorder.sampleBlockCount);
        }

        public void Dispose()
        {
            if (_recorder != null)
                _recorder.enabled = false;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallFromCommandLine()
    {
        if (_installed || !HasCommandLineFlag(ProbeFlag))
            return;

        _installed = true;
        GameObject probeObject = new GameObject(nameof(SariPerformanceProbe));
        DontDestroyOnLoad(probeObject);
        probeObject.AddComponent<SariPerformanceProbe>();
    }

    private IEnumerator Start()
    {
        if (SceneManager.GetActiveScene().name != TargetScene)
        {
            AsyncOperation load = SceneManager.LoadSceneAsync(TargetScene, LoadSceneMode.Single);
            while (load != null && !load.isDone)
                yield return null;
        }

        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        float readinessDeadline = Time.realtimeSinceStartup + 90f;
        int stableFrames = 0;
        int lastBatchCount = -1;
        int lastInstanceCount = -1;

        while (Time.realtimeSinceStartup < readinessDeadline)
        {
            GetBatchStats(out int batchCount, out int instanceCount, out _);
            bool storeReady = DataHandler.Instance != null && DataHandler.Instance.StoreLoaded;
            bool stable = storeReady &&
                          batchCount > 0 &&
                          batchCount == lastBatchCount &&
                          instanceCount == lastInstanceCount;

            stableFrames = stable ? stableFrames + 1 : 0;
            lastBatchCount = batchCount;
            lastInstanceCount = instanceCount;

            if (stableFrames >= 120)
                break;

            yield return null;
        }

        ApplyProbeVariant(GetArgument(VariantFlag));

        float warmupSeconds = GetFloatArgument(WarmupSecondsFlag, 8f, 0f, 120f);
        float sampleSeconds = GetFloatArgument(SampleSecondsFlag, 20f, 2f, 300f);
        float warmupEnd = Time.realtimeSinceStartup + warmupSeconds;
        while (Time.realtimeSinceStartup < warmupEnd)
            yield return null;

        string screenshotPath = GetArgument(ScreenshotPathFlag);
        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            string screenshotDirectory = Path.GetDirectoryName(screenshotPath);
            if (!string.IsNullOrEmpty(screenshotDirectory))
                Directory.CreateDirectory(screenshotDirectory);
            ScreenCapture.CaptureScreenshot(screenshotPath);
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
        }

        ProbeReport report = CreateReportHeader(warmupSeconds, sampleSeconds);
        report.sceneSnapshot = CaptureSceneSnapshot();
        report.availableProfilerCounters = GetRelevantProfilerCounters();

        List<double> frameMs = new();
        List<double> cpuFrameMs = new();
        List<double> gpuFrameMs = new();
        List<double> mainThreadMs = new();
        List<double> renderThreadMs = new();
        List<double> gcAllocatedBytes = new();
        List<double> batches = new();
        List<double> drawCalls = new();
        List<double> setPassCalls = new();
        List<double> triangles = new();
        List<double> vertices = new();
        List<double> frustumCullingMs = new();
        List<double> frustumCullingDispatches = new();
        List<double> occlusionCullingMs = new();
        List<double> occlusionCullingDispatches = new();
        List<double> behaviourUpdateMs = new();
        List<double> physicsSimulateMs = new();

        using CounterSampler mainThread = new(
            ProfilerCategory.Render,
            "CPU Main Thread Frame Time",
            0.000001d);
        using CounterSampler renderThread = new(
            ProfilerCategory.Render,
            "CPU Render Thread Frame Time",
            0.000001d);
        using CounterSampler gcAllocated = new(ProfilerCategory.Memory, "GC Allocated In Frame", 1d);
        using CounterSampler batchCounter = new(ProfilerCategory.Render, "Batches Count", 1d);
        using CounterSampler drawCallCount = new(ProfilerCategory.Render, "Draw Calls Count", 1d);
        using CounterSampler setPassCount = new(ProfilerCategory.Render, "SetPass Calls Count", 1d);
        using CounterSampler triangleCount = new(ProfilerCategory.Render, "Triangles Count", 1d);
        using CounterSampler vertexCount = new(ProfilerCategory.Render, "Vertices Count", 1d);
        using MarkerSampler frustumCulling = new("Frustum Culling");
        using MarkerSampler occlusionCulling = new("Occlusion Culling");
        using MarkerSampler behaviourUpdate = new("BehaviourUpdate");
        using MarkerSampler physicsSimulate = new("Physics.Simulate");

        FrameTiming[] frameTimings = new FrameTiming[1];
        float sampleEnd = Time.realtimeSinceStartup + sampleSeconds;
        while (Time.realtimeSinceStartup < sampleEnd)
        {
            FrameTimingManager.CaptureFrameTimings();
            yield return null;

            double deltaMs = Time.unscaledDeltaTime * 1000d;
            if (deltaMs > 0d && deltaMs < 1000d)
                frameMs.Add(deltaMs);

            uint timingCount = FrameTimingManager.GetLatestTimings(1, frameTimings);
            if (timingCount > 0)
            {
                if (frameTimings[0].cpuFrameTime > 0d)
                    cpuFrameMs.Add(frameTimings[0].cpuFrameTime);
                if (frameTimings[0].gpuFrameTime > 0d)
                    gpuFrameMs.Add(frameTimings[0].gpuFrameTime);
            }

            mainThread.Sample(mainThreadMs);
            renderThread.Sample(renderThreadMs);
            gcAllocated.Sample(gcAllocatedBytes);
            batchCounter.Sample(batches);
            drawCallCount.Sample(drawCalls);
            setPassCount.Sample(setPassCalls);
            triangleCount.Sample(triangles);
            vertexCount.Sample(vertices);
            frustumCulling.SampleTime(frustumCullingMs);
            frustumCulling.SampleCount(frustumCullingDispatches);
            occlusionCulling.SampleTime(occlusionCullingMs);
            occlusionCulling.SampleCount(occlusionCullingDispatches);
            behaviourUpdate.SampleTime(behaviourUpdateMs);
            physicsSimulate.SampleTime(physicsSimulateMs);
        }

        report.sampledFrames = frameMs.Count;
        report.frameMs = Summarize(frameMs);
        report.cpuFrameMs = Summarize(cpuFrameMs);
        report.gpuFrameMs = Summarize(gpuFrameMs);
        report.mainThreadMs = Summarize(mainThreadMs);
        report.renderThreadMs = Summarize(renderThreadMs);
        report.gcAllocatedBytes = Summarize(gcAllocatedBytes);
        report.batches = Summarize(batches);
        report.drawCalls = Summarize(drawCalls);
        report.setPassCalls = Summarize(setPassCalls);
        report.triangles = Summarize(triangles);
        report.vertices = Summarize(vertices);
        report.frustumCullingMs = Summarize(frustumCullingMs);
        report.frustumCullingDispatches = Summarize(frustumCullingDispatches);
        report.occlusionCullingMs = Summarize(occlusionCullingMs);
        report.occlusionCullingDispatches = Summarize(occlusionCullingDispatches);
        report.behaviourUpdateMs = Summarize(behaviourUpdateMs);
        report.physicsSimulateMs = Summarize(physicsSimulateMs);
        report.averageFps = report.frameMs.average > 0d ? 1000d / report.frameMs.average : 0d;
        report.onePercentLowFps = FpsFromWorstPercent(frameMs, 0.01d);
        report.pointOnePercentLowFps = FpsFromWorstPercent(frameMs, 0.001d);
        report.notes =
            "Frame percentiles are frame-time percentiles (lower is better). " +
            "1% low FPS is derived from the average of the slowest 1% of sampled frames.";

        string json = JsonUtility.ToJson(report, true);
        string reportPath = GetArgument(ReportPathFlag);
        if (string.IsNullOrWhiteSpace(reportPath))
            reportPath = Path.Combine(Application.persistentDataPath, "sari-performance-report.json");

        try
        {
            string directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(reportPath, json);
            Debug.Log($"SARI_PERFORMANCE_REPORT={reportPath}\n{json}");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            Quit(2);
            yield break;
        }

        Quit(0);
    }

    private static ProbeReport CreateReportHeader(float warmupSeconds, float sampleSeconds)
    {
        RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
        return new ProbeReport
        {
            label = GetArgument(LabelFlag) ?? "unlabelled",
            timestampUtc = DateTime.UtcNow.ToString("O"),
            unityVersion = Application.unityVersion,
            scene = SceneManager.GetActiveScene().name,
            platform = Application.platform.ToString(),
            operatingSystem = SystemInfo.operatingSystem,
            processor = $"{SystemInfo.processorType} ({SystemInfo.processorCount} logical cores)",
            graphicsDevice = $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB)",
            graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            renderPipeline = pipeline != null ? pipeline.name : "Built-in",
            qualityLevel = QualitySettings.names[QualitySettings.GetQualityLevel()],
            occlusionMode = GPUInstanceTracker.Instance != null
                ? GPUInstanceTracker.Instance.OcclusionMode.ToString()
                : "Unavailable",
            screen = $"{Screen.width}x{Screen.height} @{Screen.currentResolution.refreshRateRatio}",
            developmentBuild = Debug.isDebugBuild,
            targetFrameRate = Application.targetFrameRate,
            vSyncCount = QualitySettings.vSyncCount,
            warmupSeconds = warmupSeconds,
            sampleSeconds = sampleSeconds
        };
    }

    private static SceneSnapshot CaptureSceneSnapshot()
    {
        GetBatchStats(out int batchCount, out int instanceCount, out int drawCommands);
        NearbyItemBBoxManager bboxManager = NearbyItemBBoxManager.TryGetInstance();
        Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        Renderer[] renderers =
            FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        return new SceneSnapshot
        {
            gameObjects = FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length,
            renderers = renderers.Length,
            colliders = FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length,
            rigidbodies = FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length,
            behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length,
            lights = lights.Length,
            shadowCastingLights = lights.Count(light => light.shadows != LightShadows.None),
            cameras = FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length,
            batchInstancers = batchCount,
            gpuInstances = instanceCount,
            indirectDrawCommands = drawCommands,
            activeItemBBoxes = bboxManager != null ? bboxManager.ActiveRealBBoxes : 0,
            virtualItemBBoxes = bboxManager != null ? bboxManager.TotalVirtualBBoxes : 0,
            pooledItemBBoxes = bboxManager != null ? bboxManager.PooledBBoxes : 0,
            rendererDetails = renderers
                .GroupBy(renderer =>
                    $"{renderer.GetType().Name}: enabled={renderer.enabled}, " +
                    $"shadows={renderer.shadowCastingMode}, receiveShadows={renderer.receiveShadows}")
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Count()} x {group.Key}")
                .ToArray(),
            shaderDetails = renderers
                .SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null && material.shader != null)
                .GroupBy(material => material.shader.name)
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Count()} material slots x {group.Key}")
                .ToArray(),
            batchPositionDetails =
                FindObjectsByType<BatchInstancer>(
                        FindObjectsInactive.Exclude,
                        FindObjectsSortMode.None)
                    .Select(instancer => instancer.GetPositionDiagnosticSummary())
                    .OrderBy(detail => detail, StringComparer.Ordinal)
                    .ToArray(),
            cameraDetails = FindObjectsByType<Camera>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None)
                .Select(camera =>
                    $"{camera.name}: enabled={camera.enabled}, depth={camera.depth}, " +
                    $"type={camera.cameraType}, target={camera.targetTexture?.name ?? "screen"}, " +
                    $"mask=0x{camera.cullingMask:X8}")
                .ToArray(),
            lightDetails = lights
                .Select(light =>
                    $"{light.name}: type={light.type}, shadows={light.shadows}, " +
                    $"range={light.range:F2}, mask=0x{light.cullingMask:X8}")
                .ToArray()
        };
    }

    private static void ApplyProbeVariant(string variant)
    {
        if (string.IsNullOrWhiteSpace(variant) ||
            variant.Equals("current", StringComparison.OrdinalIgnoreCase))
            return;

        string[] tokens = variant.Split(
            new[] { ',', '+', ';' },
            StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i].Trim().ToLowerInvariant();
            switch (token)
            {
                case "no-ssao":
                    SetRendererFeatureActive("ScreenSpaceAmbientOcclusion", false);
                    break;
                case "no-decals":
                    SetRendererFeatureActive("DecalRendererFeature", false);
                    break;
                case "no-outline":
                    SetRendererFeatureActive("OutlineFxFeature", false);
                    break;
                case "no-price-tags":
                    foreach (BakedPriceTag priceTag in
                             FindObjectsByType<BakedPriceTag>(
                                 FindObjectsInactive.Exclude,
                                 FindObjectsSortMode.None))
                    {
                        Renderer renderer = priceTag.GetComponent<Renderer>();
                        if (renderer != null)
                            renderer.enabled = false;
                    }
                    break;
                case "no-products":
                    foreach (BatchInstancer instancer in
                             FindObjectsByType<BatchInstancer>(
                                 FindObjectsInactive.Exclude,
                                 FindObjectsSortMode.None))
                    {
                        instancer.enabled = false;
                    }
                    break;
                case "no-shadows":
                    foreach (Light light in
                             FindObjectsByType<Light>(
                                 FindObjectsInactive.Exclude,
                                 FindObjectsSortMode.None))
                    {
                        light.shadows = LightShadows.None;
                    }
                    break;
                case "disabled":
                case "occlusion-disabled":
                    SetOcclusionMode(OcclusionCullingMode.Disabled);
                    break;
                case "conservative":
                case "occlusion-conservative":
                    SetOcclusionMode(OcclusionCullingMode.Conservative);
                    break;
                case "balanced":
                case "occlusion-balanced":
                    SetOcclusionMode(OcclusionCullingMode.Balanced);
                    break;
                case "aggressive":
                case "occlusion-aggressive":
                    SetOcclusionMode(OcclusionCullingMode.Aggressive);
                    break;
                default:
                    Debug.LogWarning($"Unknown performance probe variant '{tokens[i]}'.");
                    break;
            }
        }
    }

    private static void SetOcclusionMode(OcclusionCullingMode mode)
    {
        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        if (tracker == null)
        {
            Debug.LogWarning($"Cannot set occlusion mode to {mode}: no GPU instance tracker.");
            return;
        }

        tracker.OcclusionMode = mode;
        Debug.Log($"Performance probe set GPU product occlusion mode to {mode}.");
    }

    private static void SetRendererFeatureActive(string featureName, bool active)
    {
        UniversalRenderPipelineAsset pipeline = UniversalRenderPipeline.asset;
        ScriptableRenderer renderer = pipeline != null ? pipeline.GetRenderer(0) : null;
        if (renderer == null)
        {
            Debug.LogWarning(
                $"Cannot toggle renderer feature '{featureName}': no active URP renderer.");
            return;
        }

        System.Reflection.PropertyInfo featuresProperty =
            typeof(ScriptableRenderer).GetProperty(
                "rendererFeatures",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
        IEnumerable<ScriptableRendererFeature> features =
            featuresProperty?.GetValue(renderer) as IEnumerable<ScriptableRendererFeature>;
        ScriptableRendererFeature feature = features?
            .FirstOrDefault(candidate => candidate != null && candidate.name == featureName);
        if (feature == null)
        {
            Debug.LogWarning($"Renderer feature '{featureName}' was not found.");
            return;
        }

        feature.SetActive(active);
        Debug.Log($"Performance probe set renderer feature '{featureName}' active={active}.");
    }

    private static void GetBatchStats(out int batchCount, out int instanceCount, out int drawCommands)
    {
        BatchInstancer[] instancers =
            FindObjectsByType<BatchInstancer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        batchCount = instancers.Length;
        instanceCount = 0;
        drawCommands = 0;

        for (int i = 0; i < instancers.Length; i++)
        {
            instanceCount += instancers[i].InstanceCount;
            drawCommands += instancers[i].IndirectDrawCommandCount;
        }
    }

    private static string[] GetRelevantProfilerCounters()
    {
        try
        {
            List<ProfilerRecorderHandle> handles = new();
            ProfilerRecorderHandle.GetAvailable(handles);
            List<string> descriptions = new();

            foreach (ProfilerRecorderHandle handle in handles)
            {
                ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                string name = description.Name;
                if (name.IndexOf("thread", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("frame", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("batch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("draw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("triangle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("vert", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("GC", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    descriptions.Add($"{description.Category.Name}/{name}/{description.UnitType}");
                }
            }

            descriptions.Sort(StringComparer.Ordinal);
            return descriptions.ToArray();
        }
        catch (Exception exception)
        {
            return new[] { "Profiler counter enumeration failed: " + exception.Message };
        }
    }

    private static MetricSummary Summarize(List<double> source)
    {
        if (source == null || source.Count == 0)
            return new MetricSummary();

        double[] sorted = source.Where(value => !double.IsNaN(value) && !double.IsInfinity(value)).ToArray();
        if (sorted.Length == 0)
            return new MetricSummary();

        Array.Sort(sorted);
        return new MetricSummary
        {
            samples = sorted.Length,
            average = sorted.Average(),
            minimum = sorted[0],
            p50 = Percentile(sorted, 0.50d),
            p90 = Percentile(sorted, 0.90d),
            p95 = Percentile(sorted, 0.95d),
            p99 = Percentile(sorted, 0.99d),
            maximum = sorted[sorted.Length - 1]
        };
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0d;
        if (sorted.Length == 1)
            return sorted[0];

        double position = (sorted.Length - 1) * percentile;
        int lower = Mathf.FloorToInt((float)position);
        int upper = Mathf.CeilToInt((float)position);
        double fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private static double FpsFromWorstPercent(List<double> frameTimes, double fraction)
    {
        if (frameTimes == null || frameTimes.Count == 0)
            return 0d;

        double[] descending = frameTimes.OrderByDescending(value => value).ToArray();
        int count = Math.Max(1, (int)Math.Ceiling(descending.Length * fraction));
        double meanWorstFrameMs = descending.Take(count).Average();
        return meanWorstFrameMs > 0d ? 1000d / meanWorstFrameMs : 0d;
    }

    private static bool HasCommandLineFlag(string flag)
    {
        return Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));
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

    private static float GetFloatArgument(string flag, float fallback, float minimum, float maximum)
    {
        string value = GetArgument(flag);
        return float.TryParse(value, out float parsed)
            ? Mathf.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    private static void Quit(int exitCode)
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.ExitPlaymode();
        UnityEditor.EditorApplication.Exit(exitCode);
#else
        Application.Quit(exitCode);
#endif
    }
}
