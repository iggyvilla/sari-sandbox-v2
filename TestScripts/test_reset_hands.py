"""Reset both agent hands through the Unity /commands WebSocket endpoint.

Requires: pip install websocket-client
Usage:    python test_reset_hands.py [--host localhost] [--port 8080]
"""

import argparse
import json
import sys

import websocket


DEFAULT_HOST = "localhost"
DEFAULT_PORT = 8080


def reset_hands(host: str, port: int, timeout: float) -> None:
    url = f"ws://{host}:{port}/commands"
    request = {"command": "ResetHands"}

    print(f"Connecting to {url} ...")
    ws = websocket.create_connection(url, timeout=timeout)
    try:
        message = json.dumps(request)
        print(f"[send] {message}")
        ws.send(message)

        response = ws.recv()
        if isinstance(response, bytes):
            response = response.decode("utf-8")

        print(f"[recv] {response}")
        if response.startswith("Error:"):
            raise RuntimeError(response)
    finally:
        ws.close()

    print("[pass] Both hands reset.")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Reset both Unity agent hands to their default prefab poses."
    )
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--timeout", type=float, default=10.0)
    args = parser.parse_args()

    try:
        reset_hands(args.host, args.port, args.timeout)
    except (OSError, RuntimeError, UnicodeDecodeError, websocket.WebSocketException) as exc:
        print(f"[fail] {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
