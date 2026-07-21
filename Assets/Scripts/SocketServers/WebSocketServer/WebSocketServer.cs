using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using WebSocketSharp.Server;

public class WebSocketHandler : MonoBehaviour
{
    [Serializable]
    public struct LidarCenterSampleResponse
    {
        public float distance;
        public bool hit;
        public float min_range;
        public float max_range;

        public LidarCenterSampleResponse(LidarSensor.CenterSample sample)
        {
            distance = sample.distance;
            hit = sample.hit;
            min_range = sample.minRange;
            max_range = sample.maxRange;
        }
    }

    public static WebSocketHandler Instance { get; private set; }

    [SerializeField] int port = 8080;
    [SerializeField] AgentController agentController;
    [SerializeField] GameObject ikHumanoidGhostPrefab;
    [SerializeField] private bool sariSandboxV1CompatibilityLayer;
    public ChatUIManager chatUIManager;

    private WebSocketServer _wss;
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private readonly Queue<IEnumerator> _queuedCoroutines = new();
    private HumanoidGhostFollower _agentGhost;
    private bool _isRunningQueuedCoroutines;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        SetAgent(agentController);

        _wss = new WebSocketServer($"ws://localhost:{port}");
        _wss.AddWebSocketService<SariAgentCommandBehavior>("/commands");
        _wss.AddWebSocketService<SariMultiplayerBehavior>("/multiplayer");
        _wss.Start();
        Debug.Log($"WebSocket server started on ws://localhost:{port}/commands and /multiplayer");
    }

    void Update()
    {
        while (_mainThreadActions.TryDequeue(out Action action))
            action?.Invoke();
    }

    public void Enqueue(Action action) => _mainThreadActions.Enqueue(action);

    /// <summary>
    /// Queues Unity work that must run as a coroutine on the main thread.
    /// Coroutines are serialized so expensive captures do not overlap.
    /// </summary>
    public void EnqueueCoroutine(IEnumerator routine)
    {
        _queuedCoroutines.Enqueue(routine);
        if (_isRunningQueuedCoroutines) return;

        _isRunningQueuedCoroutines = true;
        StartCoroutine(RunQueuedCoroutines());
    }

    public AgentController Agent => agentController;

    public bool SariSandboxV1CompatibilityLayer => sariSandboxV1CompatibilityLayer;

    public Camera AgentCamera
    {
        get
        {
            Camera camera = agentController != null
                ? agentController.GetComponentInChildren<Camera>(true)
                : null;
            if (camera != null) return camera;

            // AgentSandbox binds the VR controller at runtime, while the IK avatar path only
            // tags and registers its camera. Resolve those valid runtime configurations too.
            camera = Camera.main;
            if (camera != null) return camera;

            GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
            if (tracker != null && tracker.MainCamera != null)
                return tracker.MainCamera;

            AgentControllerBase[] agents = FindObjectsByType<AgentControllerBase>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int i = 0; i < agents.Length; i++)
            {
                AgentControllerBase candidate = agents[i];
                if (candidate == null || candidate.isMultiplayerAgent) continue;

                camera = candidate.GetComponentInChildren<Camera>(true);
                if (camera != null) return camera;
            }

            return null;
        }
    }

    public HumanoidGhostFollower AgentGhost => _agentGhost;

    /// <summary>
    /// Rebinds the WebSocket-controlled agent and recreates its hidden ghost follower.
    /// </summary>
    public void SetAgent(AgentController controller)
    {
        if (agentController == controller && _agentGhost != null) return;

        if (_agentGhost != null) Destroy(_agentGhost.gameObject);

        agentController = controller;
        _agentGhost = HumanoidGhostFactory.Spawn(ikHumanoidGhostPrefab, agentController);
    }

    /// <summary>
    /// Schedules a screenshot capture and returns PNG bytes through the callback.
    /// </summary>
    public void EnqueueScreenshot(
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<byte[]> callback)
    {
        EnqueueCoroutine(ScreenshotRoutine(camera, hiddenGhost, callback));
    }

    /// <summary>
    /// Schedules a LiDAR scan and returns the raw LDR1 binary payload through the byte callback.
    /// Errors are returned as text through <paramref name="errorCallback"/>.
    /// </summary>
    public void EnqueueLidarScan(
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<byte[]> callback,
        Action<string> errorCallback)
    {
        EnqueueCoroutine(LidarScanRoutine(camera, hiddenGhost, callback, errorCallback));
    }

    /// <summary>
    /// Schedules a center-gaze LiDAR sample and returns its distance metadata.
    /// </summary>
    public void EnqueueLidarCenterSample(
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<LidarCenterSampleResponse> callback,
        Action<string> errorCallback)
    {
        EnqueueCoroutine(LidarCenterSampleRoutine(camera, hiddenGhost, callback, errorCallback));
    }

    void OnDestroy()
    {
        _wss?.Stop();
        if (_agentGhost != null) Destroy(_agentGhost.gameObject);
    }

    /// <summary>
    /// Runs queued capture/movement coroutines one at a time on Unity's main thread.
    /// </summary>
    private IEnumerator RunQueuedCoroutines()
    {
        try
        {
            while (_queuedCoroutines.Count > 0)
                yield return StartCoroutine(_queuedCoroutines.Dequeue());
        }
        finally
        {
            _isRunningQueuedCoroutines = false;
        }
    }

    /// <summary>
    /// Captures a camera screenshot while temporarily hiding the matching ghost from that view.
    /// </summary>
    private static IEnumerator ScreenshotRoutine(
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<byte[]> callback)
    {
        if (camera == null) yield break;

        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        Camera originalCamera = tracker != null ? tracker.MainCamera : null;

        try
        {
            tracker?.SetCamera(camera);
            yield return null; // let instancer dispatch with the requested frustum
            yield return ScreenshotUtility.GetScreenshotBytes(
                camera,
                callback,
                () =>
                {
                    if (hiddenGhost != null) hiddenGhost.SetRenderersVisible(false);
                },
                () =>
                {
                    if (hiddenGhost != null) hiddenGhost.SetRenderersVisible(true);
                });
        }
        finally
        {
            if (tracker != null) tracker.SetCamera(originalCamera);
        }
    }

    /// <summary>
    /// Resolves the correct level LiDAR sensor, captures a scan, and forwards its binary payload.
    /// </summary>
    private static IEnumerator LidarScanRoutine(
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<byte[]> callback,
        Action<string> errorCallback)
    {
        if (camera == null)
        {
            errorCallback?.Invoke("Error: no camera found for LiDAR scan");
            yield break;
        }

        // Resolve the level LiDAR mount before rendering. The resulting payload is
        // passed back as byte[] and sent by WebSocketSharp as a binary WebSocket frame.
        LidarSensor sensor = LidarSensor.ResolveLevelSensor(camera);

        yield return sensor.CaptureScan(camera, hiddenGhost, callback, errorCallback);
    }

    /// <summary>
    /// Resolves the level LiDAR sensor, renders its forward face along the camera gaze, and
    /// forwards the center-pixel distance as a JSON-ready response value.
    /// </summary>
    private static IEnumerator LidarCenterSampleRoutine(
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<LidarCenterSampleResponse> callback,
        Action<string> errorCallback)
    {
        if (camera == null)
        {
            errorCallback?.Invoke("Error: no camera found for LiDAR center sample");
            yield break;
        }

        LidarSensor sensor = LidarSensor.ResolveLevelSensor(camera);
        yield return sensor.CaptureCenterSample(
            camera,
            hiddenGhost,
            sample => callback?.Invoke(new LidarCenterSampleResponse(sample)),
            errorCallback);
    }
}
