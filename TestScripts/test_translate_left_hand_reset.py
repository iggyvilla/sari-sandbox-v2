"""Translate the left hand rightwards, rotate it, then reset both hands.

Requires: pip install websocket-client
Usage:    python TestScripts/test_translate_left_hand_reset.py
          python TestScripts/test_translate_left_hand_reset.py --host localhost --port 8080
          python TestScripts/test_translate_left_hand_reset.py --delay 2
"""

import argparse
import json
import sys
import time

import websocket


DEFAULT_HOST = "localhost"
DEFAULT_PORT = 8080
TRANSLATION = (0.2, 0.0, 0.0)
ROTATION = (0.0, 90.0, 0.0)


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


def test_translate_left_hand_reset(
    host: str,
    port: int,
    delay: float,
    timeout: float,
) -> None:
    url = f"ws://{host}:{port}/commands"

    print(f"Connecting to {url} ...")
    ws = websocket.create_connection(url, timeout=timeout)
    try:
        print("\n--- 1/3: translate left hand rightwards ---")
        send_command(
            ws,
            {
                "command": "TranslateLeftHand",
                "translation": list(TRANSLATION),
                "rotation": [0.0, 0.0, 0.0],
            },
        )
        time.sleep(delay)

        print("\n--- 2/3: rotate left hand ---")
        send_command(
            ws,
            {
                "command": "TranslateLeftHand",
                "translation": [0.0, 0.0, 0.0],
                "rotation": list(ROTATION),
            },
        )
        time.sleep(delay)

        print("\n--- 3/3: reset both hands ---")
        send_command(ws, {"command": "ResetHands"})
    finally:
        ws.close()

    print("\n[pass] Translated, rotated, and reset the left hand.")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Move the left hand rightwards, rotate it, then reset both hands."
    )
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument(
        "--delay",
        type=float,
        default=1.5,
        help="Seconds to pause between steps for visual inspection.",
    )
    args = parser.parse_args()

    if args.delay < 0:
        print("[fail] --delay cannot be negative", file=sys.stderr)
        return 1

    try:
        test_translate_left_hand_reset(args.host, args.port, args.delay, args.timeout)
    except (OSError, RuntimeError, UnicodeDecodeError, websocket.WebSocketException) as exc:
        print(f"[fail] {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
