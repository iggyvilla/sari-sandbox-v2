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

        // Optional y rotation (in degrees) the agent should face after ResetEnvironment. JsonUtility
        // leaves fields absent from the payload at their initialised value, so NaN means "not sent"
        // and the reset falls back to its default facing.
        public float degrees = float.NaN;
    }

    [Serializable]
    class AgentStateResponse
    {
        public float[] current_position;
        public float[] current_rotation;
        public bool collision;
        public int out_of_bounds_recovery_count;
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
        // Separate physical attachment from the grip toggle. A closed hand can be empty after a
        // failed grab or an unexpected detach; treating that as occupied deadlocks later grabs.
        public bool left_hand_holding_item;
        public float[] current_right_hand_position;
        public float[] current_right_hand_rotation;
        public bool right_hand_can_grab;
        public bool right_hand_gripping;
        public bool right_hand_holding_item;
    }

    [Serializable]
    class SandboxStatusResponse
    {
        public string state;
        public string sandbox_id;
        public int port;
        public bool benchmark_build;
        public bool v1_compatibility;
        public string active_queued_command;
        public float active_queued_command_age_seconds;
        public int queued_command_count;
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

        CommandData cmd;
        try
        {
            cmd = JsonUtility.FromJson<CommandData>(e.Data);
        }
        catch (ArgumentException)
        {
            cmd = null; // malformed JSON throws rather than returning null
        }

        if (cmd == null)
        {
            Send("Error: invalid JSON");
            return;
        }

        SariAgentCommandBehavior session = this;
        WebSocketHandler.Instance?.Enqueue(() => Dispatch(cmd, session));
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
        bool v1 = handler.SariSandboxV1CompatibilityLayer;
        Action<string> sendText = text => session.Send(text);

        // Checked before mutating the agent so a rejected command leaves it untouched.
        bool CanRun(bool queuesReply = true)
        {
            if (agent == null)
            {
                session.Send("Error: AgentController not assigned");
                return false;
            }
            return !queuesReply || handler.HasCoroutineCapacity(cmd.command, sendText);
        }

        void ReplyHandState() => handler.EnqueueCoroutine(
            cmd.command, SendHandStateAfterPhysics(agent, session, v1), sendText);

        switch (cmd.command)
        {
            // V1 name; it translates exactly like TranslateAgent.
            case "TransformAgent":
            case "TranslateAgent":
                if (!CanRun()) return;
                agent.TranslateAgent(
                    agent.ClampTranslationToMaximumHeight(
                        agent.EgocentricToWorldTranslation(WireVec.ToVector3(cmd.translation))),
                    WireVec.ToVector3(cmd.rotation));
                handler.EnqueueCoroutine(
                    cmd.command, SendAgentStateAfterPhysics(agent, session, v1), sendText);
                break;

            case "TransformHand":
                if (!v1) goto case "TranslateHand";
                goto case "TransformRightHand";

            case "TransformRightHand":
            case "TransformLeftHand":
                if (!CanRun()) return;
                agent.TransformHand(WireVec.ToVector3(cmd.handPosition), WireVec.ToVector3(cmd.handRotation), SideOf(cmd.command));
                ReplyHandState();
                break;

            // The command is called TransformHands in the Sari V1
            // communication protocol, but it TRANSLATES, not transforms
            case "TransformHands":
                if (!CanRun()) return;
                agent.TranslateHand(WireVec.ToVector3(cmd.leftTranslation), WireVec.ToVector3(cmd.leftRotation), AgentHandSide.Left);
                agent.TranslateHand(WireVec.ToVector3(cmd.rightTranslation), WireVec.ToVector3(cmd.rightRotation), AgentHandSide.Right);
                ReplyHandState();
                break;

            case "TranslateHand":
            case "TranslateRightHand":
            case "TranslateLeftHand":
                if (!CanRun()) return;
                agent.TranslateHand(WireVec.ToVector3(cmd.translation), WireVec.ToVector3(cmd.rotation), SideOf(cmd.command));
                ReplyHandState();
                break;

            case "ResetHandPosition":
            case "ResetRightHandPosition":
            case "ResetLeftHandPosition":
                if (!CanRun(!v1)) return;
                agent.ResetHandPosition(SideOf(cmd.command));
                if (v1)
                {
                    session.Send(cmd.command switch
                    {
                        "ResetHandPosition" => "Hand position reset",
                        "ResetLeftHandPosition" => "Left hand position reset",
                        _ => "Right hand position reset"
                    });
                    break;
                }
                ReplyHandState();
                break;

            case "ResetHands":
                if (!CanRun()) return;
                agent.ResetHandPosition(AgentHandSide.Left);
                agent.ResetHandPosition(AgentHandSide.Right);
                ReplyHandState();
                break;

            case "IsHoldingItem":
                if (!CanRun(false)) return;
                session.Send(agent.IsHoldingItem() ? "true" : "false");
                break;

            case "ToggleRightHandGrip":
            case "ToggleLeftHandGrip":
            {
                if (!CanRun(!v1)) return;
                AgentHandSide side = SideOf(cmd.command);
                agent.ToggleGrip(side);
                if (v1)
                {
                    session.Send(side == AgentHandSide.Left
                        ? "Left Grip: " + agent.IsLeftGripped
                        : "Right Grip: " + agent.IsGripped);
                    break;
                }
                ReplyHandState();
                break;
            }

            case "ToggleRightPoke":
            case "ToggleRightPoint":
            case "TogglePoke":
            case "TogglePoint":
            case "ToggleLeftPoke":
            case "ToggleLeftPoint":
            {
                if (!CanRun(false)) return;
                AgentHandSide side = SideOf(cmd.command);
                agent.TogglePoint(side);
                session.Send(side == AgentHandSide.Left
                    ? "Left Poke: " + agent.IsLeftPointing
                    : "Right Poke: " + agent.IsPointing);
                break;
            }

            case "RequestScreenshot":
            case "RequestLidarScan":
            case "RequestLidarCenter":
                handler.EnqueueCapture(
                    cmd.command,
                    handler.AgentCamera,
                    handler.AgentGhost,
                    sendText,
                    bytes => session.Send(bytes));
                break;

            case "ResetEnvironment":
                // Answered only once the reset has genuinely settled. The old implementation acked
                // in the same tick, i.e. before Unity had even processed the deferred Destroy()
                // calls, which let state leak into whatever ran next.
                handler.BeginReset(
                    () => session.Send("Environment reset"),
                    float.IsNaN(cmd.degrees) ? (float?)null : cmd.degrees);
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
                    v1_compatibility = v1,
                    active_queued_command = handler.ActiveQueuedCommand,
                    active_queued_command_age_seconds = handler.ActiveQueuedCommandAgeSeconds,
                    queued_command_count = handler.QueuedCommandCount
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

    /// <summary>Hand named by the command; unqualified commands address the right hand.</summary>
    private static AgentHandSide SideOf(string command) =>
        command.Contains("Left") ? AgentHandSide.Left : AgentHandSide.Right;

    private static IEnumerator SendAgentStateAfterPhysics(
        AgentControllerBase agent,
        SariAgentCommandBehavior session,
        bool sariSandboxV1CompatibilityLayer)
    {
        yield return new WaitForFixedUpdate();
        yield return null;

        if (agent == null) yield break;

        // MovePosition is applied by physics after FixedUpdate. Check once more here so the same
        // command that crossed the boundary returns the authoritative spawn pose and new count,
        // rather than leaking one stale frame to the client.
        agent.RecoverIfOutOfBounds();

        Transform view = agent.ViewTransform;
        if (!sariSandboxV1CompatibilityLayer)
        {
            session.Send(JsonUtility.ToJson(new AgentStateResponse
            {
                current_position = WireVec.ToArray(view.position),
                current_rotation = WireVec.ToArray(view.rotation.eulerAngles),
                collision = agent.IsAgentColliding,
                out_of_bounds_recovery_count = agent.OutOfBoundsRecoveryCount
            }));
            yield break;
        }

        session.Send(FormatV1AgentState(
            view.position,
            view.rotation.eulerAngles,
            agent.IsAgentColliding,
            agent.OutOfBoundsRecoveryCount));
    }

    public static string FormatV1AgentState(
        Vector3 position,
        Vector3 rotation,
        bool collision,
        int recoveryCount)
    {
        return
            "Current position: " + position +
            "\nCurrent rotation: " + rotation +
            "\nCollision: " + collision +
            "\nOut-of-bounds recovery count: " + recoveryCount;
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
                current_left_hand_position = WireVec.ToArray(leftHandPosition),
                current_left_hand_rotation = WireVec.ToArray(leftHandRotation),
                left_hand_can_grab = !string.IsNullOrEmpty(leftHandHoveredItemId),
                left_hand_gripping = agent.IsLeftGripped,
                left_hand_holding_item = agent.IsHoldingItem(AgentHandSide.Left),
                current_right_hand_position = WireVec.ToArray(rightHandPosition),
                current_right_hand_rotation = WireVec.ToArray(rightHandRotation),
                right_hand_can_grab = !string.IsNullOrEmpty(rightHandHoveredItemId),
                right_hand_gripping = agent.IsGripped,
                right_hand_holding_item = agent.IsHoldingItem(AgentHandSide.Right)
            }));
            yield break;
        }

        session.Send(
            "Current left hand position: " + leftHandPosition +
            "\nCurrent left hand rotation: " + leftHandRotation +
            "\nLeft hand hovering: " + (leftHandHoveredItemId ?? "null") +
            "\nLeft hand gripping: " + agent.IsLeftGripped +
            // Deliberate V1 quirk: clients expect this empty line before the real one.
            "\nCurrent right hand position: " +
            "\nCurrent right hand position: " + rightHandPosition +
            "\nCurrent right hand rotation: " + rightHandRotation +
            "\nRight hand hovering: " + (rightHandHoveredItemId ?? "null") +
            "\nRight hand gripping: " + agent.IsGripped +
            "\nLeft hand holding item: " + agent.IsHoldingItem(AgentHandSide.Left) +
            "\nRight hand holding item: " + agent.IsHoldingItem(AgentHandSide.Right));
    }

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
}
