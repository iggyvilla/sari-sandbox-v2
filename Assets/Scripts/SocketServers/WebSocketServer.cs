using System;
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
        _wss.AddWebSocketService<CommandBehavior>("/commands");
        _wss.Start();
        Debug.Log($"WebSocket server started on ws://localhost:{port}/commands");
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

public class CommandBehavior : WebSocketBehavior
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

        CommandBehavior session = this;
        WebSocketHandler.Instance.Enqueue(() => HandleCommand(cmd, session));
    }

    private static void HandleCommand(CommandData cmd, CommandBehavior session)
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

            case "ToggleGrip":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.ToggleGrip();
                session.Send("Grip toggled");
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
