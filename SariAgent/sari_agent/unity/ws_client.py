"""Async adapter for Unity's /commands WebSocket endpoint."""

from __future__ import annotations

import base64
import binascii
import json
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from time import perf_counter
from typing import Any

from sari_agent.debug.images import thumbnail_data_url
from sari_agent.debug.trace import get_tool_trace


class UnityCommandError(RuntimeError):
    pass


def _is_png_base64(value: str) -> bytes | None:
    try:
        png_bytes = base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError):
        return None
    if png_bytes.startswith(b"\x89PNG\r\n\x1a\n"):
        return png_bytes
    return None


def _is_png_bytes(value: bytes) -> bool:
    return value.startswith(b"\x89PNG\r\n\x1a\n")


@dataclass(slots=True)
class UnityCommandClient:
    host: str = "localhost"
    port: int = 8080
    screenshot_dir: Path = Path("screenshots")
    timeout_seconds: float = 30.0
    max_message_bytes: int | None = 16 * 1024 * 1024
    debug_hub: Any | None = None

    @property
    def url(self) -> str:
        return f"ws://{self.host}:{self.port}/commands"

    async def command(self, command: str, **payload: Any) -> dict[str, Any]:
        import websockets

        message = {"command": command}
        message.update(payload)
        raw_json = json.dumps(message)
        started_at = perf_counter()
        trace = get_tool_trace()

        if self._debug_enabled():
            await self._publish_debug(
                "unity.command.sent",
                summary=f"Sent {command} to Unity",
                payload={
                    "url": self.url,
                    "command": command,
                    "payload": message,
                    "raw_json": raw_json,
                    "tool_call": trace.to_dict() if trace is not None else None,
                },
            )

        response: str | bytes | None = None
        try:
            async with websockets.connect(
                self.url,
                open_timeout=self.timeout_seconds,
                max_size=self.max_message_bytes,
            ) as websocket:
                await websocket.send(raw_json)
                response = await websocket.recv()
            result = self.normalize_response(command, response)
        except Exception as exc:
            if self._debug_enabled():
                await self._publish_debug(
                    "unity.command.error",
                    level="error",
                    summary=f"{command} failed",
                    payload={
                        "url": self.url,
                        "command": command,
                        "duration_ms": _duration_ms(started_at),
                        "tool_call": trace.to_dict() if trace is not None else None,
                        "raw_response": _describe_raw_response(command, response),
                        "error": _exception_payload(exc),
                    },
                )
            raise

        if self._debug_enabled():
            await self._publish_debug(
                "unity.command.received",
                summary=f"Received {command} response from Unity",
                payload={
                    "url": self.url,
                    "command": command,
                    "duration_ms": _duration_ms(started_at),
                    "tool_call": trace.to_dict() if trace is not None else None,
                    "raw_response": _describe_raw_response(command, response),
                    "normalized_result": result,
                    "content": self._debug_content_blocks(result),
                },
            )
        return result

    def normalize_response(self, command: str, response: str | bytes) -> dict[str, Any]:
        if command == "RequestScreenshot":
            if isinstance(response, bytes):
                if not _is_png_bytes(response):
                    raise UnityCommandError("RequestScreenshot byte response was not a PNG payload")
                return self._save_screenshot(response)

            png_bytes = _is_png_base64(response)
            if png_bytes is None:
                raise UnityCommandError("RequestScreenshot response was not a base64 PNG payload")
            return self._save_screenshot(png_bytes)

        if isinstance(response, bytes):
            response = response.decode("utf-8")

        try:
            value = json.loads(response)
        except json.JSONDecodeError as exc:
            raise UnityCommandError(
                f"{command} response was not JSON. Legacy/v1 text responses are unsupported."
            ) from exc

        if isinstance(value, dict):
            return {"command": command, "result": value}
        return {"command": command, "result": value}

    def _save_screenshot(self, png_bytes: bytes) -> dict[str, Any]:
        self.screenshot_dir.mkdir(parents=True, exist_ok=True)
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
        path = (self.screenshot_dir / f"commands-screenshot-{timestamp}.png").resolve()
        path.write_bytes(png_bytes)
        return {
            "command": "RequestScreenshot",
            "screenshot": {
                "path": str(path),
                "bytes": len(png_bytes),
                "mime_type": "image/png",
            },
        }

    async def _publish_debug(
        self,
        event_type: str,
        *,
        level: str = "info",
        summary: str = "",
        payload: dict[str, Any] | None = None,
    ) -> None:
        if not self._debug_enabled():
            return
        await self.debug_hub.publish(
            event_type,
            stage="execute_tools",
            level=level,
            summary=summary,
            payload=payload,
        )

    def _debug_content_blocks(self, result: dict[str, Any]) -> list[dict[str, Any]]:
        screenshot = result.get("screenshot")
        if isinstance(screenshot, dict):
            block: dict[str, Any] = {
                "kind": "image",
                "mime_type": screenshot.get("mime_type", "image/png"),
                "path": screenshot.get("path"),
                "bytes": screenshot.get("bytes"),
            }
            path = screenshot.get("path")
            if path:
                try:
                    png_bytes = Path(path).read_bytes()
                    max_edge = getattr(self.debug_hub, "image_max_edge", 512)
                    block["thumbnail_data_url"] = thumbnail_data_url(
                        png_bytes,
                        max_edge=max_edge,
                    )
                except OSError as exc:
                    block["thumbnail_error"] = str(exc)
            return [block]

        return [{"kind": "text", "text": json.dumps(result, ensure_ascii=False, default=str)}]

    def _debug_enabled(self) -> bool:
        return self.debug_hub is not None and getattr(self.debug_hub, "enabled", False)


def _describe_raw_response(command: str, response: str | bytes | None) -> dict[str, Any]:
    if response is None:
        return {"kind": "none"}
    if isinstance(response, bytes):
        return {
            "kind": "binary",
            "bytes": len(response),
            "mime_type": "image/png" if _is_png_bytes(response) else "application/octet-stream",
        }
    if command == "RequestScreenshot":
        png_bytes = _is_png_base64(response)
        if png_bytes is not None:
            return {
                "kind": "base64",
                "bytes": len(png_bytes),
                "mime_type": "image/png",
            }
    return {"kind": "text", "raw_text": response}


def _duration_ms(started_at: float) -> float:
    return round((perf_counter() - started_at) * 1000, 3)


def _exception_payload(exc: Exception) -> dict[str, str]:
    return {"type": type(exc).__name__, "message": str(exc)}
