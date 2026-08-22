"""Send the RequestScreenshot command to the Unity /commands WebSocket endpoint
and save the returned PNG to disk.

Requires: pip install websocket-client
Usage:    python TestScripts/test_request_screenshot.py [--host localhost] [--port 8080]
"""

import argparse
import json
import sys
from datetime import datetime
from pathlib import Path

import websocket


DEFAULT_HOST = "localhost"
DEFAULT_PORT = 8080
DEFAULT_OUTPUT_DIR = Path(__file__).resolve().parent / "screenshots"
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


def request_screenshot(host: str, port: int, timeout: float, output_dir: Path) -> Path:
    url = f"ws://{host}:{port}/commands"
    request = {"command": "RequestScreenshot"}

    print(f"Connecting to {url} ...")
    ws = websocket.create_connection(url, timeout=timeout)
    try:
        message = json.dumps(request)
        print(f"[send] {message}")
        ws.send(message)

        opcode, payload = ws.recv_data()
        if opcode != websocket.ABNF.OPCODE_BINARY:
            raise RuntimeError(f"Expected a binary PNG frame, received opcode {opcode}: {payload!r}")

        if not payload.startswith(PNG_MAGIC):
            raise RuntimeError("RequestScreenshot response was not a PNG image")

        output_dir.mkdir(parents=True, exist_ok=True)
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
        screenshot_path = output_dir / f"screenshot-{timestamp}.png"
        screenshot_path.write_bytes(payload)
    finally:
        ws.close()

    print(f"[pass] Screenshot saved to {screenshot_path.resolve()} ({len(payload)} bytes)")
    return screenshot_path


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Request a screenshot through the Unity /commands WebSocket endpoint."
    )
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument("--output-dir", default=DEFAULT_OUTPUT_DIR, type=Path)
    args = parser.parse_args()

    try:
        request_screenshot(args.host, args.port, args.timeout, args.output_dir)
    except (OSError, RuntimeError, websocket.WebSocketException) as exc:
        print(f"[fail] {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
