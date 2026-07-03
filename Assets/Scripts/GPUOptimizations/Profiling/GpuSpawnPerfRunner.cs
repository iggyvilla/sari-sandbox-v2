using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class GpuSpawnPerfRunner : MonoBehaviour
{
    private enum ScenarioKind
    {
        Shelf,
        Synthetic
    }

    private enum SyntheticVisibility
    {
        CameraVisible,
        CameraPartial,
        CameraOffscreen
    }

    private struct ScenarioConfig
    {
        public string name;
        public ScenarioKind kind;
        public GpuSpawnMaterialMode materialMode;
        public bool suppressBBoxTriggers;
        public bool suppressPriceTags;
        public bool suppressExpirationDecals;
        public bool forcePriceTagsOn;
        public bool? combineRowMeshes;
        public bool? enableShelfItemPhysics;
        public GpuSyntheticMeshKind syntheticMeshKind;
        public GpuSyntheticProductMode syntheticProductMode;
        public SyntheticVisibility syntheticVisibility;
        public int syntheticInstanceCount;
        public float syntheticSpacing;
    }

    private struct ShelfState
    {
        public ShelfBuilder shelf;
        public bool spawnItems;
        public bool spawnPriceTags;
    }

    private struct SceneState
    {
        public bool hasDataHandler;
        public bool combineRowMeshes;
        public bool enableShelfItemPhysics;
        public ShelfState[] shelves;
    }

    private sealed class NamedRecorder : IDisposable
    {
        private const int RecorderCapacity = 4096;

        public readonly string columnName;
        public readonly string displayName;
        public readonly bool durationNanoseconds;
        private ProfilerRecorder _recorder;

        public NamedRecorder(
            string columnName,
            string displayName,
            ProfilerCategory category,
            string statName,
            bool durationNanoseconds,
            ProfilerRecorderOptions options)
        {
            this.columnName = columnName;
            this.displayName = displayName;
            this.durationNanoseconds = durationNanoseconds;

            try
            {
                _recorder = ProfilerRecorder.StartNew(category, statName, RecorderCapacity, options);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{nameof(GpuSpawnPerfRunner)}: could not start profiler recorder '{statName}': {ex.Message}");
            }
        }

        public NamedRecorder(
            string columnName,
            string displayName,
            ProfilerMarker marker,
            bool durationNanoseconds,
            ProfilerRecorderOptions options)
        {
            this.columnName = columnName;
            this.displayName = displayName;
            this.durationNanoseconds = durationNanoseconds;

            try
            {
                _recorder = ProfilerRecorder.StartNew(marker, RecorderCapacity, options);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{nameof(GpuSpawnPerfRunner)}: could not start profiler recorder '{displayName}': {ex.Message}");
            }
        }

        public bool Valid => _recorder.Valid;
        public long LastValue => _recorder.Valid ? _recorder.LastValue : 0L;
        public long MaxValue
        {
            get
            {
                if (!_recorder.Valid)
                    return 0L;

                ProfilerRecorderSample[] samples = _recorder.ToArray();
                long max = 0L;
                for (int i = 0; i < samples.Length; i++)
                {
                    if (samples[i].Value > max)
                        max = samples[i].Value;
                }
                return max;
            }
        }

        public void Dispose()
        {
            if (_recorder.Valid)
                _recorder.Dispose();
        }
    }

    private sealed class RecorderSet : IDisposable
    {
        private readonly List<NamedRecorder> _recorders = new();

        public IReadOnlyList<NamedRecorder> Recorders => _recorders;

        public void AddCounter(string columnName, string displayName, ProfilerCategory category, string statName)
        {
            Add(new NamedRecorder(
                columnName,
                displayName,
                category,
                statName,
                false,
                ProfilerRecorderOptions.Default));
        }

        public void AddMarker(string columnName, string displayName, string markerName)
        {
            Add(new NamedRecorder(
                columnName,
                displayName,
                new ProfilerMarker(markerName),
                true,
                ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame));
        }

        private void Add(NamedRecorder recorder)
        {
            if (recorder.Valid)
                _recorders.Add(recorder);
            else
                recorder.Dispose();
        }

        public Dictionary<string, long> Snapshot()
        {
            Dictionary<string, long> values = new();
            for (int i = 0; i < _recorders.Count; i++)
            {
                NamedRecorder recorder = _recorders[i];
                values[recorder.columnName] = recorder.durationNanoseconds
                    ? recorder.MaxValue
                    : recorder.LastValue;
            }
            return values;
        }

        public void Dispose()
        {
            for (int i = 0; i < _recorders.Count; i++)
                _recorders[i].Dispose();
            _recorders.Clear();
        }
    }

    private sealed class ScenarioResult
    {
        public string scenario;
        public int sampleFrames;
        public float spawnMs;
        public float frameMedianMs;
        public float frameP95Ms;
        public float frameAverageMs;
        public float frameMinMs;
        public float frameMaxMs;
        public float cpuMedianMs;
        public float cpuP95Ms;
        public float gpuMedianMs;
        public float gpuP95Ms;
        public int bboxBefore;
        public int bboxAfter;
        public int priceTagsAfter;
        public GPUInstanceAggregateStats gpuStats;
        public Dictionary<string, long> recorderValues = new();
    }

    [Header("Run")]
    public bool runOnStart;
    public KeyCode runKey = KeyCode.F9;
    public bool includeShelfScenarios = true;
    public bool includeSyntheticScenarios = true;
    public bool restoreLiveStoreAfterRun = true;
    public bool clearShelfItemDataBeforeScenario = true;

    [Header("Sampling")]
    public int warmupFrames = 30;
    public int sampleFrames = 180;
    public bool captureVisibleCountsAtEnd = true;

    [Header("Synthetic")]
    public Camera measurementCamera;
    public Material syntheticMaterial;
    public GameObject[] representativeProductPrefabs;
    public string[] representativeProductIds =
    {
        "COCACOLA_REGULAR_500ML",
        "CENTURY_TUNA_FLAKES_IN_OIL_155G",
        "NISSIN_CUP_NOODLES_SPICY_SEAFOOD_45G",
        "FIBISCO_CHOCOMALLOWS_100G",
        "JACK_AND_JILL_PIATTOS_SOUR_CREAM_FLAVORED_POTATO_40G"
    };
    public int[] syntheticInstanceCounts = { 500, 1000, 2500, 5000, 10000 };
    public int syntheticGeometryInstanceCount = 2500;
    public int syntheticDiversityInstanceCount = 2500;
    public int visibleFractionInstanceCount = 2500;
    public float syntheticSpacing = 0.1f;

    private readonly FrameTiming[] _frameTimings = new FrameTiming[1];
    private bool _isRunning;
    private GpuSyntheticIndirectSpawner _syntheticSpawner;

    private static readonly ProfilerMarker ScenarioSetupMarker = new("Sari.GpuSpawnPerf.ScenarioSetup");
    private static readonly ProfilerMarker SamplingMarker = new("Sari.GpuSpawnPerf.Sampling");

    private void Start()
    {
        if (runOnStart)
            StartCoroutine(RunAllScenarios());
    }

    private void Update()
    {
        if (runKey != KeyCode.None && Input.GetKeyDown(runKey) && !_isRunning)
            StartCoroutine(RunAllScenarios());
    }

    private void OnDisable()
    {
        if (_isRunning)
        {
            GpuSpawnPerfSettings.ResetOverrides();
            _isRunning = false;
        }
    }

    [ContextMenu("Run GPU Spawn Perf Scenarios")]
    public void RunAllScenariosFromContextMenu()
    {
        if (!_isRunning)
            StartCoroutine(RunAllScenarios());
    }

    public IEnumerator RunAllScenarios()
    {
        if (_isRunning)
            yield break;

        _isRunning = true;
        SceneState originalState = CaptureSceneState();
        List<ScenarioResult> results = new();

        Debug.Log($"{nameof(GpuSpawnPerfRunner)}: starting GPU spawn profiling scenarios.");

        foreach (ScenarioConfig scenario in BuildScenarioList())
        {
            yield return RunScenario(scenario, originalState, results);
        }

        GpuSpawnPerfSettings.ResetOverrides();
        yield return CleanupScenarioObjects();
        RestoreSceneState(originalState);

        if (restoreLiveStoreAfterRun)
        {
            GpuSpawnPerfSettings.ResetOverrides();
            if (clearShelfItemDataBeforeScenario)
                ClearShelfItemData();
            SpawnShelves(originalState);
        }

        string path = WriteCsv(results);
        Debug.Log($"{nameof(GpuSpawnPerfRunner)}: complete. CSV written to {path}");
        _isRunning = false;
    }

    private IEnumerator RunScenario(ScenarioConfig scenario, SceneState originalState, List<ScenarioResult> results)
    {
        yield return CleanupScenarioObjects();

        using (ScenarioSetupMarker.Auto())
        {
            RestoreSceneState(originalState);
            ApplyScenarioSettings(scenario, originalState);
            if (clearShelfItemDataBeforeScenario)
                ClearShelfItemData();
        }

        int bboxBefore = CountLiveBBoxes();
        float spawnMs;

        using RecorderSet recorders = CreateRecorderSet();
        Stopwatch spawnWatch = Stopwatch.StartNew();
        if (scenario.kind == ScenarioKind.Shelf)
            SpawnShelves(originalState);
        else
            SpawnSyntheticScenario(scenario);
        spawnWatch.Stop();
        spawnMs = (float)spawnWatch.Elapsed.TotalMilliseconds;

        for (int i = 0; i < Mathf.Max(0, warmupFrames); i++)
        {
            FrameTimingManager.CaptureFrameTimings();
            yield return null;
        }

        List<float> frameMs = new();
        List<float> cpuMs = new();
        List<float> gpuMs = new();

        int frames = Mathf.Max(1, sampleFrames);
        for (int i = 0; i < frames; i++)
        {
            using (SamplingMarker.Auto())
            {
                FrameTimingManager.CaptureFrameTimings();
            }

            yield return null;

            using (SamplingMarker.Auto())
            {
                frameMs.Add(Time.unscaledDeltaTime * 1000f);
                uint timingCount = FrameTimingManager.GetLatestTimings(1, _frameTimings);
                if (timingCount > 0)
                {
                    if (_frameTimings[0].cpuFrameTime > 0.0)
                        cpuMs.Add((float)_frameTimings[0].cpuFrameTime);
                    if (_frameTimings[0].gpuFrameTime > 0.0)
                        gpuMs.Add((float)_frameTimings[0].gpuFrameTime);
                }
            }
        }

        GPUInstanceAggregateStats stats = GPUInstanceTracker.Instance != null
            ? GPUInstanceTracker.Instance.GetAggregateStats(captureVisibleCountsAtEnd)
            : default;

        results.Add(new ScenarioResult
        {
            scenario = scenario.name,
            sampleFrames = Mathf.Max(1, sampleFrames),
            spawnMs = spawnMs,
            frameMedianMs = Median(frameMs),
            frameP95Ms = Percentile(frameMs, 0.95f),
            frameAverageMs = Average(frameMs),
            frameMinMs = Min(frameMs),
            frameMaxMs = Max(frameMs),
            cpuMedianMs = Median(cpuMs),
            cpuP95Ms = Percentile(cpuMs, 0.95f),
            gpuMedianMs = Median(gpuMs),
            gpuP95Ms = Percentile(gpuMs, 0.95f),
            bboxBefore = bboxBefore,
            bboxAfter = CountLiveBBoxes(),
            priceTagsAfter = CountLivePriceTags(),
            gpuStats = stats,
            recorderValues = recorders.Snapshot()
        });

        Debug.Log($"{nameof(GpuSpawnPerfRunner)}: scenario '{scenario.name}' captured.");
    }

    private List<ScenarioConfig> BuildScenarioList()
    {
        List<ScenarioConfig> scenarios = new();

        if (includeShelfScenarios)
        {
            scenarios.Add(new ScenarioConfig
            {
                name = "LiveStoreBaseline",
                kind = ScenarioKind.Shelf,
                materialMode = GpuSpawnMaterialMode.Original
            });
            scenarios.Add(new ScenarioConfig
            {
                name = "RenderOnlyOriginal",
                kind = ScenarioKind.Shelf,
                materialMode = GpuSpawnMaterialMode.Original,
                suppressBBoxTriggers = true,
                suppressPriceTags = true,
                suppressExpirationDecals = true,
                combineRowMeshes = false,
                enableShelfItemPhysics = false
            });
            scenarios.Add(new ScenarioConfig
            {
                name = "RenderOnlyTextureless",
                kind = ScenarioKind.Shelf,
                materialMode = GpuSpawnMaterialMode.TexturelessWhite,
                suppressBBoxTriggers = true,
                suppressPriceTags = true,
                suppressExpirationDecals = true,
                combineRowMeshes = false,
                enableShelfItemPhysics = false
            });
            scenarios.Add(new ScenarioConfig
            {
                name = "RenderOnlyFlatLit",
                kind = ScenarioKind.Shelf,
                materialMode = GpuSpawnMaterialMode.FlatLitSameShader,
                suppressBBoxTriggers = true,
                suppressPriceTags = true,
                suppressExpirationDecals = true,
                combineRowMeshes = false,
                enableShelfItemPhysics = false
            });
            scenarios.Add(new ScenarioConfig
            {
                name = "RowCombinedOff",
                kind = ScenarioKind.Shelf,
                materialMode = GpuSpawnMaterialMode.Original,
                suppressBBoxTriggers = true,
                suppressPriceTags = true,
                suppressExpirationDecals = true,
                combineRowMeshes = false,
                enableShelfItemPhysics = false
            });
            scenarios.Add(new ScenarioConfig
            {
                name = "RowCombinedOn",
                kind = ScenarioKind.Shelf,
                materialMode = GpuSpawnMaterialMode.Original,
                suppressBBoxTriggers = true,
                suppressPriceTags = true,
                suppressExpirationDecals = true,
                combineRowMeshes = true,
                enableShelfItemPhysics = false
            });
            scenarios.Add(new ScenarioConfig
            {
                name = "InteractionBBoxOnly",
                kind = ScenarioKind.Shelf,
                materialMode = GpuSpawnMaterialMode.Original,
                suppressBBoxTriggers = false,
                suppressPriceTags = true,
                suppressExpirationDecals = true,
                combineRowMeshes = false,
                enableShelfItemPhysics = false
            });
            scenarios.Add(new ScenarioConfig
            {
                name = "InteractionPriceTags",
                kind = ScenarioKind.Shelf,
                materialMode = GpuSpawnMaterialMode.Original,
                suppressBBoxTriggers = true,
                suppressPriceTags = false,
                suppressExpirationDecals = true,
                forcePriceTagsOn = true,
                combineRowMeshes = false,
                enableShelfItemPhysics = false
            });
            scenarios.Add(new ScenarioConfig
            {
                name = "InteractionPhysicsProxies",
                kind = ScenarioKind.Shelf,
                materialMode = GpuSpawnMaterialMode.Original,
                suppressBBoxTriggers = false,
                suppressPriceTags = true,
                suppressExpirationDecals = false,
                combineRowMeshes = false,
                enableShelfItemPhysics = true
            });
        }

        if (!includeSyntheticScenarios)
            return scenarios;

        for (int i = 0; i < syntheticInstanceCounts.Length; i++)
        {
            scenarios.Add(new ScenarioConfig
            {
                name = "SyntheticInstanceScale_" + syntheticInstanceCounts[i],
                kind = ScenarioKind.Synthetic,
                materialMode = GpuSpawnMaterialMode.Original,
                syntheticMeshKind = GpuSyntheticMeshKind.RealProduct,
                syntheticProductMode = GpuSyntheticProductMode.SingleProduct,
                syntheticVisibility = SyntheticVisibility.CameraVisible,
                syntheticInstanceCount = syntheticInstanceCounts[i],
                syntheticSpacing = syntheticSpacing,
                enableShelfItemPhysics = false
            });
        }

        scenarios.Add(new ScenarioConfig
        {
            name = "SyntheticGeometry_LowProxy",
            kind = ScenarioKind.Synthetic,
            materialMode = GpuSpawnMaterialMode.Original,
            syntheticMeshKind = GpuSyntheticMeshKind.LowProxy,
            syntheticProductMode = GpuSyntheticProductMode.SingleProduct,
            syntheticVisibility = SyntheticVisibility.CameraVisible,
            syntheticInstanceCount = syntheticGeometryInstanceCount,
            syntheticSpacing = syntheticSpacing,
            enableShelfItemPhysics = false
        });
        scenarios.Add(new ScenarioConfig
        {
            name = "SyntheticGeometry_RealProduct",
            kind = ScenarioKind.Synthetic,
            materialMode = GpuSpawnMaterialMode.Original,
            syntheticMeshKind = GpuSyntheticMeshKind.RealProduct,
            syntheticProductMode = GpuSyntheticProductMode.SingleProduct,
            syntheticVisibility = SyntheticVisibility.CameraVisible,
            syntheticInstanceCount = syntheticGeometryInstanceCount,
            syntheticSpacing = syntheticSpacing,
            enableShelfItemPhysics = false
        });
        scenarios.Add(new ScenarioConfig
        {
            name = "SyntheticGeometry_HeavyProxy",
            kind = ScenarioKind.Synthetic,
            materialMode = GpuSpawnMaterialMode.Original,
            syntheticMeshKind = GpuSyntheticMeshKind.HeavyProxy,
            syntheticProductMode = GpuSyntheticProductMode.SingleProduct,
            syntheticVisibility = SyntheticVisibility.CameraVisible,
            syntheticInstanceCount = syntheticGeometryInstanceCount,
            syntheticSpacing = syntheticSpacing,
            enableShelfItemPhysics = false
        });
        scenarios.Add(new ScenarioConfig
        {
            name = "SyntheticProductDiversity_Single",
            kind = ScenarioKind.Synthetic,
            materialMode = GpuSpawnMaterialMode.Original,
            syntheticMeshKind = GpuSyntheticMeshKind.RealProduct,
            syntheticProductMode = GpuSyntheticProductMode.SingleProduct,
            syntheticVisibility = SyntheticVisibility.CameraVisible,
            syntheticInstanceCount = syntheticDiversityInstanceCount,
            syntheticSpacing = syntheticSpacing,
            enableShelfItemPhysics = false
        });
        scenarios.Add(new ScenarioConfig
        {
            name = "SyntheticProductDiversity_RoundRobin",
            kind = ScenarioKind.Synthetic,
            materialMode = GpuSpawnMaterialMode.Original,
            syntheticMeshKind = GpuSyntheticMeshKind.RealProduct,
            syntheticProductMode = GpuSyntheticProductMode.RoundRobinProducts,
            syntheticVisibility = SyntheticVisibility.CameraVisible,
            syntheticInstanceCount = syntheticDiversityInstanceCount,
            syntheticSpacing = syntheticSpacing,
            enableShelfItemPhysics = false
        });
        scenarios.Add(new ScenarioConfig
        {
            name = "VisibleFraction_All",
            kind = ScenarioKind.Synthetic,
            materialMode = GpuSpawnMaterialMode.Original,
            syntheticMeshKind = GpuSyntheticMeshKind.RealProduct,
            syntheticProductMode = GpuSyntheticProductMode.SingleProduct,
            syntheticVisibility = SyntheticVisibility.CameraVisible,
            syntheticInstanceCount = visibleFractionInstanceCount,
            syntheticSpacing = syntheticSpacing,
            enableShelfItemPhysics = false
        });
        scenarios.Add(new ScenarioConfig
        {
            name = "VisibleFraction_Partial",
            kind = ScenarioKind.Synthetic,
            materialMode = GpuSpawnMaterialMode.Original,
            syntheticMeshKind = GpuSyntheticMeshKind.RealProduct,
            syntheticProductMode = GpuSyntheticProductMode.SingleProduct,
            syntheticVisibility = SyntheticVisibility.CameraPartial,
            syntheticInstanceCount = visibleFractionInstanceCount,
            syntheticSpacing = syntheticSpacing,
            enableShelfItemPhysics = false
        });
        scenarios.Add(new ScenarioConfig
        {
            name = "VisibleFraction_Offscreen",
            kind = ScenarioKind.Synthetic,
            materialMode = GpuSpawnMaterialMode.Original,
            syntheticMeshKind = GpuSyntheticMeshKind.RealProduct,
            syntheticProductMode = GpuSyntheticProductMode.SingleProduct,
            syntheticVisibility = SyntheticVisibility.CameraOffscreen,
            syntheticInstanceCount = visibleFractionInstanceCount,
            syntheticSpacing = syntheticSpacing,
            enableShelfItemPhysics = false
        });

        return scenarios;
    }

    private SceneState CaptureSceneState()
    {
        ShelfBuilder[] shelves = FindObjectsByType<ShelfBuilder>(FindObjectsSortMode.None);
        ShelfState[] shelfStates = new ShelfState[shelves.Length];
        for (int i = 0; i < shelves.Length; i++)
        {
            shelfStates[i] = new ShelfState
            {
                shelf = shelves[i],
                spawnItems = shelves[i].spawnItems,
                spawnPriceTags = shelves[i].spawnPriceTags
            };
        }

        DataHandler dataHandler = DataHandler.Instance;
        return new SceneState
        {
            hasDataHandler = dataHandler != null,
            combineRowMeshes = dataHandler != null && dataHandler.combineRowMeshes,
            enableShelfItemPhysics = dataHandler != null && dataHandler.enableShelfItemPhysics,
            shelves = shelfStates
        };
    }

    private void RestoreSceneState(SceneState state)
    {
        DataHandler dataHandler = DataHandler.Instance;
        if (state.hasDataHandler && dataHandler != null)
        {
            dataHandler.combineRowMeshes = state.combineRowMeshes;
            dataHandler.enableShelfItemPhysics = state.enableShelfItemPhysics;
        }

        if (state.shelves == null)
            return;

        for (int i = 0; i < state.shelves.Length; i++)
        {
            ShelfBuilder shelf = state.shelves[i].shelf;
            if (shelf == null)
                continue;

            shelf.spawnItems = state.shelves[i].spawnItems;
            shelf.spawnPriceTags = state.shelves[i].spawnPriceTags;
        }
    }

    private void ApplyScenarioSettings(ScenarioConfig scenario, SceneState originalState)
    {
        DataHandler dataHandler = DataHandler.Instance;
        if (dataHandler != null)
        {
            dataHandler.combineRowMeshes = scenario.combineRowMeshes ?? originalState.combineRowMeshes;
            dataHandler.enableShelfItemPhysics = scenario.enableShelfItemPhysics ?? originalState.enableShelfItemPhysics;
        }

        if (originalState.shelves != null)
        {
            for (int i = 0; i < originalState.shelves.Length; i++)
            {
                ShelfBuilder shelf = originalState.shelves[i].shelf;
                if (shelf == null)
                    continue;

                shelf.spawnItems = originalState.shelves[i].spawnItems;
                shelf.spawnPriceTags = !scenario.suppressPriceTags &&
                                       (scenario.forcePriceTagsOn || originalState.shelves[i].spawnPriceTags);
            }
        }

        GpuSpawnPerfSettings.ApplyOverrides(
            scenario.materialMode,
            scenario.suppressBBoxTriggers,
            scenario.suppressPriceTags,
            scenario.suppressExpirationDecals,
            scenario.kind == ScenarioKind.Synthetic,
            captureVisibleCountsAtEnd);
    }

    private void SpawnShelves(SceneState originalState)
    {
        if (originalState.shelves == null)
            return;

        for (int i = 0; i < originalState.shelves.Length; i++)
        {
            ShelfBuilder shelf = originalState.shelves[i].shelf;
            if (shelf != null && shelf.spawnItems)
                shelf.SpawnItemsOnAllShelves();
        }
    }

    private void SpawnSyntheticScenario(ScenarioConfig scenario)
    {
        if (_syntheticSpawner == null)
            _syntheticSpawner = gameObject.AddComponent<GpuSyntheticIndirectSpawner>();

        Camera camera = ResolveCamera();
        Vector3 right = camera != null ? Vector3.ProjectOnPlane(camera.transform.right, Vector3.up) : Vector3.right;
        Vector3 forward = camera != null ? Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up) : Vector3.forward;
        if (right.sqrMagnitude < 0.0001f)
            right = Vector3.right;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;

        Vector3 origin = ResolveSyntheticOrigin(scenario, right.normalized, forward.normalized);
        _syntheticSpawner.Spawn(new GpuSyntheticSpawnRequest
        {
            itemIdPrefix = scenario.name,
            instanceCount = scenario.syntheticInstanceCount,
            meshKind = scenario.syntheticMeshKind,
            productMode = scenario.syntheticProductMode,
            origin = origin,
            right = right,
            forward = forward,
            spacing = Mathf.Max(0.01f, scenario.syntheticSpacing),
            material = syntheticMaterial,
            productPrefabs = representativeProductPrefabs,
            productResourceIds = representativeProductIds
        });
    }

    private Vector3 ResolveSyntheticOrigin(ScenarioConfig scenario, Vector3 cameraRight, Vector3 cameraForward)
    {
        Vector3 anchor = DataHandler.Instance != null ? DataHandler.Instance.AgentPosition : transform.position;
        Camera camera = ResolveCamera();
        if (camera != null && anchor == Vector3.zero)
            anchor = camera.transform.position;

        Vector3 origin = anchor + cameraForward * 5f;
        if (scenario.syntheticVisibility == SyntheticVisibility.CameraPartial)
            origin += cameraRight * 4f;
        else if (scenario.syntheticVisibility == SyntheticVisibility.CameraOffscreen)
            origin = anchor + cameraRight * 7f;

        origin.y = Mathf.Max(0.15f, anchor.y);
        return origin;
    }

    private Camera ResolveCamera()
    {
        if (measurementCamera != null)
            return measurementCamera;
        if (GPUInstanceTracker.Instance != null && GPUInstanceTracker.Instance.MainCamera != null)
            return GPUInstanceTracker.Instance.MainCamera;
        return Camera.main;
    }

    private IEnumerator CleanupScenarioObjects()
    {
        DestroyLiveBBoxes();
        ShelfBuilder.DeleteAllPriceTags();

        if (GPUInstanceTracker.Instance != null)
            GPUInstanceTracker.Instance.ResetAllBatchesForProfiling();

        if (_syntheticSpawner != null)
            _syntheticSpawner.ClearGeneratedProducts();

        yield return null;
        yield return null;
    }

    private void DestroyLiveBBoxes()
    {
        ItemBBoxInfo[] bboxInfos = FindObjectsByType<ItemBBoxInfo>(FindObjectsSortMode.None);
        HashSet<GameObject> destroyedRoots = new();
        for (int i = 0; i < bboxInfos.Length; i++)
        {
            ItemBBoxInfo bboxInfo = bboxInfos[i];
            if (bboxInfo == null)
                continue;

            GameObject target = bboxInfo.isPhysicsObject
                ? bboxInfo.transform.root.gameObject
                : bboxInfo.gameObject;

            if (target != null && destroyedRoots.Add(target))
                Destroy(target);
        }
    }

    private void ClearShelfItemData()
    {
        ShelfItemData[] itemData = FindObjectsByType<ShelfItemData>(FindObjectsSortMode.None);
        for (int i = 0; i < itemData.Length; i++)
        {
            itemData[i].shelfItems.Clear();
            itemData[i].itemsTotalWidth = 0f;
        }
    }

    private RecorderSet CreateRecorderSet()
    {
        RecorderSet set = new();
        set.AddCounter("render_draw_calls", "Render Draw Calls", ProfilerCategory.Render, "Draw Calls Count");
        set.AddCounter("render_batches", "Render Batches", ProfilerCategory.Render, "Batches Count");
        set.AddCounter("render_setpass", "Render SetPass", ProfilerCategory.Render, "SetPass Calls Count");
        set.AddCounter("render_triangles", "Render Triangles", ProfilerCategory.Render, "Triangles Count");
        set.AddCounter("render_vertices", "Render Vertices", ProfilerCategory.Render, "Vertices Count");
        set.AddCounter("memory_total_used", "Total Used Memory", ProfilerCategory.Memory, "Total Used Memory");
        set.AddCounter("memory_gc_used", "GC Used Memory", ProfilerCategory.Memory, "GC Used Memory");
        set.AddMarker("marker_batch_rebuild_ns", "Batch Rebuild", "Sari.BatchInstancer.RebuildBuffers");
        set.AddMarker("marker_frustum_culling_ns", "Frustum Culling", "Sari.BatchInstancer.FrustumCulling");
        set.AddMarker("marker_draw_visible_ns", "Draw Visible", "Sari.BatchInstancer.DrawVisibleBuffers");
        set.AddMarker("marker_spawn_products_ns", "Spawn Products", "Sari.ItemSpawner.SpawnProducts");
        set.AddMarker("marker_add_to_instance_ns", "Add To Instance", "Sari.ItemSpawner.AddToInstance");
        set.AddMarker("marker_bbox_create_ns", "BBox Create", "Sari.ItemSpawner.CreateBBox");
        set.AddMarker("marker_row_combine_ns", "Row Combine", "Sari.ItemSpawner.RowCombine");
        return set;
    }

    private string WriteCsv(List<ScenarioResult> results)
    {
        string directory = Path.Combine(Application.persistentDataPath, "GpuSpawnPerf");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"gpu_spawn_perf_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        SortedSet<string> recorderColumns = new();
        for (int i = 0; i < results.Count; i++)
        {
            foreach (string key in results[i].recorderValues.Keys)
                recorderColumns.Add(key);
        }

        StringBuilder csv = new();
        csv.Append(
            "scenario,sample_frames,spawn_ms,frame_median_ms,frame_p95_ms,frame_avg_ms,frame_min_ms,frame_max_ms," +
            "cpu_median_ms,cpu_p95_ms,gpu_median_ms,gpu_p95_ms,bbox_before,bbox_after,price_tags_after," +
            "batchers,active_batchers,item_ids,source_instances,visible_instances,lods,submesh_draws,material_slots,texture_slots," +
            "estimated_source_vertices_lod0,estimated_source_triangles_lod0,estimated_visible_vertices,estimated_visible_triangles");

        foreach (string column in recorderColumns)
            csv.Append(',').Append(column);
        csv.AppendLine();

        for (int i = 0; i < results.Count; i++)
        {
            ScenarioResult result = results[i];
            GPUInstanceAggregateStats stats = result.gpuStats;
            AppendCsv(csv, result.scenario);
            AppendCsv(csv, result.sampleFrames);
            AppendCsv(csv, result.spawnMs);
            AppendCsv(csv, result.frameMedianMs);
            AppendCsv(csv, result.frameP95Ms);
            AppendCsv(csv, result.frameAverageMs);
            AppendCsv(csv, result.frameMinMs);
            AppendCsv(csv, result.frameMaxMs);
            AppendCsv(csv, result.cpuMedianMs);
            AppendCsv(csv, result.cpuP95Ms);
            AppendCsv(csv, result.gpuMedianMs);
            AppendCsv(csv, result.gpuP95Ms);
            AppendCsv(csv, result.bboxBefore);
            AppendCsv(csv, result.bboxAfter);
            AppendCsv(csv, result.priceTagsAfter);
            AppendCsv(csv, stats.batchers);
            AppendCsv(csv, stats.activeBatchers);
            AppendCsv(csv, stats.itemIds);
            AppendCsv(csv, stats.sourceInstances);
            AppendCsv(csv, stats.visibleInstances);
            AppendCsv(csv, stats.lods);
            AppendCsv(csv, stats.submeshDraws);
            AppendCsv(csv, stats.materialSlots);
            AppendCsv(csv, stats.textureSlots);
            AppendCsv(csv, stats.estimatedSourceVerticesLod0);
            AppendCsv(csv, stats.estimatedSourceTrianglesLod0);
            AppendCsv(csv, stats.estimatedVisibleVertices);
            AppendCsv(csv, stats.estimatedVisibleTriangles);

            foreach (string column in recorderColumns)
            {
                result.recorderValues.TryGetValue(column, out long value);
                AppendCsv(csv, value);
            }
            csv.AppendLine();
        }

        File.WriteAllText(path, csv.ToString());
        return path;
    }

    private static void AppendCsv(StringBuilder csv, string value)
    {
        if (csv.Length > 0 && csv[csv.Length - 1] != '\n')
            csv.Append(',');

        if (string.IsNullOrEmpty(value))
        {
            csv.Append(string.Empty);
            return;
        }

        csv.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
    }

    private static void AppendCsv(StringBuilder csv, int value) => AppendCsv(csv, value.ToString(CultureInfo.InvariantCulture));
    private static void AppendCsv(StringBuilder csv, long value) => AppendCsv(csv, value.ToString(CultureInfo.InvariantCulture));
    private static void AppendCsv(StringBuilder csv, float value) => AppendCsv(csv, value.ToString("0.###", CultureInfo.InvariantCulture));

    private static int CountLiveBBoxes() => FindObjectsByType<ItemBBoxInfo>(FindObjectsSortMode.None).Length;

    private static int CountLivePriceTags()
    {
        return FindObjectsByType<PriceTag>(FindObjectsSortMode.None).Length +
               FindObjectsByType<BakedPriceTag>(FindObjectsSortMode.None).Length;
    }

    private static float Average(List<float> values)
    {
        if (values == null || values.Count == 0)
            return 0f;

        float sum = 0f;
        for (int i = 0; i < values.Count; i++)
            sum += values[i];
        return sum / values.Count;
    }

    private static float Median(List<float> values) => Percentile(values, 0.5f);

    private static float Percentile(List<float> values, float percentile)
    {
        if (values == null || values.Count == 0)
            return 0f;

        List<float> sorted = new(values);
        sorted.Sort();
        int index = Mathf.Clamp(Mathf.CeilToInt(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static float Min(List<float> values)
    {
        if (values == null || values.Count == 0)
            return 0f;

        float min = values[0];
        for (int i = 1; i < values.Count; i++)
            if (values[i] < min)
                min = values[i];
        return min;
    }

    private static float Max(List<float> values)
    {
        if (values == null || values.Count == 0)
            return 0f;

        float max = values[0];
        for (int i = 1; i < values.Count; i++)
            if (values[i] > max)
                max = values[i];
        return max;
    }
}
