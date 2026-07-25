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
        public float[] leftTranslation;
        public float[] leftRotation;
        public float[] rightTranslation;
        public float[] rightRotation;
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
        // True when an item is within grab range of the hand, i.e. Toggle*HandGrip would pick it up.
        // Deliberately a bool: the item id must not leak to the agent.
        public bool left_hand_can_grab;
        public bool left_hand_gripping;
        public float[] current_right_hand_position;
        public float[] current_right_hand_rotation;
        public bool right_hand_can_grab;
        public bool right_hand_gripping;
    }

    [Serializable]
    class SandboxStatusResponse
    {
        public string state;
        public string sandbox_id;
        public int port;
        public bool benchmark_build;
        public bool v1_compatibility;
    }

    /// <summary>
    /// Commands answered regardless of readiness. Everything else is parked while the environment
    /// boots or resets, so an agent that races a reset waits rather than seeing a garbled reply.
    /// </summary>
    private static bool IsAlwaysAllowed(string command) =>
        command == "GetStatus" || command == "ResetEnvironment";

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
        WebSocketHandler.Instance.Enqueue(() => Dispatch(cmd, session));
    }

    private static void Dispatch(CommandData cmd, SariAgentCommandBehavior session)
    {
        WebSocketHandler handler = WebSocketHandler.Instance;

        if (IsAlwaysAllowed(cmd.command))
        {
            HandleCommand(cmd, session);
            return;
        }

        bool parked = handler.ParkOrRun(
            () => HandleCommand(cmd, session),
            cmd.command,
            error => session.Send(error));

        if (!parked)
        {
            // The queue is full, so we cannot stay silent - an unanswered command blocks the
            // agent's recv() indefinitely.
            session.Send(
                $"Error: sandbox is {handler.State} and its pending-command queue is full " +
                $"(command '{cmd.command}' dropped).");
        }
    }

    private static void HandleCommand(CommandData cmd, SariAgentCommandBehavior session)
    {
        WebSocketHandler handler = WebSocketHandler.Instance;
        AgentController agent = handler.Agent;
        bool sariSandboxV1CompatibilityLayer = handler.SariSandboxV1CompatibilityLayer;

        switch (cmd.command)
        {
            case "TransformAgent":
                // if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                // if (!sariSandboxV1CompatibilityLayer) goto case "TranslateAgent";
                // Vector3 worldPosition = ToVec3(cmd.translation);
                // worldPosition.y = Mathf.Min(worldPosition.y, agent.MaximumMovementRootHeight);
                // agent.TransformAgent(worldPosition, ToVec3(cmd.rotation));
                // handler.EnqueueCoroutine(SendAgentStateAfterPhysics(
                //     agent,
                //     session,
                //     sariSandboxV1CompatibilityLayer));
                // break;

            case "TranslateAgent":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                Vector3 deltaTranslation = agent.ClampTranslationToMaximumHeight(
                    agent.EgocentricToWorldTranslation(ToVec3(cmd.translation)));
                agent.TranslateAgent(deltaTranslation, ToVec3(cmd.rotation));
                handler.EnqueueCoroutine(SendAgentStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "TransformHand":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                if (!sariSandboxV1CompatibilityLayer) goto case "TranslateHand";
                agent.TransformHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation), AgentHandSide.Right);
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "TransformRightHand":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TransformHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation), AgentHandSide.Right);
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "TransformLeftHand":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TransformHand(ToVec3(cmd.handPosition), ToVec3(cmd.handRotation), AgentHandSide.Left);
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;
            
            // The command is called TransformHands in the Sari V1
            // communication protocol, but it TRANSLATES, not transforms
            case "TransformHands":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TranslateHand(ToVec3(cmd.leftTranslation), ToVec3(cmd.leftRotation), AgentHandSide.Left);
                agent.TranslateHand(ToVec3(cmd.rightTranslation), ToVec3(cmd.rightRotation), AgentHandSide.Right);
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "TranslateHand":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TranslateHand(ToVec3(cmd.translation), ToVec3(cmd.rotation), AgentHandSide.Right);
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "TranslateRightHand":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TranslateHand(ToVec3(cmd.translation), ToVec3(cmd.rotation), AgentHandSide.Right);
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "TranslateLeftHand":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TranslateHand(ToVec3(cmd.translation), ToVec3(cmd.rotation), AgentHandSide.Left);
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "ResetHandPosition":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.ResetHandPosition(AgentHandSide.Right);
                if (sariSandboxV1CompatibilityLayer)
                {
                    session.Send("Hand position reset");
                    break;
                }
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "ResetRightHandPosition":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.ResetHandPosition(AgentHandSide.Right);
                if (sariSandboxV1CompatibilityLayer)
                {
                    session.Send("Right hand position reset");
                    break;
                }
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "ResetLeftHandPosition":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.ResetHandPosition(AgentHandSide.Left);
                if (sariSandboxV1CompatibilityLayer)
                {
                    session.Send("Left hand position reset");
                    break;
                }
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "IsHoldingItem":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                session.Send(agent.IsHoldingItem() ? "true" : "false");
                break;

            case "ToggleRightHandGrip":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.ToggleGrip(AgentHandSide.Right);
                if (sariSandboxV1CompatibilityLayer)
                {
                    session.Send("Right Grip: " + agent.IsGripped);
                    break;
                }
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "ToggleLeftHandGrip":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.ToggleGrip(AgentHandSide.Left);
                if (sariSandboxV1CompatibilityLayer)
                {
                    session.Send("Left Grip: " + agent.IsLeftGripped);
                    break;
                }
                handler.EnqueueCoroutine(SendHandStateAfterPhysics(
                    agent,
                    session,
                    sariSandboxV1CompatibilityLayer));
                break;

            case "ToggleRightPoke":
            case "ToggleRightPoint":
            case "TogglePoke":
            case "TogglePoint":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TogglePoint(AgentHandSide.Right);
                session.Send("Right Poke: " + agent.IsPointing);
                break;

            case "ToggleLeftPoke":
            case "ToggleLeftPoint":
                if (agent == null) { session.Send("Error: AgentController not assigned"); return; }
                agent.TogglePoint(AgentHandSide.Left);
                session.Send("Left Poke: " + agent.IsLeftPointing);
                break;

            case "RequestScreenshot":
            {
                Camera camera = handler.AgentCamera;
                if (camera == null) { session.Send("Error: no camera found for agent"); return; }
                handler.EnqueueScreenshot(camera, handler.AgentGhost, bytes => session.Send(bytes));
                break;
            }

            case "RequestLidarScan":
            {
                Camera camera = handler.AgentCamera;
                if (camera == null) { session.Send("Error: no camera found for agent"); return; }
                handler.EnqueueLidarScan(
                    camera,
                    handler.AgentGhost,
                    // WebSocketSharp.Send(byte[]) sends a binary frame. The bytes are the LDR1
                    // payload built in LidarSensor.BuildPayload, not JSON or base64 text.
                    bytes => session.Send(bytes),
                    error => session.Send(error));
                break;
            }

            case "RequestLidarCenter":
            {
                Camera camera = handler.AgentCamera;
                if (camera == null) { session.Send("Error: no camera found for agent"); return; }
                handler.EnqueueLidarCenterSample(
                    camera,
                    handler.AgentGhost,
                    sample => session.Send(JsonUtility.ToJson(sample)),
                    error => session.Send(error));
                break;
            }

            case "ResetEnvironment":
                // Answered only once the reset has genuinely settled. The old implementation acked
                // in the same tick, i.e. before Unity had even processed the deferred Destroy()
                // calls, which let state leak into whatever ran next.
                handler.BeginReset(() => session.Send("Environment reset"));
                break;

            case "GetStatus":
                // Always answered, whatever the state - this is how a benchmark runner polls a
                // sandbox that is still booting.
                session.Send(JsonUtility.ToJson(new SandboxStatusResponse
                {
                    state = handler.State.ToString(),
                    sandbox_id = handler.SandboxId,
                    port = handler.BoundPort,
                    benchmark_build = handler.IsBenchmarkBuild,
                    v1_compatibility = sariSandboxV1CompatibilityLayer
                }));
                break;

            case "WaitUntilReady":
                // Parked by Dispatch until the sandbox is ready, so reaching here means it is.
                session.Send("Ready");
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

        Transform reference = agent.ViewTransform;

        Transform leftHand = agent.LeftHandTransform;
        Vector3 leftHandPosition = GetRelativePosition(reference, leftHand);
        Vector3 leftHandRotation = GetRelativeRotation(reference, leftHand);
        string leftHandHoveredItemId = agent.LeftHandHoveredItemId;

        Transform rightHand = agent.RightHandTransform;
        Vector3 rightHandPosition = GetRelativePosition(reference, rightHand);
        Vector3 rightHandRotation = GetRelativeRotation(reference, rightHand);
        string rightHandHoveredItemId = agent.RightHandHoveredItemId;

        if (!sariSandboxV1CompatibilityLayer)
        {
            session.Send(JsonUtility.ToJson(new HandStateResponse
            {
                current_left_hand_position = Vec3ToArr(leftHandPosition),
                current_left_hand_rotation = Vec3ToArr(leftHandRotation),
                left_hand_can_grab = !string.IsNullOrEmpty(leftHandHoveredItemId),
                left_hand_gripping = agent.IsLeftGripped,
                current_right_hand_position = Vec3ToArr(rightHandPosition),
                current_right_hand_rotation = Vec3ToArr(rightHandRotation),
                right_hand_can_grab = !string.IsNullOrEmpty(rightHandHoveredItemId),
                right_hand_gripping = agent.IsGripped
            }));
            yield break;
        }

        session.Send(
            "Current left hand position: " + leftHandPosition +
            "\nCurrent left hand rotation: " + leftHandRotation +
            "\nLeft hand hovering: " + (leftHandHoveredItemId ?? "null") +
            "\nLeft hand gripping: " + agent.IsLeftGripped +
            "\nCurrent right hand position: " +
            "\nCurrent right hand position: " + rightHandPosition +
            "\nCurrent right hand rotation: " + rightHandRotation +
            "\nRight hand hovering: " + (rightHandHoveredItemId ?? "null") +
            "\nRight hand gripping: " + agent.IsGripped);
    }

    private static float[] Vec3ToArr(Vector3 v) => new float[] { v.x, v.y, v.z };

    private static Vector3 GetRelativePosition(Transform reference, Transform target)
    {
        if (target == null) return Vector3.zero;
        return reference != null ? reference.InverseTransformPoint(target.position) : target.position;
    }

    private static Vector3 GetRelativeRotation(Transform reference, Transform target)
    {
        if (target == null) return Vector3.zero;
        Quaternion relativeRotation = reference != null
            ? Quaternion.Inverse(reference.rotation) * target.rotation
            : target.rotation;
        return relativeRotation.eulerAngles;
    }

    private static Vector3 ToVec3(float[] arr)
    {
        if (arr == null || arr.Length < 3) return Vector3.zero;
        return new Vector3(arr[0], arr[1], arr[2]);
    }
}
