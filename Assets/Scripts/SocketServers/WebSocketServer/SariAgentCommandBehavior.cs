using System;
using System.Collections;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

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

    [Serializable]
    class AgentStateResponse
    {
        public float[] current_position;
        public float[] current_rotation;
        public bool collision;
    }

    [Serializable]
    class HandStateResponse
    {
        public float[] current_left_hand_position;
        public float[] current_left_hand_rotation;
        public string left_hand_hovering;
        public bool left_hand_gripping;
        public float[] current_right_hand_position;
        public float[] current_right_hand_rotation;
        public string right_hand_hovering;
        public bool right_hand_gripping;
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
        WebSocketHandler handler = WebSocketHandler.Instance;
        AgentController agent = handler.Agent;
        bool sariSandboxV1CompatibilityLayer = handler.SariSandboxV1CompatibilityLayer;

        switch (cmd.command)
        {
            case "TransformAgent":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                if (!sariSandboxV1CompatibilityLayer) goto case "TranslateAgent";
                Vector3 worldPosition = ToVec3(cmd.translation);
                worldPosition.y = Mathf.Min(worldPosition.y, agent.MaximumMovementRootHeight);
                agent.TransformAgent(worldPosition, ToVec3(cmd.rotation));
                handler.EnqueueCoroutine(SendAgentStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "TranslateAgent":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                Vector3 deltaTranslation = agent.ClampTranslationToMaximumHeight(ToVec3(cmd.translation));
                agent.TranslateAgent(deltaTranslation, ToVec3(cmd.rotation));
                handler.EnqueueCoroutine(SendAgentStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "TransformHand":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                if (!sariSandboxV1CompatibilityLayer) goto case "TranslateHand";
                agent.TransformHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation));
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "TranslateHand":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TranslateHand(ToVec3(cmd.translation), ToVec3(cmd.rotation));
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
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

            case "ToggleRightGrip":
            case "ToggleGrip":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.ToggleGrip();
                session.Send("Right Grip: " + agent.IsGripped);
                break;

            case "ToggleRightPoke":
            case "TogglePoke":
            case "TogglePoint":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TogglePoint();
                session.Send("Right Poke: " + agent.IsPointing);
                break;

            case "RequestScreenshot":
            {
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

    private static IEnumerator SendAgentStateAfterPhysics(
        AgentControllerBase agent,
        SariAgentCommandBehavior session,
        bool sariSandboxV1CompatibilityLayer)
    {
        yield return new WaitForFixedUpdate();
        yield return null;

        if (agent == null) yield break;

        Transform view = agent.ViewTransform;
        if (!sariSandboxV1CompatibilityLayer)
        {
            session.Send(JsonUtility.ToJson(new AgentStateResponse
            {
                current_position = Vec3ToArr(view.position),
                current_rotation = Vec3ToArr(view.rotation.eulerAngles),
                collision = agent.IsAgentColliding
            }));
            yield break;
        }

        session.Send(
            "Current position: " + view.position +
            "\nCurrent rotation: " + view.rotation.eulerAngles +
            "\nCollision: " + agent.IsAgentColliding);
    }

    private static IEnumerator SendHandStateAfterPhysics(
        AgentControllerBase agent,
        SariAgentCommandBehavior session,
        bool sariSandboxV1CompatibilityLayer)
    {
        yield return new WaitForFixedUpdate();
        yield return null;

        if (agent == null) yield break;

        Transform rightHand = agent.HandTransform;
        Vector3 rightHandPosition = rightHand != null ? rightHand.position : Vector3.zero;
        Vector3 rightHandRotation = rightHand != null ? rightHand.rotation.eulerAngles : Vector3.zero;
        string rightHandHoveredItemId = agent.RightHandHoveredItemId;

        if (!sariSandboxV1CompatibilityLayer)
        {
            session.Send(SerializeHandStateResponse(new HandStateResponse
            {
                current_left_hand_position = Vec3ToArr(Vector3.zero),
                current_left_hand_rotation = Vec3ToArr(Vector3.zero),
                left_hand_hovering = null,
                left_hand_gripping = false,
                current_right_hand_position = Vec3ToArr(rightHandPosition),
                current_right_hand_rotation = Vec3ToArr(rightHandRotation),
                right_hand_hovering = rightHandHoveredItemId,
                right_hand_gripping = agent.IsGripped
            }));
            yield break;
        }

        session.Send(
            "Current left hand position: " + Vector3.zero +
            "\nCurrent left hand rotation: " + Vector3.zero +
            "\nLeft hand hovering: null" +
            "\nLeft hand gripping: False" +
            "\nCurrent right hand position: " + rightHandPosition +
            "\nCurrent right hand rotation: " + rightHandRotation +
            "\nRight hand hovering: " + (rightHandHoveredItemId ?? "null") +
            "\nRight hand gripping: " + agent.IsGripped);
    }

    private static string SerializeHandStateResponse(HandStateResponse response)
    {
        string json = JsonUtility.ToJson(response);

        // JsonUtility can serialize null strings as empty strings under Unity serialization rules.
        if (response.left_hand_hovering == null)
            json = json.Replace("\"left_hand_hovering\":\"\"", "\"left_hand_hovering\":null");
        if (response.right_hand_hovering == null)
            json = json.Replace("\"right_hand_hovering\":\"\"", "\"right_hand_hovering\":null");

        return json;
    }

    private static float[] Vec3ToArr(Vector3 v) => new float[] { v.x, v.y, v.z };

    private static Vector3 ToVec3(float[] arr)
    {
        if (arr == null || arr.Length < 3) return Vector3.zero;
        return new Vector3(arr[0], arr[1], arr[2]);
    }
}
