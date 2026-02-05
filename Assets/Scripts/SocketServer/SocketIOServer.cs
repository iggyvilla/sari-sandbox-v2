using System;
using System.Collections.Generic;
using SocketIOClient;
using SocketIOClient.Newtonsoft.Json;
using UnityEngine;

public class SocketIOServer : MonoBehaviour
{
    // SocketIO server IP and port
    public string serverIP = "localhost";
    public int serverPort = 6060;

    public SocketIOUnity socket;

    public Camera agentCamera;
    
    void Start()
    {
        Debug.Log("Connecting to SocketIO server...");
        
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
            agentCamera.transform.position += agentCamera.transform.forward * amount;
        });
        
        socket.Connect();
    }

    void ServerLog(string msg)
    {
        Debug.Log($"socket >> {msg}");
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
