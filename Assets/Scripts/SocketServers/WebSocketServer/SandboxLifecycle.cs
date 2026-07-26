using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

/// <summary>
/// Readiness of the sandbox as a whole. Distributed Sari Bench leases a sandbox only while it is
/// <see cref="Ready"/>, and commands that arrive in any other state are parked rather than
/// answered with an error - see <see cref="PendingCommandQueue"/>.
/// </summary>
public enum SandboxState
{
    Booting,
    Resetting,
    Ready,
    Leased
}

/// <summary>
/// Holds commands that arrived while the sandbox was booting or resetting and replays them, in
/// arrival order, once it is ready.
///
/// Parking rather than rejecting is deliberate. The benchmark agent speaks the V1 TEXT protocol
/// (see sim/env.py `_v1_or_raise`), which parses replies positionally - a "busy" reply, in any
/// shape, aborts the run. Leaving the request unanswered simply blocks the agent's `recv()`, which
/// is exactly the "wait instead of crashing" behaviour we want. The timeout is the backstop for a
/// reset that never finishes: there we do answer, so the agent gets an actionable error instead of
/// hanging forever.
/// </summary>
public class PendingCommandQueue
{
    private readonly struct Entry
    {
        public readonly Action Execute;
        public readonly Action<string> OnTimeout;
        public readonly float ParkedAt;
        public readonly string Command;

        public Entry(Action execute, Action<string> onTimeout, float parkedAt, string command)
        {
            Execute = execute;
            OnTimeout = onTimeout;
            ParkedAt = parkedAt;
            Command = command;
        }
    }

    private readonly Queue<Entry> _entries = new();
    private readonly int _capacity;
    private readonly float _timeoutSeconds;

    public PendingCommandQueue(int capacity = 64, float timeoutSeconds = 120f)
    {
        _capacity = capacity;
        _timeoutSeconds = timeoutSeconds;
    }

    public int Count => _entries.Count;

    /// <summary>
    /// Parks a command until the sandbox is ready. Returns false when the queue is full, in which
    /// case the caller must answer the client itself - silently dropping would hang the agent.
    /// </summary>
    public bool Park(string command, Action execute, Action<string> onTimeout)
    {
        if (_entries.Count >= _capacity) return false;

        _entries.Enqueue(new Entry(execute, onTimeout, Time.realtimeSinceStartup, command));
        return true;
    }

    /// <summary>Runs every parked command in arrival order.</summary>
    public void DrainAll()
    {
        while (_entries.Count > 0)
        {
            Entry entry = _entries.Dequeue();
            try
            {
                entry.Execute?.Invoke();
            }
            catch (Exception error)
            {
                Debug.LogError($"Parked command '{entry.Command}' failed on replay: {error}");
            }
        }
    }

    /// <summary>
    /// Answers commands that have waited past the timeout. Entries are FIFO and share one timeout,
    /// so everything expired sits at the head of the queue.
    /// </summary>
    public void ExpireStale()
    {
        float now = Time.realtimeSinceStartup;
        while (_entries.Count > 0 && now - _entries.Peek().ParkedAt >= _timeoutSeconds)
        {
            Entry entry = _entries.Dequeue();
            Debug.LogWarning(
                $"Parked command '{entry.Command}' timed out after {_timeoutSeconds}s waiting for the " +
                "sandbox to become ready.");
            entry.OnTimeout?.Invoke(
                $"Error: sandbox not ready after {_timeoutSeconds}s (command '{entry.Command}' was " +
                "parked while the environment was resetting).");
        }
    }
}

/// <summary>
/// Helpers for running one sandbox per machine-port in a distributed benchmark fleet.
/// </summary>
public static class SandboxNetwork
{
    /// <summary>
    /// Asks the OS for a free TCP port by binding an ephemeral listener and immediately releasing
    /// it. WebSocketSharp cannot report an OS-assigned port back to us (its <c>Port</c> property
    /// echoes whatever it was constructed with), so we probe first and hand it a concrete number.
    /// The gap between releasing the probe and binding for real is a TOCTOU window; callers retry.
    /// </summary>
    public static int FindFreePort()
    {
        TcpListener probe = new TcpListener(IPAddress.Any, 0);
        try
        {
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>
    /// Stable per-installation sandbox id, so a sim that crashes and restarts re-registers as the
    /// same sandbox rather than leaking a phantom entry in the coordinator's pool.
    ///
    /// Distributed bench fleets run several copies of the same build on one machine, and
    /// <see cref="Application.persistentDataPath"/> is keyed on company/product name rather than
    /// process or install location, so every copy would otherwise share one file and register with
    /// the same id. Bench sandboxes are disposable, so for them we skip persistence and just hand
    /// back a fresh id per process instead.
    /// </summary>
    public static string LoadOrCreateSandboxId(bool persist = true)
    {
        if (!persist) return Guid.NewGuid().ToString("N");

        string path = System.IO.Path.Combine(Application.persistentDataPath, "sandbox_id.txt");
        try
        {
            if (System.IO.File.Exists(path))
            {
                string existing = System.IO.File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing)) return existing;
            }

            string created = Guid.NewGuid().ToString("N");
            System.IO.File.WriteAllText(path, created);
            return created;
        }
        catch (Exception error)
        {
            // A read-only persistentDataPath must not stop the sandbox from registering; fall back
            // to a process-lifetime id and accept that a restart looks like a new sandbox.
            Debug.LogWarning($"Could not persist sandbox id at {path}: {error.Message}");
            return Guid.NewGuid().ToString("N");
        }
    }
}
