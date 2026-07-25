"""Sweep the optional `degrees` field of ResetEnvironment so the resulting facing can be eyeballed.

Resets the store once per angle, pausing in between so there is time to look at the sandbox and
note which heading each angle produces.

Requires: pip install websocket-client
Usage:    python TestScripts/test_reset_degrees.py [--host localhost] [--port 8080]
          python TestScripts/test_reset_degrees.py --step 45 --delay 2
          python TestScripts/test_reset_degrees.py --angles 0 90 180 270
"""

import argparse
import json
import sys
import time

import websocket


DEFAULT_HOST = "localhost"
DEFAULT_PORT = 8080
EXPECTED_RESPONSE = "Environment reset"


def reset_at(ws: "websocket.WebSocket", degrees: float | None) -> None:
    request = {"command": "ResetEnvironment"}
    if degrees is not None:
        request["degrees"] = degrees

    message = json.dumps(request)
    print(f"[send] {message}")
    ws.send(message)

    # The sandbox answers only once the reset has settled, so this recv doubles as the wait.
    response = ws.recv()
    if isinstance(response, bytes):
        response = response.decode("utf-8")

    print(f"[recv] {response}")
    if response != EXPECTED_RESPONSE:
        raise RuntimeError(f"Expected {EXPECTED_RESPONSE!r}, received {response!r}")


def sweep(host: str, port: int, angles: list[float], delay: float, timeout: float) -> None:
    url = f"ws://{host}:{port}/commands"

    print(f"Connecting to {url} ...")
    ws = websocket.create_connection(url, timeout=timeout)
    try:
        for index, degrees in enumerate(angles):
            print(f"\n--- {index + 1}/{len(angles)}: degrees={degrees} ---")
            reset_at(ws, degrees)
            if index < len(angles) - 1:
                time.sleep(delay)
    finally:
        ws.close()

    print(f"\n[pass] Swept {len(angles)} angle(s).")


def build_angles(args: argparse.Namespace) -> list[float]:
    if args.angles:
        return list(args.angles)

    # 360 is deliberately included even though it is the same facing as 0 - it confirms the sim
    # accepts an unwrapped angle rather than rejecting or clamping it.
    angles: list[float] = []
    current = float(args.start)
    while current <= args.stop + 1e-6:
        angles.append(round(current, 6))
        current += args.step
    return angles


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Reset the Unity store repeatedly across a range of agent y rotations."
    )
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--timeout", type=float, default=60.0)
    parser.add_argument(
        "--angles",
        type=float,
        nargs="+",
        help="Explicit list of angles to test, overriding --start/--stop/--step.",
    )
    parser.add_argument("--start", type=float, default=0.0)
    parser.add_argument("--stop", type=float, default=360.0)
    parser.add_argument("--step", type=float, default=45.0)
    parser.add_argument(
        "--delay",
        type=float,
        default=1.5,
        help="Seconds to pause between resets, on top of the sim's own settle time.",
    )
    args = parser.parse_args()

    if args.step <= 0:
        print("[fail] --step must be positive", file=sys.stderr)
        return 1

    angles = build_angles(args)
    if not angles:
        print("[fail] no angles to test", file=sys.stderr)
        return 1

    print(f"Angles: {angles}")
    try:
        sweep(args.host, args.port, angles, args.delay, args.timeout)
    except (OSError, RuntimeError, UnicodeDecodeError, websocket.WebSocketException) as exc:
        print(f"[fail] {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
