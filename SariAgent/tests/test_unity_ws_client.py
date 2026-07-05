from __future__ import annotations

import base64
import json

import pytest

from sari_agent.debug import DebugHub, ToolTraceContext, tool_trace
from sari_agent.unity.ws_client import UnityCommandClient, UnityCommandError


PNG_BYTES = b"\x89PNG\r\n\x1a\n" + b"\x00" * 8
VALID_PNG_BYTES = (
    b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01"
    b"\x08\x06\x00\x00\x00\x1f\x15\xc4\x89\x00\x00\x00\rIDATx\x9cc\xf8\xff"
    b"\xff?\x00\x05\xfe\x02\xfeA\xe2%\xb3\x00\x00\x00\x00IEND\xaeB`\x82"
)


class FakeWebSocket:
    def __init__(self, response: str | bytes) -> None:
        self.response = response
        self.sent: list[str] = []

    async def send(self, message: str) -> None:
        self.sent.append(message)

    async def recv(self) -> str | bytes:
        return self.response


class FakeConnect:
    def __init__(self, websocket: FakeWebSocket) -> None:
        self.websocket = websocket

    async def __aenter__(self) -> FakeWebSocket:
        return self.websocket

    async def __aexit__(self, exc_type, exc, tb) -> None:
        return None


def test_json_string_response_normalization(tmp_path) -> None:
    client = UnityCommandClient(screenshot_dir=tmp_path)

    result = client.normalize_response("ResetHandPosition", json.dumps("Hand position reset"))

    assert result == {
        "command": "ResetHandPosition",
        "result": "Hand position reset",
    }


def test_json_object_response_normalization(tmp_path) -> None:
    client = UnityCommandClient(screenshot_dir=tmp_path)
    payload = {"current_position": [0, 0, 0], "collision": False}

    result = client.normalize_response("TranslateAgent", json.dumps(payload))

    assert result == {
        "command": "TranslateAgent",
        "result": payload,
    }


def test_legacy_text_response_is_rejected(tmp_path) -> None:
    client = UnityCommandClient(screenshot_dir=tmp_path)

    with pytest.raises(UnityCommandError, match="Legacy/v1 text responses are unsupported"):
        client.normalize_response("ResetHandPosition", "Hand position reset")


def test_screenshot_base64_is_saved_to_file(tmp_path) -> None:
    client = UnityCommandClient(screenshot_dir=tmp_path)
    response = base64.b64encode(PNG_BYTES).decode("ascii")

    result = client.normalize_response("RequestScreenshot", response)
    screenshot = result["screenshot"]

    assert result["command"] == "RequestScreenshot"
    assert screenshot["mime_type"] == "image/png"
    assert screenshot["bytes"] == len(PNG_BYTES)
    assert (tmp_path / screenshot["path"].split("/")[-1]).read_bytes() == PNG_BYTES


def test_screenshot_bytes_are_saved_to_file(tmp_path) -> None:
    client = UnityCommandClient(screenshot_dir=tmp_path)

    result = client.normalize_response("RequestScreenshot", PNG_BYTES)
    screenshot = result["screenshot"]

    assert result["command"] == "RequestScreenshot"
    assert screenshot["mime_type"] == "image/png"
    assert screenshot["bytes"] == len(PNG_BYTES)
    assert (tmp_path / screenshot["path"].split("/")[-1]).read_bytes() == PNG_BYTES


@pytest.mark.asyncio
async def test_command_publishes_unity_debug_events(monkeypatch, tmp_path) -> None:
    websockets = pytest.importorskip("websockets")
    websocket = FakeWebSocket(json.dumps("Hand position reset"))
    monkeypatch.setattr(websockets, "connect", lambda *args, **kwargs: FakeConnect(websocket))

    hub = DebugHub(enabled=True, run_id="run", replay_limit=10)
    client = UnityCommandClient(screenshot_dir=tmp_path, debug_hub=hub)

    with tool_trace(ToolTraceContext("call_1", "ResetHandPosition", "{}")):
        result = await client.command("ResetHandPosition")

    events = [event.to_dict() for event in hub.replay_events()]

    assert result == {"command": "ResetHandPosition", "result": "Hand position reset"}
    assert [event["type"] for event in events] == [
        "unity.command.sent",
        "unity.command.received",
    ]
    assert json.loads(websocket.sent[0]) == {"command": "ResetHandPosition"}
    assert events[0]["payload"]["tool_call"]["call_id"] == "call_1"
    assert events[0]["payload"]["raw_json"] == '{"command": "ResetHandPosition"}'
    assert events[1]["payload"]["raw_response"] == {
        "kind": "text",
        "raw_text": '"Hand position reset"',
    }


@pytest.mark.asyncio
async def test_request_screenshot_debug_event_has_thumbnail(monkeypatch, tmp_path) -> None:
    websockets = pytest.importorskip("websockets")
    websocket = FakeWebSocket(VALID_PNG_BYTES)
    monkeypatch.setattr(websockets, "connect", lambda *args, **kwargs: FakeConnect(websocket))

    hub = DebugHub(enabled=True, run_id="run", replay_limit=10, image_max_edge=1)
    client = UnityCommandClient(screenshot_dir=tmp_path, debug_hub=hub)

    result = await client.command("RequestScreenshot")
    received = hub.replay_events()[-1].to_dict()
    content = received["payload"]["content"][0]

    assert result["screenshot"]["bytes"] == len(VALID_PNG_BYTES)
    assert received["payload"]["raw_response"] == {
        "kind": "binary",
        "bytes": len(VALID_PNG_BYTES),
        "mime_type": "image/png",
    }
    assert content["kind"] == "image"
    assert content["thumbnail_data_url"].startswith("data:image/png;base64,")
