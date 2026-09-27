using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using WebSocketSharp.Server;

public class WebSocketHandler : MonoBehaviour
{
    private sealed class QueuedCoroutineWork
    {
        public readonly string Command;
        public readonly IEnumerator Routine;
        public readonly Action<string> OnFailure;
        public readonly Action OnAbort;
        public readonly float EnqueuedAtRealtime;
        public readonly float TimeoutSeconds;

        public QueuedCoroutineWork(
            string command,
            IEnumerator routine,
            Action<string> onFailure,
            Action onAbort,
            float timeoutSeconds)
        {
            Command = string.IsNullOrEmpty(command) ? "unnamed" : command;
            Routine = routine;
            OnFailure = onFailure;
            OnAbort = onAbort;
            EnqueuedAtRealtime = Time.realtimeSinceStartup;
            TimeoutSeconds = timeoutSeconds;
        }
    }

    private sealed class QueuedCoroutineExecution
    {
        public bool Completed;
    }

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
    private readonly Queue<QueuedCoroutineWork> _queuedCoroutines = new();
    private readonly List<Action> _resetCompletionCallbacks = new();
    private readonly PendingCommandQueue _parkedCommands = new();
    private HumanoidGhostFollower _agentGhost;
    private bool _isRunningQueuedCoroutines;
    private Coroutine _queuedCoroutineRunner;
    private Coroutine _activeQueuedCoroutine;
    private QueuedCoroutineWork _activeQueuedWork;
    private float _activeQueuedStartedAtRealtime;
    private Coroutine _resetCoroutine;
    private SandboxState _state = SandboxState.Booting;
    private bool _leased;
    private bool _resetInFlight;
    private bool _resetWatchdogReported;
    private float _resetStartedAtRealtime;
    private string _resetPhase = "idle";

    private const float ResetWatchdogSeconds = 30f;
    private const int MaxQueuedCoroutines = 64;
    private const float QueuedCoroutineTimeoutSeconds = 8f;

    /// <summary>Fires on the main thread whenever the sandbox changes readiness.</summary>
    public event Action<SandboxState> StateChanged;

    public SandboxState State => _state;

    private bool IsReadyForCommands => _state == SandboxState.Ready || _state == SandboxState.Leased;

    /// <summary>The port the command server actually bound to. Self-assigned in distributed builds.</summary>
    public int BoundPort => port;

    /// <summary>Command currently occupying the serialized coroutine lane, if any.</summary>
    public string ActiveQueuedCommand => _activeQueuedWork != null ? _activeQueuedWork.Command : "";

    /// <summary>Real-time age of the active command. Exposed for health/status diagnostics.</summary>
    public float ActiveQueuedCommandAgeSeconds => _activeQueuedWork != null
        ? Mathf.Max(0f, Time.realtimeSinceStartup - _activeQueuedStartedAtRealtime)
        : 0f;

    /// <summary>Number of commands waiting behind the active serialized command.</summary>
    public int QueuedCommandCount => _queuedCoroutines.Count;

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
        if (IsBenchmarkBuild) Application.runInBackground = true;
        SandboxId = SandboxNetwork.LoadOrCreateSandboxId(persist: !IsBenchmarkBuild);
    }

    void Start()
    {
        Debug.Log(
            $"Sandbox runtime configuration: runInBackground={Application.runInBackground}, " +
            $"unityVersion={Application.unityVersion}, version={Application.version}.");

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
        {
            // Isolated so one failed send (e.g. a closed session) cannot stall the rest of the frame.
            try
            {
                action?.Invoke();
            }
            catch (Exception error)
            {
                Debug.LogError($"Main-thread action failed: {error}");
            }
        }

        if (!IsReadyForCommands)
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
        if (next == SandboxState.Ready || next == SandboxState.Leased) _parkedCommands.DrainAll();
        StateChanged?.Invoke(next);
    }

    /// <summary>
    /// Marks the sandbox as leased by a benchmark run. Purely advisory - it does not gate commands,
    /// it only stops the coordinator handing this sandbox to a second runner. A lease never masks
    /// Booting/Resetting; it is applied once the sandbox settles.
    /// </summary>
    public void SetLeased(bool leased)
    {
        _leased = leased;
        if (_state == SandboxState.Ready || _state == SandboxState.Leased)
            SetState(ReadyState);
    }

    private SandboxState ReadyState => _leased ? SandboxState.Leased : SandboxState.Ready;

    /// <summary>
    /// Resets the environment and reports ready only once it has genuinely settled. Concurrent
    /// calls collapse into the in-flight reset; the callback still fires for each caller.
    ///
    /// <paramref name="agentYawDegrees"/> is the facing the agent is left in, or null for the
    /// default. A caller that collapses into an in-flight reset does not get to change its facing.
    /// <paramref name="releaseLease"/> drops the lease without ever publishing a transient Ready.
    /// </summary>
    public void BeginReset(Action onComplete, float? agentYawDegrees = null, bool releaseLease = false)
    {
        if (releaseLease) _leased = false;

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
        SetState(ReadyState);
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
        QueuedCoroutineWork activeWork = _activeQueuedWork;
        QueuedCoroutineWork[] pending = _queuedCoroutines.ToArray();

        _queuedCoroutines.Clear();
        _activeQueuedCoroutine = null;
        _activeQueuedWork = null;
        _queuedCoroutineRunner = null;
        _isRunningQueuedCoroutines = false;

        if (active != null) StopCoroutine(active);
        if (runner != null) StopCoroutine(runner);

        AbortQueuedWork(activeWork);
        // Answer every abandoned command so no client is left blocked in recv().
        const string cancelled = "was cancelled by an environment reset";
        NotifyQueuedFailure(activeWork, cancelled);
        foreach (QueuedCoroutineWork work in pending)
            NotifyQueuedFailure(work, cancelled);

        if (activeWork != null || pending.Length > 0)
        {
            Debug.LogWarning(
                $"Environment reset cancelled active command " +
                $"'{(activeWork != null ? activeWork.Command : "none")}' and " +
                $"{pending.Length} waiting command(s).");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> now if the sandbox is ready, otherwise parks it until it is.
    /// Returns false only when the parking queue is full, so the caller can answer the client
    /// itself rather than leaving it blocked forever.
    /// </summary>
    public bool ParkOrRun(Action action, string command = "", Action<string> onTimeout = null)
    {
        if (IsReadyForCommands)
        {
            action?.Invoke();
            return true;
        }

        return _parkedCommands.Park(command, action, onTimeout);
    }

    public void Enqueue(Action action) => _mainThreadActions.Enqueue(action);

    /// <summary>Broadcast an authoritative server event to every multiplayer session.</summary>
    public void BroadcastMultiplayer(string message)
    {
        if (_wss == null || string.IsNullOrEmpty(message)) return;
        _wss.WebSocketServices["/multiplayer"].Sessions.Broadcast(message);
    }

    /// <summary>
    /// Queues named Unity work that must run as a coroutine on the main thread. Coroutines are
    /// serialized so expensive captures do not overlap, but every item has a real-time deadline
    /// and the queue is bounded so one bad client cannot create an unlimited stale backlog.
    /// </summary>
    public bool EnqueueCoroutine(
        string command,
        IEnumerator routine,
        Action<string> onFailure,
        Action onAbort = null,
        float timeoutSeconds = QueuedCoroutineTimeoutSeconds)
    {
        if (routine == null)
        {
            NotifyFailureCallback(onFailure, command, "could not be queued because its coroutine is null");
            return false;
        }

        if (!HasCoroutineCapacity(command, onFailure)) return false;

        QueuedCoroutineWork work = new QueuedCoroutineWork(
            command,
            routine,
            onFailure,
            onAbort,
            Mathf.Max(0.1f, timeoutSeconds));
        _queuedCoroutines.Enqueue(work);

        Debug.Log(
            $"Coroutine queue enqueued '{work.Command}' " +
            $"(waiting={_queuedCoroutines.Count}, active='{ActiveQueuedCommand}').");

        if (_isRunningQueuedCoroutines) return true;

        _isRunningQueuedCoroutines = true;
        _queuedCoroutineRunner = StartCoroutine(RunQueuedCoroutines());
        return true;
    }

    /// <summary>
    /// True when another command can be queued; otherwise reports the rejection. Lets callers
    /// refuse a command before mutating the agent rather than after.
    /// </summary>
    public bool HasCoroutineCapacity(string command, Action<string> onFailure)
    {
        int outstandingCount = _queuedCoroutines.Count + (_activeQueuedWork != null ? 1 : 0);
        if (outstandingCount < MaxQueuedCoroutines) return true;

        NotifyFailureCallback(
            onFailure,
            command,
            $"was rejected because the coroutine queue is full ({MaxQueuedCoroutines} items)");
        return false;
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

    /// <summary>True for the camera capture commands handled by <see cref="EnqueueCapture"/>.</summary>
    public static bool IsCaptureCommand(string command) =>
        command == "RequestScreenshot" || command == "RequestLidarScan" || command == "RequestLidarCenter";

    /// <summary>
    /// Queues a screenshot or LiDAR capture for <paramref name="camera"/>. Binary payloads (PNG, LDR1)
    /// go through <paramref name="sendBytes"/> as binary frames; JSON and errors through
    /// <paramref name="sendText"/>.
    /// </summary>
    public void EnqueueCapture(
        string command,
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<string> sendText,
        Action<byte[]> sendBytes)
    {
        if (camera == null)
        {
            sendText("Error: no camera found for agent");
            return;
        }

        switch (command)
        {
            case "RequestScreenshot":
                EnqueueScreenshot(camera, hiddenGhost, sendBytes, sendText);
                break;

            case "RequestLidarScan":
                EnqueueLidar(command, "LiDAR scan", camera, sendText,
                    sensor => sensor.CaptureScan(camera, hiddenGhost, sendBytes, sendText));
                break;

            case "RequestLidarCenter":
                EnqueueLidar(command, "LiDAR center sample", camera, sendText,
                    sensor => sensor.CaptureCenterSample(
                        camera,
                        hiddenGhost,
                        sample => sendText(JsonUtility.ToJson(new LidarCenterSampleResponse(sample))),
                        sendText));
                break;
        }
    }

    private void EnqueueScreenshot(
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<byte[]> callback,
        Action<string> errorCallback)
    {
        Action abortCleanup = null;
        EnqueueCoroutine(
            "RequestScreenshot",
            ScreenshotRoutine(
                camera,
                hiddenGhost,
                callback,
                errorCallback,
                cleanup => abortCleanup = cleanup),
            errorCallback,
            () => abortCleanup?.Invoke());
    }

    private void EnqueueLidar(
        string command,
        string label,
        Camera camera,
        Action<string> errorCallback,
        Func<LidarSensor, IEnumerator> capture)
    {
        LidarSensor activeSensor = null;
        EnqueueCoroutine(
            command,
            LidarRoutine(camera, label, errorCallback, capture, sensor => activeSensor = sensor),
            errorCallback,
            () => activeSensor?.CancelActiveCapture());
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
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
                QueuedCoroutineWork work = _queuedCoroutines.Dequeue();
                _activeQueuedWork = work;
                _activeQueuedStartedAtRealtime = Time.realtimeSinceStartup;

                float deadline = work.EnqueuedAtRealtime + work.TimeoutSeconds;
                float queuedFor = _activeQueuedStartedAtRealtime - work.EnqueuedAtRealtime;
                if (_activeQueuedStartedAtRealtime >= deadline)
                {
                    NotifyQueuedFailure(
                        work,
                        $"timed out after waiting {queuedFor:0.00}s in the coroutine queue");
                    _activeQueuedWork = null;
                    continue;
                }

                Debug.Log(
                    $"Coroutine queue starting '{work.Command}' after {queuedFor:0.00}s " +
                    $"(waiting={_queuedCoroutines.Count}).");

                QueuedCoroutineExecution execution = new QueuedCoroutineExecution();
                _activeQueuedCoroutine = StartCoroutine(TrackQueuedCoroutine(work.Routine, execution));

                while (!execution.Completed && Time.realtimeSinceStartup < deadline)
                    yield return null;

                float totalAge = Time.realtimeSinceStartup - work.EnqueuedAtRealtime;
                if (!execution.Completed)
                {
                    Coroutine timedOut = _activeQueuedCoroutine;
                    _activeQueuedCoroutine = null;
                    if (timedOut != null) StopCoroutine(timedOut);

                    AbortQueuedWork(work);
                    NotifyQueuedFailure(
                        work,
                        $"timed out after {totalAge:0.00}s in the coroutine queue");
                }
                else
                {
                    Debug.Log(
                        $"Coroutine queue completed '{work.Command}' in {totalAge:0.00}s " +
                        $"(waiting={_queuedCoroutines.Count}).");
                }

                _activeQueuedCoroutine = null;
                _activeQueuedWork = null;
            }
        }
        finally
        {
            _activeQueuedCoroutine = null;
            _activeQueuedWork = null;
            _queuedCoroutineRunner = null;
            _isRunningQueuedCoroutines = false;
        }
    }

    private static IEnumerator TrackQueuedCoroutine(
        IEnumerator routine,
        QueuedCoroutineExecution execution)
    {
        try
        {
            yield return routine;
        }
        finally
        {
            execution.Completed = true;
        }
    }

    private static void AbortQueuedWork(QueuedCoroutineWork work)
    {
        if (work?.OnAbort == null) return;

        try
        {
            work.OnAbort();
        }
        catch (Exception error)
        {
            Debug.LogError($"Cleanup for queued command '{work.Command}' failed: {error}");
        }
    }

    private static void NotifyQueuedFailure(QueuedCoroutineWork work, string reason)
    {
        if (work == null) return;
        NotifyFailureCallback(work.OnFailure, work.Command, reason);
    }

    private static void NotifyFailureCallback(
        Action<string> onFailure,
        string command,
        string reason)
    {
        string commandName = string.IsNullOrEmpty(command) ? "unnamed" : command;
        string message = $"Error: command '{commandName}' {reason}.";
        Debug.LogError(message);

        try
        {
            onFailure?.Invoke(message);
        }
        catch (Exception error)
        {
            // The original WebSocket commonly closes before an infrastructure timeout expires.
            Debug.LogWarning(
                $"Could not return queued-command failure for '{commandName}': {error.Message}");
        }
    }

    /// <summary>
    /// Captures a camera screenshot while temporarily hiding the matching ghost from that view.
    /// </summary>
    private static IEnumerator ScreenshotRoutine(
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<byte[]> callback,
        Action<string> errorCallback,
        Action<Action> setAbortCleanup)
    {
        const string cameraLost = "Error: screenshot camera was destroyed before capture";
        if (camera == null)
        {
            errorCallback?.Invoke(cameraLost);
            yield break;
        }

        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        Camera originalCamera = tracker != null ? tracker.MainCamera : null;
        bool cleaned = false;
        Action cleanup = () =>
        {
            if (cleaned) return;
            cleaned = true;
            if (hiddenGhost != null) hiddenGhost.SetRenderersVisible(true);
            if (tracker != null) tracker.SetCamera(originalCamera);
        };
        setAbortCleanup?.Invoke(cleanup);

        try
        {
            tracker?.SetCamera(camera);
            yield return null; // let instancer dispatch with the requested frustum
            bool delivered = false;
            yield return ScreenshotUtility.GetScreenshotBytes(
                camera,
                bytes =>
                {
                    delivered = true;
                    callback?.Invoke(bytes);
                },
                () =>
                {
                    if (hiddenGhost != null) hiddenGhost.SetRenderersVisible(false);
                },
                () =>
                {
                    if (hiddenGhost != null) hiddenGhost.SetRenderersVisible(true);
                },
                () => cleaned);

            // The capture skips its callback when the camera dies mid-wait; still answer the client.
            if (!delivered && !cleaned) errorCallback?.Invoke(cameraLost);
        }
        finally
        {
            cleanup();
            setAbortCleanup?.Invoke(null);
        }
    }

    /// <summary>
    /// Resolves the level LiDAR sensor for <paramref name="camera"/> and runs
    /// <paramref name="capture"/> on it, always cancelling any leftover GPU work afterwards.
    /// </summary>
    private static IEnumerator LidarRoutine(
        Camera camera,
        string label,
        Action<string> errorCallback,
        Func<LidarSensor, IEnumerator> capture,
        Action<LidarSensor> setActiveSensor)
    {
        if (camera == null)
        {
            errorCallback?.Invoke($"Error: no camera found for {label}");
            yield break;
        }

        LidarSensor sensor = LidarSensor.ResolveLevelSensor(camera);
        setActiveSensor?.Invoke(sensor);
        try
        {
            yield return capture(sensor);
        }
        finally
        {
            sensor.CancelActiveCapture();
            setActiveSensor?.Invoke(null);
        }
    }
}
