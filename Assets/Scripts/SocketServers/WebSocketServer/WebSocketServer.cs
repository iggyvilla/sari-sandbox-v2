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
        public float pitch_deg;
        public float camera_height;

        public LidarCenterSampleResponse(LidarSensor.CenterSample sample)
        {
            distance = sample.distance;
            hit = sample.hit;
            min_range = sample.minRange;
            max_range = sample.maxRange;
            pitch_deg = sample.pitchDeg;
            camera_height = sample.cameraHeight;
        }
    }

    /// <summary>Number of probe/bind attempts before giving up on a self-assigned port.</summary>
    private const int PortBindAttempts = 5;

    public static WebSocketHandler Instance { get; private set; }

    [SerializeField] int port = 8080;
    [SerializeField] AgentController agentController;
    [SerializeField] GameObject ikHumanoidGhostPrefab;
    [SerializeField] private bool sariSandboxV1CompatibilityLayer;

    // Distributed Sari Bench builds self-assign a free port, bind on every interface so a remote
    // coordinator can reach them, and register with that coordinator on startup. The build flag is
    // the authoritative opt-in; retaining a coordinator URL in a normal scene must not silently
    // enable fleet lifecycle behavior.
    [SerializeField] private bool distributedBenchmarkBuild;
    [SerializeField] private string coordinatorUrl;

    public ChatUIManager chatUIManager;

    private WebSocketServer _wss;
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private readonly Queue<IEnumerator> _queuedCoroutines = new();
    private readonly List<Action> _resetCompletionCallbacks = new();
    private readonly PendingCommandQueue _parkedCommands = new();
    private HumanoidGhostFollower _agentGhost;
    private bool _isRunningQueuedCoroutines;
    private Coroutine _queuedCoroutineRunner;
    private Coroutine _activeQueuedCoroutine;
    private Coroutine _resetCoroutine;
    private SandboxState _state = SandboxState.Booting;
    private bool _resetInFlight;
    private bool _resetWatchdogReported;
    private float _resetStartedAtRealtime;
    private string _resetPhase = "idle";

    private const float ResetWatchdogSeconds = 30f;

    /// <summary>Fires on the main thread whenever the sandbox changes readiness.</summary>
    public event Action<SandboxState> StateChanged;

    public SandboxState State => _state;

    /// <summary>The port the command server actually bound to. Self-assigned in distributed builds.</summary>
    public int BoundPort => port;

    /// <summary>Stable id this sandbox reports to the benchmark coordinator.</summary>
    public string SandboxId { get; private set; }

    /// <summary>
    /// True when this sandbox participates in a distributed benchmark fleet. The explicit build
    /// flag is authoritative so a stale or preconfigured coordinator URL cannot trigger startup
    /// resets and remote lifecycle commands in a normal single-sandbox session.
    /// </summary>
    public bool IsBenchmarkBuild => distributedBenchmarkBuild;

    /// <summary>Coordinator URL, e.g. ws://coordinator-host:9000/sandbox. Empty disables registration.</summary>
    public string CoordinatorUrl => coordinatorUrl;

    /// <summary>
    /// True only for a distributed fleet build, where several sandboxes share a machine and so
    /// cannot all take the serialized port. A normal single-sandbox session keeps the port it was
    /// configured with, regardless of whether a coordinator URL remains serialized in the scene.
    /// </summary>
    private bool SelfAssignsPort => distributedBenchmarkBuild;

    void Awake()
    {
        Instance = this;
        SandboxId = SandboxNetwork.LoadOrCreateSandboxId(persist: !IsBenchmarkBuild);
    }

    void Start()
    {
        SetAgent(agentController);
        StartServer();

        if (IsBenchmarkBuild)
        {
            BenchCoordinatorClient.AttachIfConfigured(this);

            // Never advertise readiness off a scene that has not been through a full reset - the
            // very first lease must see the same pristine store every later lease does.
            BeginReset(null);
        }
        else
        {
            SetState(SandboxState.Ready);
        }
    }

    void Update()
    {
        while (_mainThreadActions.TryDequeue(out Action action))
            action?.Invoke();

        if (_state != SandboxState.Ready && _state != SandboxState.Leased)
            _parkedCommands.ExpireStale();

        if (_resetInFlight &&
            !_resetWatchdogReported &&
            Time.realtimeSinceStartup - _resetStartedAtRealtime >= ResetWatchdogSeconds)
        {
            _resetWatchdogReported = true;
            Debug.LogError(
                $"Sandbox reset has not completed after {ResetWatchdogSeconds}s " +
                $"(phase: {_resetPhase}). It will remain unavailable rather than being leased dirty.");
        }
    }

    /// <summary>
    /// Binds the command server on every interface: a remote coordinator, remote agents, and agents
    /// running inside WSL all have to reach it, and a loopback-only bind refuses anything that does
    /// not originate on this machine's loopback interface. Distributed fleet builds additionally
    /// take an OS-chosen free port so several sandboxes can share a machine; everything else keeps
    /// the serialized port.
    /// </summary>
    private void StartServer()
    {
        const string bindAddress = "0.0.0.0";

        for (int attempt = 1; attempt <= PortBindAttempts; attempt++)
        {
            if (SelfAssignsPort) port = SandboxNetwork.FindFreePort();

            try
            {
                WebSocketServer server = new WebSocketServer($"ws://{bindAddress}:{port}");
                server.AddWebSocketService<SariAgentCommandBehavior>("/commands");
                server.AddWebSocketService<SariMultiplayerBehavior>("/multiplayer");
                server.Start();
                _wss = server;
                Debug.Log(
                    $"WebSocket server started on ws://{bindAddress}:{port}/commands and /multiplayer");
                return;
            }
            catch (Exception error)
            {
                // Probing for a free port then binding it is not atomic: another process can claim
                // it in between. Only a self-assigned port is worth retrying.
                if (!SelfAssignsPort || attempt == PortBindAttempts) throw;
                Debug.LogWarning(
                    $"Port {port} was taken between probe and bind (attempt {attempt}/{PortBindAttempts}): " +
                    error.Message);
            }
        }
    }

    private void SetState(SandboxState next)
    {
        if (_state == next) return;

        _state = next;
        if (next == SandboxState.Ready) _parkedCommands.DrainAll();
        StateChanged?.Invoke(next);
    }

    /// <summary>
    /// Marks the sandbox as leased by a benchmark run. Purely advisory - it does not gate commands,
    /// it only stops the coordinator handing this sandbox to a second runner.
    /// </summary>
    public void SetLeased(bool leased)
    {
        if (leased) SetState(SandboxState.Leased);
        else if (_state == SandboxState.Leased) SetState(SandboxState.Ready);
    }

    /// <summary>
    /// Resets the environment and reports ready only once it has genuinely settled. Concurrent
    /// calls collapse into the in-flight reset; the callback still fires for each caller.
    ///
    /// <paramref name="agentYawDegrees"/> is the facing the agent is left in, or null for the
    /// default. A caller that collapses into an in-flight reset does not get to change its facing.
    /// </summary>
    public void BeginReset(Action onComplete, float? agentYawDegrees = null)
    {
        if (onComplete != null)
            _resetCompletionCallbacks.Add(onComplete);

        if (_resetInFlight)
            return;

        // Reset is recovery-critical and must never sit behind a screenshot, LiDAR readback, or
        // movement coroutine left by the attempt that just ended. Cancel that disposable work and
        // run reset on its own coroutine lane.
        CancelQueuedCoroutinesForReset();
        _resetInFlight = true;
        _resetWatchdogReported = false;
        _resetStartedAtRealtime = Time.realtimeSinceStartup;
        _resetPhase = "starting";
        SetState(SandboxState.Resetting);
        _resetCoroutine = StartCoroutine(ResetRoutine(agentYawDegrees));
    }

    private IEnumerator ResetRoutine(float? agentYawDegrees)
    {
        Debug.Log("Sandbox reset started.");
        DataHandler data = DataHandler.Instance;
        if (data == null)
        {
            Debug.LogError("Cannot reset the environment: DataHandler.Instance is null.");
        }
        else
        {
            _resetPhase = "rebuilding environment";
            yield return data.ResetEnvironmentRoutine(agentYawDegrees);
        }

        _resetPhase = "publishing ready";
        _resetCoroutine = null;
        _resetInFlight = false;
        _resetWatchdogReported = false;
        SetState(SandboxState.Ready);
        Debug.Log(
            $"Sandbox reset completed in " +
            $"{Time.realtimeSinceStartup - _resetStartedAtRealtime:0.0}s.");
        _resetPhase = "idle";

        Action[] callbacks = _resetCompletionCallbacks.ToArray();
        _resetCompletionCallbacks.Clear();
        foreach (Action callback in callbacks)
        {
            try
            {
                callback?.Invoke();
            }
            catch (Exception error)
            {
                Debug.LogError($"Sandbox reset completion callback failed: {error}");
            }
        }
    }

    /// <summary>
    /// Abandons queued work owned by the attempt that just ended. Reset has its own coroutine lane,
    /// so a wedged GPU readback or a backlog of physics replies cannot strand lifecycle recovery.
    /// </summary>
    private void CancelQueuedCoroutinesForReset()
    {
        Coroutine active = _activeQueuedCoroutine;
        Coroutine runner = _queuedCoroutineRunner;

        _queuedCoroutines.Clear();
        _activeQueuedCoroutine = null;
        _queuedCoroutineRunner = null;
        _isRunningQueuedCoroutines = false;

        if (active != null) StopCoroutine(active);
        if (runner != null) StopCoroutine(runner);
    }

    /// <summary>
    /// Runs <paramref name="action"/> now if the sandbox is ready, otherwise parks it until it is.
    /// Returns false only when the parking queue is full, so the caller can answer the client
    /// itself rather than leaving it blocked forever.
    /// </summary>
    public bool ParkOrRun(Action action, string command = "", Action<string> onTimeout = null)
    {
        if (_state == SandboxState.Ready || _state == SandboxState.Leased)
        {
            action?.Invoke();
            return true;
        }

        return _parkedCommands.Park(command, action, onTimeout);
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
        _queuedCoroutineRunner = StartCoroutine(RunQueuedCoroutines());
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
            {
                _activeQueuedCoroutine = StartCoroutine(_queuedCoroutines.Dequeue());
                yield return _activeQueuedCoroutine;
                _activeQueuedCoroutine = null;
            }
        }
        finally
        {
            _activeQueuedCoroutine = null;
            _queuedCoroutineRunner = null;
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
