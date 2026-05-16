"""
Emulates a multiplayer client joining /multiplayer and moving around.
Requires: pip install websocket-client
Usage:    python test_multiplayer_client.py [--host localhost] [--port 8080]
"""

import json
import time
import argparse
import threading
import websocket

HOST = "localhost"
PORT = 8080

agent_id = None
received_events = []


def on_message(ws, message):
    try:
        data = json.loads(message)
    except json.JSONDecodeError:
        # Could be a raw screenshot base64 string
        print(f"[recv] <binary/base64 data, length={len(message)}>")
        return

    msg_type = data.get("type") or data.get("command", "?")
    received_events.append(data)

    global agent_id
    if data.get("type") == "Joined":
        agent_id = data["agentId"]
        print(f"[recv] Joined as agentId={agent_id}")
    elif data.get("type") == "Snapshot":
        print(f"[recv] Snapshot: existing agent {data['agentId']} at {data.get('position')}")
    elif data.get("type") == "AgentSpawned":
        print(f"[recv] AgentSpawned: {data['agentId']} at {data.get('position')}")
    elif data.get("type") == "AgentUpdate":
        print(f"[recv] AgentUpdate: {data['agentId']} -> {data.get('command')} "
              f"translation={data.get('translation')} rotation={data.get('rotation')}")
    elif data.get("type") == "AgentLeft":
        print(f"[recv] AgentLeft: {data['agentId']}")
    elif data.get("type") == "Chat":
        print(f"[recv] Chat: {data['agentId']}: {data.get('message')}")
    else:
        print(f"[recv] {message}")


def on_error(ws, error):
    print(f"[error] {error}")


def on_close(ws, close_status_code, close_msg):
    print(f"[closed] status={close_status_code} msg={close_msg}")


def on_open(ws):
    print("[open] Connected to /multiplayer")


def send(ws, payload: dict):
    msg = json.dumps(payload)
    print(f"[send] {msg}")
    ws.send(msg)


def wait_for_join(timeout=5.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if agent_id is not None:
            return True
        time.sleep(0.05)
    return False


def run(host, port):
    url = f"ws://{host}:{port}/multiplayer"
    print(f"Connecting to {url} ...")

    ws = websocket.WebSocketApp(
        url,
        on_open=on_open,
        on_message=on_message,
        on_error=on_error,
        on_close=on_close,
    )

    thread = threading.Thread(target=ws.run_forever, daemon=True)
    thread.start()
    time.sleep(0.5)  # let the connection establish

    # --- Join ---
    send(ws, {"command": "Join"})
    if not wait_for_join():
        print("[error] Timed out waiting for Joined response")
        ws.close()
        return

    print(f"\n--- Starting movement sequence for agent {agent_id} ---\n")

    send(ws, {"command": "Chat", "message": "Hello! Agent joining."})
    time.sleep(0.2)

    # --- Walk forward in steps ---
    print("Walking forward (10 steps)...")
    for i in range(10):
        send(ws, {
            "command": "TranslateAgent",
            "translation": [0, 0, 0.08],
            "rotation": [0, 0, 0],
        })
        time.sleep(0.3)

    # --- Rotate right ---
    print("Rotating right 90 degrees (9 steps of 10 deg)...")
    for i in range(9):
        send(ws, {
            "command": "TranslateAgent",
            "translation": [0, 0, 0],
            "rotation": [0, 10, 0],
        })
        time.sleep(0.2)

    # --- Walk forward again ---
    print("Walking forward again (5 steps)...")
    for i in range(5):
        send(ws, {
            "command": "TranslateAgent",
            "translation": [0, 0, 0.5],
            "rotation": [0, 0, 0],
        })
        time.sleep(0.3)

    # --- Move hand around ---
    print("Moving hand forward...")
    send(ws, {"command": "TransformHand", "handPosition": [0, 0, 0.3], "handRotation": [0, 0, 0]})
    time.sleep(0.5)

    print("Toggling grip...")
    send(ws, {"command": "ToggleGrip"})
    time.sleep(0.5)

    print("Resetting hand position...")
    send(ws, {"command": "ResetHandPosition"})
    time.sleep(0.5)

    # --- Teleport to origin ---
    print("Teleporting to origin...")
    send(ws, {"command": "Chat", "message": "Teleporting back to origin."})
    time.sleep(0.2)
    send(ws, {
        "command": "TransformAgent",
        "translation": [0, 0, 0],
        "rotation": [0, 0, 0],
    })
    time.sleep(0.5)

    # --- Request screenshot ---
    print("Requesting egocentric screenshot...")
    send(ws, {"command": "RequestScreenshot"})
    time.sleep(2.0)  # screenshot takes ~0.5s + processing

    send(ws, {"command": "Chat", "message": "Done! Disconnecting."})
    time.sleep(0.2)

    print("\n--- Sequence complete. Disconnecting. ---")
    ws.close()
    thread.join(timeout=2.0)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Sari multiplayer test client")
    parser.add_argument("--host", default=HOST)
    parser.add_argument("--port", type=int, default=PORT)
    args = parser.parse_args()
    run(args.host, args.port)
