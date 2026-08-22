using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;
using WebSocketSharp;

/// <summary>
/// Registers this sandbox with the Distributed Sari Bench coordinator and keeps it in the pool.
///
/// The sandbox self-assigns its command port (see <see cref="SandboxNetwork.FindFreePort"/>) and
/// reports it up; the coordinator learns the reachable host from this connection's peer address, so
/// nothing here has to guess the machine's own IP. Set SARI_BENCH_ADVERTISED_HOST when the peer
/// address is wrong - behind NAT or inside a container with a published port.
/// </summary>
public class BenchCoordinatorClient : MonoBehaviour
{
    public const int SchemaVersion = 1;
    public const string AdvertisedHostEnvVar = "SARI_BENCH_ADVERTISED_HOST";

    private const float HeartbeatIntervalSeconds = 5f;
    private const float ReconnectMinSeconds = 1f;
    private const float ReconnectMaxSeconds = 30f;

#pragma warning disable CS0649 // assigned by JsonUtility
    [Serializable]
    private class InboundMessage
    {
        public string type;
        public string lease_id;
        public string lease_alias;
        public string sandbox_alias;
    }
#pragma warning restore CS0649

    [Serializable]
    private class HelloMessage
    {
        public string type = "sandbox.hello";
        public int schema_version = SchemaVersion;
        public string sandbox_id;
        public string advertised_host;
        public int port;
        public string state;
        public bool store_loaded;
        public bool v1_compatibility;
        public string unity_version;
        public string store_name;
    }

    [Serializable]
    private class HeartbeatMessage
    {
        public string type = "sandbox.heartbeat";
        public string sandbox_id;
    }

    [Serializable]
    private class StateMessage
    {
        public string type = "sandbox.state";
        public string sandbox_id;
        public string state;
        public bool store_loaded;
        public string lease_id;
    }

    private WebSocket _socket;
    private WebSocketHandler _handler;
    private string _url;
    private float _reconnectDelay = ReconnectMinSeconds;
    private bool _connected;
    private bool _shuttingDown;
    private string _activeLeaseId = string.Empty;
    private string _sandboxAlias = string.Empty;

#if UNITY_STANDALONE_WIN
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr window, string title);
#endif

    /// <summary>
    /// Attaches a client to <paramref name="handler"/> unless this build is not part of a fleet or
    /// no coordinator URL was configured.
    /// </summary>
    public static BenchCoordinatorClient AttachIfConfigured(WebSocketHandler handler)
    {
        if (!handler.IsBenchmarkBuild)
            return null;

        string url = handler.CoordinatorUrl;
        if (string.IsNullOrEmpty(url))
        {
            Debug.LogWarning(
                "This sandbox is flagged as a distributed benchmark build but no coordinator " +
                "URL is set, so it will not join a fleet.");
            return null;
        }

        BenchCoordinatorClient client = handler.gameObject.AddComponent<BenchCoordinatorClient>();
        client._handler = handler;
        client._url = url;
        return client;
    }

    void Start()
    {
        if (_handler == null || string.IsNullOrEmpty(_url))
        {
            enabled = false;
            return;
        }

        _handler.StateChanged += OnSandboxStateChanged;
        StartCoroutine(ConnectionLoop());
        StartCoroutine(HeartbeatLoop());
    }

    void OnDestroy()
    {
        _shuttingDown = true;
        if (_handler != null) _handler.StateChanged -= OnSandboxStateChanged;
        CloseSocket();
    }

    /// <summary>
    /// Keeps a connection to the coordinator up, reconnecting with backoff. A coordinator restart
    /// or a network blip must not take the sandbox permanently out of the pool.
    /// </summary>
    private IEnumerator ConnectionLoop()
    {
        while (!_shuttingDown)
        {
            if (!_connected)
            {
                Connect();
                yield return new WaitForSeconds(_reconnectDelay);
                if (!_connected)
                    _reconnectDelay = Mathf.Min(_reconnectDelay * 2f, ReconnectMaxSeconds);
            }
            else
            {
                yield return new WaitForSeconds(1f);
            }
        }
    }

    private void Connect()
    {
        CloseSocket();

        try
        {
            _socket = new WebSocket(_url);
        }
        catch (Exception error)
        {
            Debug.LogError($"Invalid coordinator URL '{_url}': {error.Message}");
            enabled = false;
            return;
        }

        // WebSocketSharp raises these on its own threads, so nothing here may touch Unity state
        // directly - everything hops back through the handler's main-thread queue.
        _socket.OnOpen += (_, __) => _handler.Enqueue(OnSocketOpen);
        _socket.OnMessage += (_, e) => _handler.Enqueue(() => OnSocketMessage(e.Data));
        _socket.OnClose += (_, e) => _handler.Enqueue(() => OnSocketClosed(e.Reason));
        _socket.OnError += (_, e) => _handler.Enqueue(() => Debug.LogWarning(
            $"Coordinator socket error: {e.Message}"));

        _socket.ConnectAsync();
    }

    private void OnSocketOpen()
    {
        _connected = true;
        _reconnectDelay = ReconnectMinSeconds;
        Debug.Log($"Registered with benchmark coordinator at {_url}");

        DataHandler data = DataHandler.Instance;
        Send(new HelloMessage
        {
            sandbox_id = _handler.SandboxId,
            advertised_host = ReadEnv(AdvertisedHostEnvVar),
            port = _handler.BoundPort,
            state = _handler.State.ToString(),
            store_loaded = data != null && data.StoreLoaded,
            v1_compatibility = _handler.SariSandboxV1CompatibilityLayer,
            unity_version = Application.unityVersion,
            store_name = data != null ? data.storeName : string.Empty
        });
    }

    private void OnSocketClosed(string reason)
    {
        if (!_connected) return;

        _connected = false;
        Debug.LogWarning($"Coordinator connection closed ({reason}); will reconnect.");
    }

    private void OnSocketMessage(string payload)
    {
        InboundMessage message;
        try
        {
            message = JsonUtility.FromJson<InboundMessage>(payload);
        }
        catch (Exception error)
        {
            Debug.LogWarning($"Unparseable coordinator message: {error.Message}");
            return;
        }

        if (message == null || string.IsNullOrEmpty(message.type)) return;

        switch (message.type)
        {
            case "coord.welcome":
                _sandboxAlias = message.sandbox_alias ?? string.Empty;
                ApplyWindowTitle();
                break;

            case "coord.lease":
                _activeLeaseId = message.lease_id ?? string.Empty;
                _handler.SetLeased(true);
                break;

            case "coord.reset":
                // The coordinator resets on release, so this is what guarantees the next attempt
                // starts clean even when the previous agent process died mid-run.
                _activeLeaseId = message.lease_id ?? string.Empty;
                _handler.SetLeased(false);
                _handler.BeginReset(() =>
                {
                    _activeLeaseId = string.Empty;
                    PublishState();
                });
                break;

            case "coord.release":
                _activeLeaseId = string.Empty;
                _handler.SetLeased(false);
                break;

            default:
                Debug.LogWarning($"Unknown coordinator message type: {message.type}");
                break;
        }
    }

    /// <summary>
    /// Beats every <see cref="HeartbeatIntervalSeconds"/> of wall-clock time. Unscaled deliberately:
    /// a reset that parks timeScale would otherwise stop the beat and get this sandbox evicted for
    /// being busy rather than for being dead.
    ///
    /// This still rides the main thread, so a long frame hitch pauses the beat - which is the point.
    /// A heartbeat that kept ticking from a background thread would attest to a Unity instance that
    /// may be wedged. The coordinator's timeout carries the slack for a legitimately slow frame
    /// (see HEARTBEAT_TIMEOUT_SECONDS in protocol.py); it is not this loop's job to hide one.
    /// </summary>
    private IEnumerator HeartbeatLoop()
    {
        WaitForSecondsRealtime interval = new WaitForSecondsRealtime(HeartbeatIntervalSeconds);
        while (!_shuttingDown)
        {
            if (_connected)
                Send(new HeartbeatMessage { sandbox_id = _handler.SandboxId });

            yield return interval;
        }
    }

    private void OnSandboxStateChanged(SandboxState state) => PublishState();

    private void PublishState()
    {
        if (!_connected) return;

        DataHandler data = DataHandler.Instance;
        Send(new StateMessage
        {
            sandbox_id = _handler.SandboxId,
            state = _handler.State.ToString(),
            store_loaded = data != null && data.StoreLoaded,
            lease_id = _activeLeaseId
        });
    }

    private void Send(object message)
    {
        if (_socket == null || _socket.ReadyState != WebSocketState.Open) return;

        try
        {
            _socket.Send(JsonUtility.ToJson(message));
        }
        catch (Exception error)
        {
            Debug.LogWarning($"Failed to send to coordinator: {error.Message}");
        }
    }

    private void ApplyWindowTitle()
    {
#if UNITY_STANDALONE_WIN
        if (string.IsNullOrEmpty(_sandboxAlias)) return;

        try
        {
            IntPtr window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (window != IntPtr.Zero)
                SetWindowText(window, $"{_sandboxAlias} | Sari Sandbox²");
        }
        catch (Exception error)
        {
            Debug.LogWarning($"Could not set distributed sandbox window title: {error.Message}");
        }
#endif
    }

    private void CloseSocket()
    {
        if (_socket == null) return;

        try
        {
            _socket.Close();
        }
        catch (Exception)
        {
            // Closing an already-dead socket is not worth surfacing.
        }

        _socket = null;
        _connected = false;
    }

    private static string ReadEnv(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
