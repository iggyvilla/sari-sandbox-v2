"""
Requests center-gaze LiDAR distance samples from Unity's /commands websocket.

Requires:
  pip install websocket-client

Usage:
  python TestScripts/test_lidar_center_client.py
  python TestScripts/test_lidar_center_client.py --count 5 --delay 0.25
"""

import argparse
import json
import math
import sys
import time

import websocket


HOST = "localhost"
PORT = 8080
EXPECTED_KEYS = {"distance", "hit", "min_range", "max_range"}


class LidarCenterClientError(RuntimeError):
    pass


def request_sample(ws):
    request = {"command": "RequestLidarCenter"}
    encoded = json.dumps(request)
    print(f"[send] {encoded}")
    ws.send(encoded)

    response = ws.recv()
    if isinstance(response, bytes):
        try:
            response = response.decode("utf-8")
        except UnicodeDecodeError as exc:
            raise LidarCenterClientError(
                "Expected a JSON text response, but Unity returned binary data"
            ) from exc

    if response.startswith("Error:"):
        raise LidarCenterClientError(response)

    try:
        sample = json.loads(response)
    except json.JSONDecodeError as exc:
        raise LidarCenterClientError(
            f"Unity response was not valid JSON: {response!r}"
        ) from exc

    validate_sample(sample)
    return sample


def validate_sample(sample):
    if not isinstance(sample, dict):
        raise LidarCenterClientError(
            f"Expected a JSON object, got {type(sample).__name__}"
        )

    actual_keys = set(sample)
    if actual_keys != EXPECTED_KEYS:
        raise LidarCenterClientError(
            f"Expected keys {sorted(EXPECTED_KEYS)}, got {sorted(actual_keys)}"
        )

    distance = require_finite_number(sample["distance"], "distance")
    min_range = require_finite_number(sample["min_range"], "min_range")
    max_range = require_finite_number(sample["max_range"], "max_range")
    hit = sample["hit"]

    if not isinstance(hit, bool):
        raise LidarCenterClientError(f"hit must be a JSON boolean, got {hit!r}")
    if min_range >= max_range:
        raise LidarCenterClientError(
            f"Invalid LiDAR bounds: min_range={min_range}, max_range={max_range}"
        )
    if distance < min_range or distance > max_range:
        raise LidarCenterClientError(
            f"distance={distance} is outside {min_range}..{max_range}"
        )
    if hit and distance >= max_range:
        raise LidarCenterClientError("A hit cannot report distance >= max_range")
    if not hit and distance != max_range:
        raise LidarCenterClientError("A miss must report distance == max_range")


def require_finite_number(value, label):
    if (
        isinstance(value, bool)
        or not isinstance(value, (int, float))
        or not math.isfinite(value)
    ):
        raise LidarCenterClientError(
            f"{label} must be a finite JSON number, got {value!r}"
        )
    return float(value)


def run(host, port, timeout, count, delay):
    url = f"ws://{host}:{port}/commands"
    print(f"[connect] {url}")

    ws = websocket.create_connection(url, timeout=timeout)
    try:
        for index in range(count):
            sample = request_sample(ws)
            status = "hit" if sample["hit"] else "miss"
            print(
                f"[pass] sample {index + 1}/{count}: "
                f"distance={sample['distance']:.6f} m, "
                f"status={status}, range={sample['min_range']}..{sample['max_range']} m"
            )

            if delay > 0 and index + 1 < count:
                time.sleep(delay)
    finally:
        ws.close()


def main():
    parser = argparse.ArgumentParser(
        description="Request and validate center-gaze LiDAR samples from Unity."
    )
    parser.add_argument("--host", default=HOST)
    parser.add_argument("--port", type=int, default=PORT)
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument("--count", type=int, default=1)
    parser.add_argument("--delay", type=float, default=0.0)
    args = parser.parse_args()

    if args.count < 1:
        parser.error("--count must be at least 1")
    if args.timeout <= 0:
        parser.error("--timeout must be greater than 0")
    if args.delay < 0:
        parser.error("--delay cannot be negative")

    try:
        run(args.host, args.port, args.timeout, args.count, args.delay)
    except (LidarCenterClientError, OSError, websocket.WebSocketException) as exc:
        print(f"[fail] {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
