"""Send the store-reset command to the Unity /commands WebSocket endpoint.

Requires: pip install websocket-client
Usage:    python TestScripts/test_reset_store.py [--host localhost] [--port 8080]
"""

import argparse
import json
import sys

import websocket


DEFAULT_HOST = "localhost"
DEFAULT_PORT = 8080
EXPECTED_RESPONSE = "Environment reset"


def reset_store(host: str, port: int, timeout: float) -> None:
    url = f"ws://{host}:{port}/commands"
    request = {"command": "ResetEnvironment"}

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
        if response != EXPECTED_RESPONSE:
            raise RuntimeError(
                f"Expected {EXPECTED_RESPONSE!r}, received {response!r}"
            )
    finally:
        ws.close()

    print("[pass] Store reset completed.")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Reset the Unity store through the /commands WebSocket endpoint."
    )
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--timeout", type=float, default=10.0)
    args = parser.parse_args()

    try:
        reset_store(args.host, args.port, args.timeout)
    except (OSError, RuntimeError, UnicodeDecodeError, websocket.WebSocketException) as exc:
        print(f"[fail] {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
