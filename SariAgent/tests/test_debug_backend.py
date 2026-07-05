from __future__ import annotations

import asyncio
import json
from pathlib import Path

import pytest

from sari_agent.debug import DebugEvent, DebugHub, DebugWebSocketServer
from sari_agent.debug.images import thumbnail_data_url


PNG_BYTES = (
    b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01"
    b"\x08\x06\x00\x00\x00\x1f\x15\xc4\x89\x00\x00\x00\rIDATx\x9cc\xf8\xff"
    b"\xff?\x00\x05\xfe\x02\xfeA\xe2%\xb3\x00\x00\x00\x00IEND\xaeB`\x82"
)


def test_debug_event_serializes_json_safe_payloads() -> None:
    event = DebugEvent(
        seq=1,
        run_id="run",
        type="test.event",
        payload={"path": Path("debug_runs/run.jsonl"), "bytes": b"abc"},
    )

    payload = event.to_dict()["payload"]

    assert payload["path"] == "debug_runs/run.jsonl"
    assert payload["bytes"] == {"kind": "bytes", "bytes": 3}
    assert json.loads(event.to_json())["schema_version"] == 1


@pytest.mark.asyncio
async def test_debug_hub_replays_recent_events_and_records_jsonl(tmp_path) -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=2, jsonl_dir=tmp_path)

    await hub.publish("first")
    await hub.publish("second")
    await hub.publish("third")

    queue = await hub.add_subscriber()
    replayed = [json.loads(await queue.get()) for _ in range(2)]

    assert [event["type"] for event in replayed] == ["second", "third"]
    assert [event.type for event in hub.replay_events()] == ["second", "third"]
    assert len((tmp_path / "run.jsonl").read_text(encoding="utf-8").splitlines()) == 3


@pytest.mark.asyncio
async def test_debug_websocket_server_streams_replay_and_live_events() -> None:
    websockets = pytest.importorskip("websockets")
    hub = DebugHub(enabled=True, run_id="run", replay_limit=10)
    await hub.publish("before-connect", summary="replayed")
    server = DebugWebSocketServer(hub, host="127.0.0.1", port=0)

    await server.start()
    assert server._server is not None
    port = server._server.sockets[0].getsockname()[1]
    try:
        async with websockets.connect(f"ws://127.0.0.1:{port}/debug") as websocket:
            first = json.loads(await asyncio.wait_for(websocket.recv(), timeout=1))
            assert first["type"] == "before-connect"

            await hub.publish("after-connect", summary="live")
            second = json.loads(await asyncio.wait_for(websocket.recv(), timeout=1))
            assert second["type"] == "after-connect"
    finally:
        await server.stop()


def test_thumbnail_data_url_returns_png_data_url() -> None:
    value = thumbnail_data_url(PNG_BYTES, max_edge=1)

    assert value.startswith("data:image/png;base64,")
