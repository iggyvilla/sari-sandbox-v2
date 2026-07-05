from __future__ import annotations

import asyncio
import json

import pytest

from sari_agent.debug import DebugHub, DebugWebSocketServer
from sari_agent.serve import ServeController


@pytest.mark.asyncio
async def test_hub_begin_run_rotates_run_id_and_jsonl_path(tmp_path) -> None:
    hub = DebugHub(enabled=True, run_id="first", replay_limit=10, jsonl_dir=tmp_path)

    await hub.publish("one")
    second = hub.begin_run()
    await hub.publish("two")

    assert second != "first"
    assert hub.jsonl_path == tmp_path / f"{second}.jsonl"
    assert (tmp_path / "first.jsonl").exists()
    assert (tmp_path / f"{second}.jsonl").exists()
    events = [event.to_dict() for event in hub.replay_events()]
    assert [event["run_id"] for event in events] == ["first", second]
    assert [event["seq"] for event in events] == [1, 2]


@pytest.mark.asyncio
async def test_controller_accepts_prompt_when_idle() -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=10)
    controller = ServeController(hub, mode="serve")

    await controller.handle_client_message(
        {"type": "client.run.start", "prompt": "look around"}
    )

    assert await controller.next_prompt() == "look around"
    assert "server.run.rejected" not in [event.type for event in hub.replay_events()]


@pytest.mark.asyncio
async def test_controller_rejects_when_running_cli_mode_or_invalid() -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=10)

    running = ServeController(hub, mode="serve")
    running.state = "running"
    await running.handle_client_message({"type": "client.run.start", "prompt": "hi"})

    cli = ServeController(hub, mode="cli")
    await cli.handle_client_message({"type": "client.run.start", "prompt": "hi"})

    idle = ServeController(hub, mode="serve")
    await idle.handle_client_message({"type": "client.run.start", "prompt": "   "})

    reasons = [
        event.payload["reason"]
        for event in hub.replay_events()
        if event.type == "server.run.rejected"
    ]
    assert reasons == ["run_active", "prompts_not_accepted", "invalid_message"]
    assert not cli.accepts_prompts
    assert not running.accepts_prompts


@pytest.mark.asyncio
async def test_websocket_server_sends_status_and_routes_client_messages() -> None:
    websockets = pytest.importorskip("websockets")
    hub = DebugHub(enabled=True, run_id="run", replay_limit=10)
    received: list[dict] = []

    async def on_client_message(message: dict) -> None:
        received.append(message)

    server = DebugWebSocketServer(
        hub,
        host="127.0.0.1",
        port=0,
        on_client_message=on_client_message,
        status_provider=lambda: {"state": "idle", "accepts_prompts": True},
    )
    await hub.publish("replayed")

    await server.start()
    assert server._server is not None
    port = server._server.sockets[0].getsockname()[1]
    try:
        async with websockets.connect(f"ws://127.0.0.1:{port}/debug") as websocket:
            first = json.loads(await asyncio.wait_for(websocket.recv(), timeout=1))
            assert first["type"] == "server.status"
            assert first["seq"] == 0
            assert first["payload"]["accepts_prompts"] is True

            second = json.loads(await asyncio.wait_for(websocket.recv(), timeout=1))
            assert second["type"] == "replayed"

            await websocket.send(json.dumps({"type": "client.run.start", "prompt": "go"}))
            await websocket.send("not json")
            await websocket.send(json.dumps({"type": "client.run.start", "prompt": "again"}))

            for _ in range(50):
                if len(received) >= 2:
                    break
                await asyncio.sleep(0.02)
    finally:
        await server.stop()

    assert received == [
        {"type": "client.run.start", "prompt": "go"},
        {"type": "client.run.start", "prompt": "again"},
    ]
