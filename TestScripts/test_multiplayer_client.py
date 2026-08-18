"""
Emulates a multiplayer client joining /multiplayer and moving around.
Requires: pip install websocket-client
Usage:    python test_multiplayer_client.py [--host localhost] [--port 8080] [--screenshot-dir PATH]
"""

import json
import math
import time
import argparse
import base64
import binascii
import threading
from datetime import datetime
from pathlib import Path
import websocket

HOST = "localhost"
PORT = 8080

agent_id = None
received_events = []
screenshot_count = 0
lidar_center_samples = []
screenshot_dir = Path(__file__).resolve().parent / "screenshots"
connected_event = threading.Event()
screenshot_condition = threading.Condition()
lidar_center_condition = threading.Condition()


def on_message(ws, message):
    global agent_id, screenshot_count

    try:
        data = json.loads(message)
    except json.JSONDecodeError:
        if is_png_screenshot(message):
            png_bytes = base64.b64decode(message, validate=True)
            with screenshot_condition:
                screenshot_count += 1
                screenshot_path = save_screenshot(png_bytes, screenshot_count)
                screenshot_condition.notify_all()
            print(f"[recv] <PNG screenshot #{screenshot_count}, saved to {screenshot_path}>")
        else:
            # Command acknowledgements are plain text rather than JSON.
            print(f"[recv] {message}")
        return

    received_events.append(data)

    if data.get("type") == "Joined":
        agent_id = data["agentId"]
        print(f"[recv] Joined as agentId={agent_id}")
    elif data.get("type") == "Snapshot":
        validate_pose_event(data, require_recovery_count=True)
        print(f"[recv] Snapshot: existing agent {data['agentId']} at {data.get('position')}")
    elif data.get("type") == "AgentSpawned":
        print(f"[recv] AgentSpawned: {data['agentId']} at {data.get('position')}")
    elif data.get("type") == "AgentUpdate":
        print(f"[recv] AgentUpdate: {data['agentId']} -> {data.get('command')} "
              f"translation={data.get('translation')} rotation={data.get('rotation')}")
    elif data.get("type") == "AgentRecovered":
        validate_pose_event(data, require_recovery_count=True)
        if data.get("reason") != "out_of_bounds":
            raise ValueError(f"Unexpected recovery reason: {data!r}")
        print(f"[recv] AgentRecovered: {data['agentId']} snapped to "
              f"{data['position']} rotation={data['rotation']} "
              f"count={data['recoveryCount']}")
    elif data.get("type") == "AgentLeft":
        print(f"[recv] AgentLeft: {data['agentId']}")
    elif data.get("type") == "Chat":
        print(f"[recv] Chat: {data['agentId']}: {data.get('message')}")
    elif set(data) == {"distance", "hit", "min_range", "max_range"}:
        validate_lidar_center(data)
        with lidar_center_condition:
            lidar_center_samples.append(data)
            lidar_center_condition.notify_all()
        print(f"[recv] LiDAR center: distance={data['distance']} hit={data['hit']}")
    else:
        print(f"[recv] {message}")


def on_error(ws, error):
    print(f"[error] {error}")


def on_close(ws, close_status_code, close_msg):
    print(f"[closed] status={close_status_code} msg={close_msg}")


def on_open(ws):
    print("[open] Connected to /multiplayer")
    connected_event.set()


def send(ws, payload: dict):
    msg = json.dumps(payload)
    print(f"[send] {msg}")
    ws.send(msg)


def is_png_screenshot(message):
    try:
        png_bytes = base64.b64decode(message, validate=True)
    except (binascii.Error, ValueError):
        return False
    return png_bytes.startswith(b"\x89PNG\r\n\x1a\n")


def save_screenshot(png_bytes, sequence_number):
    screenshot_dir.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    screenshot_path = screenshot_dir / f"multiplayer-screenshot-{timestamp}-{sequence_number}.png"
    screenshot_path.write_bytes(png_bytes)
    return screenshot_path.resolve()


def wait_for_join(timeout=5.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if agent_id is not None:
            return True
        time.sleep(0.05)
    return False


def wait_for_screenshots(expected_count, timeout=5.0):
    deadline = time.time() + timeout
    with screenshot_condition:
        while screenshot_count < expected_count:
            remaining = deadline - time.time()
            if remaining <= 0:
                return False
            screenshot_condition.wait(timeout=remaining)
    return True


def validate_lidar_center(sample):
    for key in ("distance", "min_range", "max_range"):
        value = sample[key]
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
            raise ValueError(f"LiDAR center {key} must be a finite number: {value!r}")
    if not isinstance(sample["hit"], bool):
        raise ValueError(f"LiDAR center hit must be a bool: {sample['hit']!r}")
    if sample["min_range"] >= sample["max_range"]:
        raise ValueError(f"Invalid LiDAR center range bounds: {sample!r}")
    if not sample["min_range"] <= sample["distance"] <= sample["max_range"]:
        raise ValueError(f"LiDAR center distance is outside its range bounds: {sample!r}")
    if sample["hit"] and sample["distance"] >= sample["max_range"]:
        raise ValueError(f"LiDAR center hit cannot be at max_range: {sample!r}")
    if not sample["hit"] and sample["distance"] != sample["max_range"]:
        raise ValueError(f"LiDAR center miss must equal max_range: {sample!r}")


def validate_pose_event(event, require_recovery_count=False):
    if not isinstance(event.get("agentId"), str) or not event["agentId"]:
        raise ValueError(f"Pose event requires a non-empty agentId: {event!r}")
    for key in ("position", "rotation"):
        values = event.get(key)
        if not isinstance(values, list) or len(values) != 3:
            raise ValueError(f"Pose event {key} must contain three coordinates: {event!r}")
        if any(isinstance(value, bool) or not isinstance(value, (int, float))
               or not math.isfinite(value) for value in values):
            raise ValueError(f"Pose event {key} must contain finite numbers: {event!r}")
    if require_recovery_count:
        count = event.get("recoveryCount")
        if isinstance(count, bool) or not isinstance(count, int) or count < 0:
            raise ValueError(f"Pose event recoveryCount must be a non-negative int: {event!r}")


def wait_for_lidar_center_samples(expected_count, timeout=5.0):
    deadline = time.time() + timeout
    with lidar_center_condition:
        while len(lidar_center_samples) < expected_count:
            remaining = deadline - time.time()
            if remaining <= 0:
                return False
            lidar_center_condition.wait(timeout=remaining)
    return True


def run(host, port, output_dir):
    global agent_id, screenshot_count, screenshot_dir

    agent_id = None
    screenshot_count = 0
    screenshot_dir = Path(output_dir).expanduser()
    received_events.clear()
    lidar_center_samples.clear()
    connected_event.clear()

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
    if not connected_event.wait(timeout=5.0):
        print("[error] Timed out connecting to the multiplayer endpoint")
        ws.close()
        return

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
    print("Moving along world +Z (10 steps)...")
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
    print("Moving along world +Z again after yaw rotation (5 steps)...")
    for i in range(5):
        send(ws, {
            "command": "TranslateAgent",
            "translation": [0, 0, 0.5],
            "rotation": [0, 0, 0],
        })
        time.sleep(0.3)

    # --- Move hand around ---
    print("Moving hand forward...")
    send(ws, {"command": "TranslateHand", "handPosition": [0, 0, 0.3], "handRotation": [0, 0, 0]})
    time.sleep(0.5)

    print("Toggling grip...")
    send(ws, {"command": "ToggleGrip"})
    time.sleep(0.5)

    print("Resetting hand position...")
    send(ws, {"command": "ResetHandPosition"})
    time.sleep(0.5)

    # --- Reverse the movement sequence ---
    print("Returning to the spawn pose with inverse deltas...")
    send(ws, {"command": "Chat", "message": "Returning to my spawn pose."})
    time.sleep(0.2)
    send(ws, {
        "command": "TranslateAgent",
        "translation": [0, 0, -3.3],
        "rotation": [0, -90, 0],
    })
    time.sleep(0.5)

    # --- Request screenshots ---
    print("Requesting two queued egocentric screenshots...")
    expected_screenshot_count = screenshot_count + 2
    send(ws, {"command": "RequestScreenshot"})
    send(ws, {"command": "RequestScreenshot"})
    if not wait_for_screenshots(expected_screenshot_count, timeout=5.0):
        print(f"[error] Timed out waiting for queued screenshots "
              f"({screenshot_count}/{expected_screenshot_count} received)")

    print("Requesting center-gaze LiDAR distance...")
    expected_lidar_sample_count = len(lidar_center_samples) + 1
    send(ws, {"command": "RequestLidarCenter"})
    if not wait_for_lidar_center_samples(expected_lidar_sample_count, timeout=5.0):
        print("[error] Timed out waiting for LiDAR center sample")

    send(ws, {"command": "Chat", "message": "Done! Disconnecting."})
    time.sleep(0.2)

    print("\n--- Sequence complete. Disconnecting. ---")
    ws.close()
    thread.join(timeout=2.0)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Sari multiplayer test client")
    parser.add_argument("--host", default=HOST)
    parser.add_argument("--port", type=int, default=PORT)
    parser.add_argument("--screenshot-dir", default=screenshot_dir)
    args = parser.parse_args()
    run(args.host, args.port, args.screenshot_dir)
