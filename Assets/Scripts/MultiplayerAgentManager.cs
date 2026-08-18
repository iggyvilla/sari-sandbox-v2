using System.Collections.Generic;
using UnityEngine;

public struct AgentState
{
    public string agentId;
    public Vector3 position;
    public Quaternion rotation;
    public int recoveryCount;
}

public class MultiplayerAgentManager : MonoBehaviour
{
    [System.Serializable]
    private sealed class AgentRecoveredMsg
    {
        public string type = "AgentRecovered";
        public string agentId;
        public string reason = "out_of_bounds";
        public int recoveryCount;
        public float[] position;
        public float[] rotation;
    }

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
        public HumanoidGhostFollower ghostFollower;
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

        HumanoidGhostFollower ghostFollower =
            HumanoidGhostFactory.Spawn(ikHumanoidGhostPrefab, controller);
        if (ghostFollower == null)
        {
            Destroy(vrAvatar);
            return null;
        }

        _agents[agentId] = new MultiplayerAgentRecord
        {
            vrAvatar = vrAvatar,
            controller = controller,
            camera = vrAvatar.GetComponentInChildren<Camera>(true),
            humanoidGhost = ghostFollower.gameObject,
            ghostFollower = ghostFollower
        };
        controller.OutOfBoundsRecovered += HandleAgentRecovered;

        Debug.Log($"Spawned multiplayer VR agent {agentId} with an IK humanoid ghost.");
        return agentId;
    }

    public void DespawnAgent(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out MultiplayerAgentRecord record)) return;
        if (record.controller != null)
            record.controller.OutOfBoundsRecovered -= HandleAgentRecovered;
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

    public HumanoidGhostFollower GetGhostFollower(string agentId)
    {
        return _agents.TryGetValue(agentId, out MultiplayerAgentRecord record)
            ? record.ghostFollower
            : null;
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
                rotation = kvp.Value.controller.ViewTransform.rotation,
                recoveryCount = kvp.Value.controller.OutOfBoundsRecoveryCount
            });
        }
        return result;
    }

    private void HandleAgentRecovered(
        AgentControllerBase recoveredController,
        int recoveryCount,
        Vector3 position,
        Quaternion rotation)
    {
        string recoveredAgentId = null;
        foreach (KeyValuePair<string, MultiplayerAgentRecord> pair in _agents)
        {
            if (pair.Value.controller != recoveredController) continue;
            recoveredAgentId = pair.Key;
            break;
        }

        if (recoveredAgentId == null) return;

        WebSocketHandler.Instance?.BroadcastMultiplayer(JsonUtility.ToJson(new AgentRecoveredMsg
        {
            agentId = recoveredAgentId,
            recoveryCount = recoveryCount,
            position = Vec3ToArr(position),
            rotation = Vec3ToArr(rotation.eulerAngles)
        }));
    }

    private static float[] Vec3ToArr(Vector3 value) =>
        new float[] { value.x, value.y, value.z };

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
