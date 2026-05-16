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

    [SerializeField] private GameObject ikHumanoidPrefab;
    [SerializeField] private Transform spawnPoint;

    private readonly Dictionary<string, GameObject> _agents = new();
    private int _nextId = 1;

    void Awake() => Instance = this;

    public string SpawnAgent()
    {
        string agentId = (_nextId++).ToString();
        Vector3 pos = spawnPoint != null ? spawnPoint.position : Vector3.zero;
        Quaternion rot = spawnPoint != null ? spawnPoint.rotation : Quaternion.identity;
        GameObject go = Instantiate(ikHumanoidPrefab, pos, rot);
        go.GetComponent<AgentControllerBase>().isMultiplayerAgent = true;
        Debug.Log("Spaned ikHumanoid");
        _agents[agentId] = go;
        return agentId;
    }

    public void DespawnAgent(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out GameObject go)) return;
        Destroy(go);
        _agents.Remove(agentId);
    }

    public AgentControllerBase GetAgent(string agentId)
    {
        if (_agents.TryGetValue(agentId, out GameObject go))
            return go.GetComponent<AgentControllerBase>();
        return null;
    }

    public Camera GetAgentCamera(string agentId)
    {
        if (_agents.TryGetValue(agentId, out GameObject go))
            return go.GetComponentInChildren<Camera>();
        return null;
    }

    public List<AgentState> GetSnapshot(string excludeId = null)
    {
        var result = new List<AgentState>();
        foreach (var kvp in _agents)
        {
            if (kvp.Key == excludeId) continue;
            result.Add(new AgentState
            {
                agentId = kvp.Key,
                position = kvp.Value.transform.position,
                rotation = kvp.Value.transform.rotation
            });
        }
        return result;
    }
}
