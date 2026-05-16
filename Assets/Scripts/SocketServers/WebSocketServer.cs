using System;
using System.Collections;
using System.Collections.Concurrent;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

public class WebSocketHandler : MonoBehaviour
{
    public static WebSocketHandler Instance { get; private set; }

    [SerializeField] int port = 8080;
    [SerializeField] AgentController agentController;

    private WebSocketServer _wss;
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
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

    public AgentController Agent => agentController;

    void OnDestroy()
    {
        _wss?.Stop();
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
                WebSocketHandler.Instance.StartCoroutine(
                    ScreenshotUtility.GetScreenshotBase64(base64 => session.Send(base64))
                );
                break;

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
                Vector3 spawnPos = newAgent != null ? newAgent.transform.position : Vector3.zero;
                Vector3 spawnRot = newAgent != null ? newAgent.transform.eulerAngles : Vector3.zero;
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
                Camera originalCamera = GPUInstanceTracker.Instance.MainCamera;
                WebSocketHandler.Instance.StartCoroutine(ScreenshotRoutine(this, mpCamera, originalCamera));
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
            case "TransformAgent":
                agent.TransformAgent(ToVec3(cmd.translation), ToVec3(cmd.rotation));
                Send($"Agent position: {agent.transform.position}, rotation: {agent.transform.eulerAngles}");
                break;
            case "TranslateAgent":
                agent.TranslateAgent(ToVec3(cmd.translation), ToVec3(cmd.rotation));
                Send($"Agent position: {agent.transform.position}, rotation: {agent.transform.eulerAngles}");
                break;
            case "TransformHand":
                agent.TransformHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation));
                Send("Hand transformed");
                break;
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

    private static IEnumerator ScreenshotRoutine(SariMultiplayerBehavior session, Camera mpCamera, Camera originalCamera)
    {
        GPUInstanceTracker.Instance.SetCamera(mpCamera);
        yield return null; // let instancer dispatch with new frustum
        yield return ScreenshotUtility.GetScreenshotBase64(mpCamera, base64 =>
        {
            GPUInstanceTracker.Instance.SetCamera(originalCamera);
            session.Send(base64);
        });
    }

    private static float[] Vec3ToArr(Vector3 v) => new float[] { v.x, v.y, v.z };

    private static Vector3 ToVec3(float[] arr)
    {
        if (arr == null || arr.Length < 3) return Vector3.zero;
        return new Vector3(arr[0], arr[1], arr[2]);
    }
}
