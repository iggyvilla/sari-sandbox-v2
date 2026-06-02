using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

public class WebSocketHandler : MonoBehaviour
{
    public static WebSocketHandler Instance { get; private set; }

    [SerializeField] int port = 8080;
    [SerializeField] AgentController agentController;
    [SerializeField] GameObject ikHumanoidGhostPrefab;
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

public class SariAgentCommandBehavior : WebSocketBehavior
{
    [Serializable]
    class CommandData
    {
        public string command;
        public float[] translation;
        public float[] rotation;
        public float[] handPosition;
        public float[] handRotation;
    }

    protected override void OnMessage(MessageEventArgs e)
    {
        Debug.Log($"WebSocket recv: {e.Data}");

        CommandData cmd = JsonUtility.FromJson<CommandData>(e.Data);
        if (cmd == null)
        {
            Send("Error: invalid JSON");
            return;
        }

        SariAgentCommandBehavior session = this;
        WebSocketHandler.Instance.Enqueue(() => HandleCommand(cmd, session));
    }

    private static void HandleCommand(CommandData cmd, SariAgentCommandBehavior session)
    {
        AgentController agent = WebSocketHandler.Instance.Agent;

        switch (cmd.command)
        {
            case "TransformAgent":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TransformAgent(ToVec3(cmd.translation), ToVec3(cmd.rotation));
                session.Send($"Agent position: {agent.transform.position}, rotation: {agent.transform.eulerAngles}");
                break;

            case "TransformHand":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TransformHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation));
                session.Send("Hand transformed");
                break;

            case "TranslateAgent":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TranslateAgent(ToVec3(cmd.translation), ToVec3(cmd.rotation));
                session.Send($"Agent position: {agent.transform.position}, rotation: {agent.transform.eulerAngles}");
                break;

            case "TranslateHand":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TranslateHand(ToVec3(cmd.translation), ToVec3(cmd.rotation));
                session.Send("Hand translated");
                break;

            case "ResetHandPosition":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.ResetHandPosition();
                session.Send("Hand position reset");
                break;

            case "IsHoldingItem":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                session.Send(agent.IsHoldingItem() ? "true" : "false");
                break;

            case "ToggleGrip":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.ToggleGrip();
                session.Send("Grip toggled");
                break;

            case "TogglePoint":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TogglePoint();
                session.Send("Point toggled");
                break;

            case "RequestScreenshot":
            {
                WebSocketHandler handler = WebSocketHandler.Instance;
                Camera camera = handler.AgentCamera;
                if (camera == null) { session.Send("Error: no camera found for agent"); return; }
                handler.EnqueueScreenshot(camera, handler.AgentGhost, base64 => session.Send(base64));
                break;
            }

            case "ResetEnvironment":
                DataHandler.Instance.ResetEnvironment();
                session.Send("Environment reset");
                break;

            default:
                Debug.LogWarning($"WebSocket unknown command: {cmd.command}");
                session.Send($"Unknown command: {cmd.command}");
                break;
        }
    }

    private static Vector3 ToVec3(float[] arr)
    {
        if (arr == null || arr.Length < 3) return Vector3.zero;
        return new Vector3(arr[0], arr[1], arr[2]);
    }
}

public class SariMultiplayerBehavior : WebSocketBehavior
{
    [Serializable] class MultiplayerCommandData
    {
        public string command;
        public float[] translation;
        public float[] rotation;
        public float[] handPosition;
        public float[] handRotation;
        public string message;
    }

    [Serializable] class JoinedMsg
    {
        public string type = "Joined";
        public string agentId;
    }

    [Serializable] class AgentStateMsg
    {
        public string type;
        public string agentId;
        public float[] position;
        public float[] rotation;
    }

    [Serializable] class AgentUpdateMsg
    {
        public string type = "AgentUpdate";
        public string agentId;
        public string command;
        public float[] translation;
        public float[] rotation;
        public float[] handPosition;
        public float[] handRotation;
    }

    [Serializable] class AgentLeftMsg
    {
        public string type = "AgentLeft";
        public string agentId;
    }

    [Serializable] class ChatMsg
    {
        public string type = "Chat";
        public string agentId;
        public string message;
    }

    [Serializable] class ChatLogMsg
    {
        public string type = "ChatLog";
        public string log;
    }

    private string _agentId;

    protected override void OnMessage(MessageEventArgs e)
    {
        MultiplayerCommandData cmd = JsonUtility.FromJson<MultiplayerCommandData>(e.Data);
        if (cmd == null) { Send("Error: invalid JSON"); return; }
        WebSocketHandler.Instance.Enqueue(() => HandleCommand(cmd));
    }

    protected override void OnClose(CloseEventArgs e)
    {
        if (_agentId == null) return;
        Sessions.Broadcast(JsonUtility.ToJson(new AgentLeftMsg { agentId = _agentId }));
        string agentId = _agentId;
        WebSocketHandler.Instance.Enqueue(() => MultiplayerAgentManager.Instance.DespawnAgent(agentId));
    }

    private void HandleCommand(MultiplayerCommandData cmd)
    {
        switch (cmd.command)
        {
            case "Join":
            {
                string agentId = MultiplayerAgentManager.Instance.SpawnAgent();
                if (agentId == null) { Send("Error: failed to spawn multiplayer agent"); return; }
                _agentId = agentId;

                Send(JsonUtility.ToJson(new JoinedMsg { agentId = agentId }));

                foreach (AgentState s in MultiplayerAgentManager.Instance.GetSnapshot(agentId))
                    Send(JsonUtility.ToJson(new AgentStateMsg
                    {
                        type = "Snapshot",
                        agentId = s.agentId,
                        position = Vec3ToArr(s.position),
                        rotation = Vec3ToArr(s.rotation.eulerAngles)
                    }));

                AgentControllerBase newAgent = MultiplayerAgentManager.Instance.GetAgent(agentId);
                Vector3 spawnPos = newAgent != null ? newAgent.MovementRoot.position : Vector3.zero;
                Vector3 spawnRot = newAgent != null ? newAgent.ViewTransform.eulerAngles : Vector3.zero;
                Sessions.Broadcast(JsonUtility.ToJson(new AgentStateMsg
                {
                    type = "AgentSpawned",
                    agentId = agentId,
                    position = Vec3ToArr(spawnPos),
                    rotation = Vec3ToArr(spawnRot)
                }));
                break;
            }

            case "RequestScreenshot":
            {
                if (_agentId == null) { Send("Error: not joined"); return; }
                Camera mpCamera = MultiplayerAgentManager.Instance.GetAgentCamera(_agentId);
                if (mpCamera == null) { Send("Error: no camera found for agent"); return; }
                WebSocketHandler.Instance.EnqueueScreenshot(
                    mpCamera,
                    MultiplayerAgentManager.Instance.GetGhostFollower(_agentId),
                    base64 => Send(base64));
                break;
            }

            case "Chat":
            {
                if (_agentId == null) { Send("Error: not joined"); return; }
                if (string.IsNullOrEmpty(cmd.message)) { Send("Error: empty message"); return; }
                string chatLine = $"{_agentId}: {cmd.message}";
                ChatUIManager.Instance.Log(chatLine);
                Sessions.Broadcast(JsonUtility.ToJson(new ChatMsg { agentId = _agentId, message = cmd.message }));
                break;
            }

            case "RequestChatLog":
            {
                ChatUIManager chatUIManager = WebSocketHandler.Instance.chatUIManager;
                if (chatUIManager == null) { Send("Error: ChatUIManager not assigned"); return; }
                Send(JsonUtility.ToJson(new ChatLogMsg { log = chatUIManager.ChatLog }));
                break;
            }

            default:
            {
                if (_agentId == null) { Send("Error: not joined"); return; }
                AgentControllerBase agent = MultiplayerAgentManager.Instance.GetAgent(_agentId);
                if (agent == null) { Send("Error: agent not found"); return; }

                ExecuteAgentCommand(cmd, agent);

                Sessions.Broadcast(JsonUtility.ToJson(new AgentUpdateMsg
                {
                    agentId = _agentId,
                    command = cmd.command,
                    translation = cmd.translation,
                    rotation = cmd.rotation,
                    handPosition = cmd.handPosition,
                    handRotation = cmd.handRotation
                }));
                break;
            }
        }
    }

    private void ExecuteAgentCommand(MultiplayerCommandData cmd, AgentControllerBase agent)
    {
        switch (cmd.command)
        {
            // Semantically, transform is very different from translate,
            // but Sari Sandbox v1 used TransformAgent as the command even if
            // it translates. This is only here for compatability.
            case "TransformAgent":
                // agent.TransformAgent(ToVec3(cmd.translation), ToVec3(cmd.rotation));
                // Send($"Agent position: {agent.transform.position}, rotation: {agent.transform.eulerAngles}");
                // break;
            case "TranslateAgent":
                agent.TranslateAgent(ToVec3(cmd.translation), ToVec3(cmd.rotation));
                Send($"Agent position: {agent.transform.position}, rotation: {agent.transform.eulerAngles}");
                break;
            case "TransformHand":
                // agent.TransformHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation));
                // Send("Hand transformed");
                // break;
            case "TranslateHand":
                agent.TranslateHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation));
                Send("Hand translated");
                break;
            case "ResetHandPosition":
                agent.ResetHandPosition();
                Send("Hand position reset");
                break;
            case "ToggleGrip":
                agent.ToggleGrip();
                Send("Grip toggled");
                break;
            case "TogglePoint":
                agent.TogglePoint();
                Send("Point toggled");
                break;
            default:
                Send($"Unknown command: {cmd.command}");
                break;
        }
    }

    private static float[] Vec3ToArr(Vector3 v) => new float[] { v.x, v.y, v.z };

    private static Vector3 ToVec3(float[] arr)
    {
        if (arr == null || arr.Length < 3) return Vector3.zero;
        return new Vector3(arr[0], arr[1], arr[2]);
    }
}
