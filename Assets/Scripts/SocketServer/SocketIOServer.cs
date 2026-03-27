using System;
using System.Collections.Generic;
using SocketIOClient;
using SocketIOClient.Newtonsoft.Json;
using UnityEngine;
using UnityEngine.AI;

[Serializable]
struct NavMeshPathCorners
{
    public List<List<float>> corners;
}

public class SocketIOServer : MonoBehaviour
{
    // SocketIO server IP and port
    public string serverIP = "localhost";
    public int serverPort = 6060;

    public SocketIOUnity socket;

    public GameObject agentGameObject;

    private bool isWalkingToPath = false;
    private int currentCorner = 0;
    private List<List<float>> pathCorners = new();
    private NavMeshAgent agent;
    
    void Start()
    {
        Debug.Log("Connecting to SocketIO server...");
        
        agent = agentGameObject.GetComponent<NavMeshAgent>();
        
        var uri = new Uri($"http://{serverIP}:{serverPort}");
        socket = new SocketIOUnity(uri, new SocketIOOptions
        {
            Query = new Dictionary<string, string>
            {
                {"token", "UNITY" }
            },
            EIO = EngineIO.V4,
            Transport = SocketIOClient.Transport.TransportProtocol.WebSocket
        });
        
        socket.JsonSerializer = new NewtonsoftJsonSerializer();
        
        socket.OnConnected += (sender, e) =>
        {
            ServerLog("Connected to SocketIO server.");
        };
        
        socket.unityThreadScope = SocketIOUnity.UnityThreadScope.Update; 
        socket.OnUnityThread("MOVE_FWD", (data) =>
        {
            float amount = data.GetValue<float>();
            ServerLog($"recv MOVE_FWD({amount})");
            agentGameObject.transform.position += agentGameObject.transform.forward * amount;
        });
        
        socket.OnUnityThread("SARI_AGENT_UPDATE_PATH", (data) =>
        {
            NavMeshPathCorners corners = data.GetValue<NavMeshPathCorners>();

            isWalkingToPath = true;
            pathCorners = corners.corners;
            currentCorner = 0;
            
            agent.SetDestination(
                GetCornerVec(currentCorner)
            );
        });
        
        socket.Connect();
    }

    void Update()
    {
        if (isWalkingToPath)
        {
            Vector3 cornerVec = GetCornerVec(currentCorner);
            
            // If we're at the point, move to the next corner
            if (Vector3.Distance(agent.transform.position, cornerVec) < 0.5)
            {
                if (currentCorner < pathCorners.Count - 1)
                {
                    currentCorner += 1;
                    
                    agent.SetDestination(
                        GetCornerVec(currentCorner)
                    );
                }
                else
                {
                    Debug.Log($"currentCorner={currentCorner}");
                    Debug.Log($"no of corners: {pathCorners.Count}");
                    isWalkingToPath = false;
                }
            }
        }
    }

    void ServerLog(string msg)
    {
        Debug.Log($"socket >> {msg}");
    }

    Vector3 GetCornerVec(int currentCorner)
    {
        return new Vector3(
            pathCorners[currentCorner][0],
            pathCorners[currentCorner][1],
            pathCorners[currentCorner][2]
        );
    }
    
    async void OnApplicationQuit()
    {
        if (socket != null && socket.Connected) 
        {
            await socket.DisconnectAsync();
        }
        socket?.Dispose();
    }
}
