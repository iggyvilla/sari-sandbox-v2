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
                    bytes => Send(bytes));
                break;
            }

            case "RequestLidarScan":
            {
                if (_agentId == null) { Send("Error: not joined"); return; }
                Camera mpCamera = MultiplayerAgentManager.Instance.GetAgentCamera(_agentId);
                if (mpCamera == null) { Send("Error: no camera found for agent"); return; }
                WebSocketHandler.Instance.EnqueueLidarScan(
                    mpCamera,
                    MultiplayerAgentManager.Instance.GetGhostFollower(_agentId),
                    // WebSocketSharp.Send(byte[]) sends a binary frame. The bytes are the LDR1
                    // payload built in LidarSensor.BuildPayload, not JSON or base64 text.
                    bytes => Send(bytes),
                    error => Send(error));
                break;
            }

            case "RequestLidarCenter":
            {
                if (_agentId == null) { Send("Error: not joined"); return; }
                Camera mpCamera = MultiplayerAgentManager.Instance.GetAgentCamera(_agentId);
                if (mpCamera == null) { Send("Error: no camera found for agent"); return; }
                WebSocketHandler.Instance.EnqueueLidarCenterSample(
                    mpCamera,
                    MultiplayerAgentManager.Instance.GetGhostFollower(_agentId),
                    sample => Send(JsonUtility.ToJson(sample)),
                    error => Send(error));
                break;
            }

            case "Chat":
            {
                if (_agentId == null) { Send("Error: not joined"); return; }
                if (string.IsNullOrEmpty(cmd.message)) { Send("Error: empty message"); return; }
                string chatLine = $"<{_agentId}> {cmd.message}";
                ChatUIManager.Instance.Log(chatLine);
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
            // Semantically, transform is very different from translate,
            // but Sari Sandbox v1 used TransformAgent as the command even if
            // it translates. This is only here for compatability.
            case "TransformAgent":
                // agent.TransformAgent(ToVec3(cmd.translation), ToVec3(cmd.rotation));
                // Send($"Agent position: {agent.transform.position}, rotation: {agent.transform.eulerAngles}");
                // break;
            case "TranslateAgent":
                Vector3 deltaTranslation = agent.ClampTranslationToMaximumHeight(
                    agent.EgocentricToWorldTranslation(ToVec3(cmd.translation)));
                cmd.translation = Vec3ToArr(deltaTranslation);
                agent.TranslateAgent(deltaTranslation, ToVec3(cmd.rotation));
                Send($"Agent position: {agent.transform.position}, rotation: {agent.transform.eulerAngles}");
                break;
            case "TransformHand":
                // agent.TransformHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation));
                // Send("Hand transformed");
                // break;
            case "TranslateHand":
                agent.TranslateHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation), AgentHandSide.Right);
                Send("Right hand translated");
                break;
            case "TransformRightHand":
                agent.TransformHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation), AgentHandSide.Right);
                Send("Right hand transformed");
                break;
            case "TransformLeftHand":
                agent.TransformHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation), AgentHandSide.Left);
                Send("Left hand transformed");
                break;
            case "TranslateRightHand":
                agent.TranslateHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation), AgentHandSide.Right);
                Send("Right hand translated");
                break;
            case "TranslateLeftHand":
                agent.TranslateHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation), AgentHandSide.Left);
                Send("Left hand translated");
                break;
            case "ResetHandPosition":
                agent.ResetHandPosition(AgentHandSide.Right);
                Send("Right hand position reset");
                break;
            case "ResetRightHandPosition":
                agent.ResetHandPosition(AgentHandSide.Right);
                Send("Right hand position reset");
                break;
            case "ResetLeftHandPosition":
                agent.ResetHandPosition(AgentHandSide.Left);
                Send("Left hand position reset");
                break;
            case "ToggleGrip":
                agent.ToggleGrip(AgentHandSide.Right);
                Send("Right grip toggled");
                break;
            case "ToggleRightGrip":
                agent.ToggleGrip(AgentHandSide.Right);
                Send("Right grip toggled");
                break;
            case "ToggleLeftGrip":
                agent.ToggleGrip(AgentHandSide.Left);
                Send("Left grip toggled");
                break;
            case "TogglePoint":
                agent.TogglePoint(AgentHandSide.Right);
                Send("Right point toggled");
                break;
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

    private static float[] Vec3ToArr(Vector3 v) => new float[] { v.x, v.y, v.z };

    private static Vector3 ToVec3(float[] arr)
    {
        if (arr == null || arr.Length < 3) return Vector3.zero;
        return new Vector3(arr[0], arr[1], arr[2]);
    }
}
