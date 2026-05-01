using System;
using System.Collections;
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
    
    public GameObject debugCanvas;
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
                {"sari_instance", "UNITY" }
            },
            EIO = EngineIO.V4,
            Transport = SocketIOClient.Transport.TransportProtocol.WebSocket
        });
        
        socket.JsonSerializer = new NewtonsoftJsonSerializer();
        socket.unityThreadScope = SocketIOUnity.UnityThreadScope.Update; 
        
        socket.OnConnected += (sender, e) =>
        {
            ServerLog("Connected to SocketIO server.");
        };
        
        socket.OnUnityThread("MOVE_FWD", (data) =>
        {
            float amount = data.GetValue<float>();
            ServerLog($"recv MOVE_FWD({amount})");
            agentGameObject.transform.position += agentGameObject.transform.forward * amount;
            StartCoroutine(GetScreenshotBase64((base64) =>
                {
                    socket.Emit("UNITY_RESPONSE", base64);
                })
            );
        });
        
        socket.OnUnityThread("GET_VIEW", (data) =>
        {
            ServerLog($"recv GET_VIEW()");
            StartCoroutine(GetScreenshotBase64((base64) =>
                {
                    socket.Emit("UNITY_RESPONSE", base64);
                })
            );
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
    
    public IEnumerator GetScreenshotBase64(Action<string> callback)
    {
        // 1. Wait until the end of the frame 
        // This ensures all draw calls (including procedural ones) are finished
        yield return new WaitForEndOfFrame();
        
        /* Due to Unity's asynchronous nature, the render might capture the
         * movement BEFORE a movement command. We want it to capture AFTER,
         * hence this delay */
        yield return new WaitForSeconds(0.5f);

        // 2. Capture the current screen buffer
        Texture2D screenShot = ScreenCapture.CaptureScreenshotAsTexture();

        // 3. Convert to Base64
        byte[] bytes = screenShot.EncodeToPNG();
        string base64String = Convert.ToBase64String(bytes);

        // 4. Clean up memory
        Destroy(screenShot);

        // 5. Return result via callback
        callback?.Invoke(base64String);
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
