using System.Collections.Generic;
using UnityEngine;

public struct AgentState
{
    public string agentId;
    public Vector3 position;
    public Quaternion rotation;
}

public class MultiplayerAgentManager : MonoBehaviour
{
    public static MultiplayerAgentManager Instance { get; private set; }

    [SerializeField] private GameObject vrAgentPrefab;
    [SerializeField] private GameObject ikHumanoidGhostPrefab;
    [SerializeField] private Transform spawnPoint;

    private sealed class MultiplayerAgentRecord
    {
        public GameObject vrAvatar;
        public AgentController controller;
        public Camera camera;
        public GameObject humanoidGhost;
        public MultiplayerHumanoidGhost ghostFollower;
    }

    private readonly Dictionary<string, MultiplayerAgentRecord> _agents = new();
    private int _nextId = 1;

    void Awake() => Instance = this;

    public string SpawnAgent()
    {
        string agentId = (_nextId++).ToString();
        Vector3 pos = spawnPoint != null ? spawnPoint.position : Vector3.zero;
        Quaternion rot = spawnPoint != null ? spawnPoint.rotation : Quaternion.identity;

        if (vrAgentPrefab == null || ikHumanoidGhostPrefab == null)
        {
            Debug.LogError("MultiplayerAgentManager needs both the VR agent and IK humanoid ghost prefabs.");
            return null;
        }

        GameObject vrAvatar = Instantiate(vrAgentPrefab, pos, rot);
        AgentController controller = vrAvatar.GetComponentInChildren<AgentController>(true);
        if (controller == null)
        {
            Debug.LogError("The multiplayer VR prefab is missing an AgentController.");
            Destroy(vrAvatar);
            return null;
        }

        controller.isMultiplayerAgent = true;
        PrepareMultiplayerAuthority(vrAvatar, controller);

        GameObject humanoidGhost = Instantiate(ikHumanoidGhostPrefab, pos, rot);
        IKAgentController humanoidController = humanoidGhost.GetComponentInChildren<IKAgentController>(true);
        if (humanoidController == null)
        {
            Debug.LogError("The multiplayer IK humanoid prefab is missing an IKAgentController.");
            Destroy(vrAvatar);
            Destroy(humanoidGhost);
            return null;
        }

        MultiplayerHumanoidGhost ghostFollower = humanoidGhost.AddComponent<MultiplayerHumanoidGhost>();
        ghostFollower.Bind(controller, humanoidController);

        _agents[agentId] = new MultiplayerAgentRecord
        {
            vrAvatar = vrAvatar,
            controller = controller,
            camera = vrAvatar.GetComponentInChildren<Camera>(true),
            humanoidGhost = humanoidGhost,
            ghostFollower = ghostFollower
        };

        Debug.Log($"Spawned multiplayer VR agent {agentId} with an IK humanoid ghost.");
        return agentId;
    }

    public void DespawnAgent(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out MultiplayerAgentRecord record)) return;
        Destroy(record.vrAvatar);
        Destroy(record.humanoidGhost);
        _agents.Remove(agentId);
    }

    public AgentControllerBase GetAgent(string agentId)
    {
        if (_agents.TryGetValue(agentId, out MultiplayerAgentRecord record))
            return record.controller;
        return null;
    }

    public Camera GetAgentCamera(string agentId)
    {
        if (_agents.TryGetValue(agentId, out MultiplayerAgentRecord record))
            return record.camera;
        return null;
    }

    public void SetGhostVisibleForCapture(string agentId, bool visible)
    {
        if (_agents.TryGetValue(agentId, out MultiplayerAgentRecord record) &&
            record.ghostFollower != null)
        {
            record.ghostFollower.SetVisibleForCapture(visible);
        }
    }

    public List<AgentState> GetSnapshot(string excludeId = null)
    {
        var result = new List<AgentState>();
        foreach (var kvp in _agents)
        {
            if (kvp.Key == excludeId) continue;
            Transform movementRoot = kvp.Value.controller.MovementRoot;
            result.Add(new AgentState
            {
                agentId = kvp.Key,
                position = movementRoot.position,
                rotation = kvp.Value.controller.ViewTransform.rotation
            });
        }
        return result;
    }

    private static void PrepareMultiplayerAuthority(GameObject vrAvatar, AgentController controller)
    {
        foreach (Camera agentCamera in vrAvatar.GetComponentsInChildren<Camera>(true))
        {
            agentCamera.enabled = false;
            agentCamera.tag = "Untagged";
        }

        foreach (AudioListener listener in vrAvatar.GetComponentsInChildren<AudioListener>(true))
            listener.enabled = false;

        foreach (Behaviour behaviour in vrAvatar.GetComponentsInChildren<Behaviour>(true))
        {
            if (behaviour == controller) continue;

            string typeName = behaviour.GetType().FullName;
            if (typeName != null &&
                (typeName.EndsWith(".TrackedPoseDriver") ||
                 typeName.EndsWith(".InputActionManager") ||
                 typeName.EndsWith(".XROrigin")))
            {
                behaviour.enabled = false;
            }
        }
    }
}
