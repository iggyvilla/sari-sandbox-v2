"""Run three independent TranslateHand rotation tests against the Unity sandbox.

The right hand is reset before each rotation so the rotations do not accumulate.

Requires: pip install websocket-client
Usage:    python TestScripts/test_translate_hand_rotations.py
          python TestScripts/test_translate_hand_rotations.py --host localhost --port 8080
          python TestScripts/test_translate_hand_rotations.py --delay 2
"""

import argparse
import json
import sys
import time

import websocket


DEFAULT_HOST = "localhost"
DEFAULT_PORT = 8080
ROTATIONS = (
    (90.0, 0.0, 0.0),
    (0.0, 90.0, 0.0),
    (0.0, 0.0, 90.0),
)


def send_command(ws: "websocket.WebSocket", request: dict[str, object]) -> str:
    message = json.dumps(request)
    print(f"[send] {message}")
    ws.send(message)

    response = ws.recv()
    if isinstance(response, bytes):
        response = response.decode("utf-8")

    if response.startswith("Error:"):
        raise RuntimeError(response)

    print(f"[recv] {response}")
    return response


def test_rotations(
    host: str,
    port: int,
    delay: float,
    timeout: float,
) -> None:
    url = f"ws://{host}:{port}/commands"

    print(f"Connecting to {url} ...")
    ws = websocket.create_connection(url, timeout=timeout)
    try:
        for index, rotation in enumerate(ROTATIONS):
            print(f"\n--- {index + 1}/{len(ROTATIONS)}: rotation={rotation} ---")
            send_command(ws, {"command": "ResetHandPosition"})
            send_command(
                ws,
                {
                    "command": "TranslateHand",
                    "translation": [0.0, 0.0, 0.0],
                    "rotation": list(rotation),
                },
            )

            if index < len(ROTATIONS) - 1:
                time.sleep(delay)
    finally:
        ws.close()

    print(f"\n[pass] Ran {len(ROTATIONS)} TranslateHand rotation tests.")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Test independent 90-degree rotations of the right hand on each axis."
    )
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument(
        "--delay",
        type=float,
        default=1.5,
        help="Seconds to pause between rotations for visual inspection.",
    )
    args = parser.parse_args()

    if args.delay < 0:
        print("[fail] --delay cannot be negative", file=sys.stderr)
        return 1

    try:
        test_rotations(args.host, args.port, args.delay, args.timeout)
    except (OSError, RuntimeError, UnicodeDecodeError, websocket.WebSocketException) as exc:
        print(f"[fail] {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
