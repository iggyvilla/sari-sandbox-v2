using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using WebSocketSharp.Server;

public class WebSocketHandler : MonoBehaviour
{
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

    public void EnqueueCoroutine(IEnumerator routine)
    {
        _queuedCoroutines.Enqueue(routine);
        if (_isRunningQueuedCoroutines) return;

        _isRunningQueuedCoroutines = true;
        StartCoroutine(RunQueuedCoroutines());
    }

    public AgentController Agent => agentController;

    public bool SariSandboxV1CompatibilityLayer => sariSandboxV1CompatibilityLayer;

    public Camera AgentCamera =>
        agentController != null
            ? agentController.GetComponentInChildren<Camera>(true)
            : null;

    public HumanoidGhostFollower AgentGhost => _agentGhost;

    public void SetAgent(AgentController controller)
    {
        if (agentController == controller && _agentGhost != null) return;

        if (_agentGhost != null) Destroy(_agentGhost.gameObject);

        agentController = controller;
        _agentGhost = HumanoidGhostFactory.Spawn(ikHumanoidGhostPrefab, agentController);
    }

    public void EnqueueScreenshot(
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<string> callback)
    {
        EnqueueCoroutine(ScreenshotRoutine(camera, hiddenGhost, callback));
    }

    void OnDestroy()
    {
        _wss?.Stop();
        if (_agentGhost != null) Destroy(_agentGhost.gameObject);
    }

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

    private static IEnumerator ScreenshotRoutine(
        Camera camera,
        HumanoidGhostFollower hiddenGhost,
        Action<string> callback)
    {
        if (camera == null) yield break;

        GPUInstanceTracker tracker = GPUInstanceTracker.Instance;
        Camera originalCamera = tracker != null ? tracker.MainCamera : null;

        try
        {
            tracker?.SetCamera(camera);
            yield return null; // let instancer dispatch with the requested frustum
            yield return ScreenshotUtility.GetScreenshotBase64(
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
}
