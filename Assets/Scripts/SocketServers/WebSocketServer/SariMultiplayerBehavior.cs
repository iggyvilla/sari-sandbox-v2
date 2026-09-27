using System;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

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
        public int recoveryCount;
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

    // Only touched on the main thread; queue order guarantees a pending Join runs before OnClose's work.
    protected override void OnMessage(MessageEventArgs e)
    {
        MultiplayerCommandData cmd;
        try
        {
            cmd = JsonUtility.FromJson<MultiplayerCommandData>(e.Data);
        }
        catch (ArgumentException)
        {
            cmd = null; // malformed JSON throws rather than returning null
        }

        if (cmd == null) { Send("Error: invalid JSON"); return; }
        WebSocketHandler.Instance?.Enqueue(() => HandleCommand(cmd));
    }

    protected override void OnClose(CloseEventArgs e)
    {
        WebSocketHandler.Instance?.Enqueue(LeaveCurrentAgent);
    }

    /// <summary>Despawns this session's agent, if any, and tells the other sessions.</summary>
    private void LeaveCurrentAgent()
    {
        if (_agentId == null) return;
        MultiplayerAgentManager.Instance?.DespawnAgent(_agentId);
        Sessions.Broadcast(JsonUtility.ToJson(new AgentLeftMsg { agentId = _agentId }));
        _agentId = null;
    }

    private void HandleCommand(MultiplayerCommandData cmd)
    {
        switch (cmd.command)
        {
            case "Join":
            {
                // A repeat Join replaces this session's agent instead of orphaning it.
                LeaveCurrentAgent();
                string agentId = MultiplayerAgentManager.Instance.SpawnAgent();
                if (agentId == null) { Send("Error: failed to spawn multiplayer agent"); return; }
                _agentId = agentId;

                Send(JsonUtility.ToJson(new JoinedMsg { agentId = agentId }));

                foreach (AgentState s in MultiplayerAgentManager.Instance.GetSnapshot(agentId))
                    Send(JsonUtility.ToJson(new AgentStateMsg
                    {
                        type = "Snapshot",
                        agentId = s.agentId,
                        position = WireVec.ToArray(s.position),
                        rotation = WireVec.ToArray(s.rotation.eulerAngles),
                        recoveryCount = s.recoveryCount
                    }));

                AgentControllerBase newAgent = MultiplayerAgentManager.Instance.GetAgent(agentId);
                Vector3 spawnPos = newAgent != null ? newAgent.MovementRoot.position : Vector3.zero;
                Vector3 spawnRot = newAgent != null ? newAgent.ViewTransform.eulerAngles : Vector3.zero;
                Sessions.Broadcast(JsonUtility.ToJson(new AgentStateMsg
                {
                    type = "AgentSpawned",
                    agentId = agentId,
                    position = WireVec.ToArray(spawnPos),
                    rotation = WireVec.ToArray(spawnRot),
                    recoveryCount = newAgent != null ? newAgent.OutOfBoundsRecoveryCount : 0
                }));
                break;
            }

            case "RequestScreenshot":
            case "RequestLidarScan":
            case "RequestLidarCenter":
            {
                if (_agentId == null) { Send("Error: not joined"); return; }
                MultiplayerAgentManager manager = MultiplayerAgentManager.Instance;
                WebSocketHandler.Instance.EnqueueCapture(
                    cmd.command,
                    manager.GetAgentCamera(_agentId),
                    manager.GetGhostFollower(_agentId),
                    text => Send(text),
                    bytes => Send(bytes));
                break;
            }

            case "Chat":
            {
                if (_agentId == null) { Send("Error: not joined"); return; }
                if (string.IsNullOrEmpty(cmd.message)) { Send("Error: empty message"); return; }
                string chatLine = $"<{_agentId}> {cmd.message}";
                ChatUIManager.Instance?.Log(chatLine);
                MultiplayerAgentManager.Instance.GetAgent(_agentId)?.ShowChat(cmd.message);
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

                ExecuteMultiplayerAgentCommand(cmd, agent);

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

    private void ExecuteMultiplayerAgentCommand(MultiplayerCommandData cmd, AgentControllerBase agent)
    {
        switch (cmd.command)
        {
            // V1 name; it translates exactly like TranslateAgent.
            case "TransformAgent":
            case "TranslateAgent":
                Vector3 deltaTranslation = agent.ClampTranslationToMaximumHeight(
                    agent.EgocentricToWorldTranslation(WireVec.ToVector3(cmd.translation)));
                cmd.translation = WireVec.ToArray(deltaTranslation);
                agent.TranslateAgent(deltaTranslation, WireVec.ToVector3(cmd.rotation));
                Send($"Agent position: {agent.transform.position}, rotation: {agent.transform.eulerAngles}");
                break;
            // V1 name; it translates exactly like TranslateHand.
            case "TransformHand":
            case "TranslateHand":
            case "TranslateRightHand":
                agent.TranslateHand(WireVec.ToVector3(cmd.handPosition), WireVec.ToVector3(cmd.handRotation), AgentHandSide.Right);
                Send("Right hand translated");
                break;
            case "TranslateLeftHand":
                agent.TranslateHand(WireVec.ToVector3(cmd.handPosition), WireVec.ToVector3(cmd.handRotation), AgentHandSide.Left);
                Send("Left hand translated");
                break;
            case "TransformRightHand":
                agent.TransformHand(WireVec.ToVector3(cmd.handPosition), WireVec.ToVector3(cmd.handRotation), AgentHandSide.Right);
                Send("Right hand transformed");
                break;
            case "TransformLeftHand":
                agent.TransformHand(WireVec.ToVector3(cmd.handPosition), WireVec.ToVector3(cmd.handRotation), AgentHandSide.Left);
                Send("Left hand transformed");
                break;
            case "ResetHandPosition":
            case "ResetRightHandPosition":
                agent.ResetHandPosition(AgentHandSide.Right);
                Send("Right hand position reset");
                break;
            case "ResetLeftHandPosition":
                agent.ResetHandPosition(AgentHandSide.Left);
                Send("Left hand position reset");
                break;
            case "ToggleGrip":
            case "ToggleRightGrip":
                agent.ToggleGrip(AgentHandSide.Right);
                Send("Right grip toggled");
                break;
            case "ToggleLeftGrip":
                agent.ToggleGrip(AgentHandSide.Left);
                Send("Left grip toggled");
                break;
            case "TogglePoint":
            case "ToggleRightPoint":
                agent.TogglePoint(AgentHandSide.Right);
                Send("Right point toggled");
                break;
            case "ToggleLeftPoint":
                agent.TogglePoint(AgentHandSide.Left);
                Send("Left point toggled");
                break;
            default:
                Send($"Unknown command: {cmd.command}");
                break;
        }
    }
}
