"""Async adapter for Unity's /commands WebSocket endpoint."""

from __future__ import annotations

import base64
import binascii
import json
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any


class UnityCommandError(RuntimeError):
    pass


PNG_MAGIC = b"\x89PNG\r\n\x1a\n"

# Commands whose response is a PNG image saved to screenshot_dir
IMAGE_COMMANDS = {
    "RequestScreenshot": "screenshot",
    "RequestDepthMap": "depthmap",
}


def _is_png_base64(value: str) -> bytes | None:
    try:
        png_bytes = base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError):
        return None
    if png_bytes.startswith(b"\x89PNG\r\n\x1a\n"):
        return png_bytes
    return None


@dataclass(slots=True)
class UnityCommandClient:
    host: str = "localhost"
    port: int = 8080
    screenshot_dir: Path = Path("screenshots")
    timeout_seconds: float = 30.0

    @property
    def url(self) -> str:
        return f"ws://{self.host}:{self.port}/commands"

    async def command(self, command: str, **payload: Any) -> dict[str, Any]:
        import websockets

        message = {"command": command}
        message.update(payload)
        async with websockets.connect(self.url, open_timeout=self.timeout_seconds) as websocket:
            await websocket.send(json.dumps(message))
            response = await websocket.recv()
        if isinstance(response, bytes):
            # Unity sends image payloads as raw binary frames — don't utf-8 decode those.
            if response.startswith(PNG_MAGIC):
                return self._save_image(command, response)
            response = response.decode("utf-8")
        return self.normalize_response(command, response)

    def normalize_response(self, command: str, response: str) -> dict[str, Any]:
        if command in IMAGE_COMMANDS:
            png_bytes = _is_png_base64(response)
            if png_bytes is None:
                raise UnityCommandError(f"{command} response was not a PNG payload: {response[:200]}")
            return self._save_image(command, png_bytes)

        try:
            value = json.loads(response)
        except json.JSONDecodeError as exc:
            raise UnityCommandError(
                f"{command} response was not JSON. Legacy/v1 text responses are unsupported."
            ) from exc

        if isinstance(value, dict):
            return {"command": command, "result": value}
        return {"command": command, "result": value}

    def _save_image(self, command: str, png_bytes: bytes) -> dict[str, Any]:
        kind = IMAGE_COMMANDS.get(command, "image")
        self.screenshot_dir.mkdir(parents=True, exist_ok=True)
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
        path = (self.screenshot_dir / f"commands-{kind}-{timestamp}.png").resolve()
        path.write_bytes(png_bytes)
        return {
            "command": command,
            "screenshot": {
                "path": str(path),
                "bytes": len(png_bytes),
                "mime_type": "image/png",
            },
        }
