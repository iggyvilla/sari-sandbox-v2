"""Embedded websocket server for browser-side debug subscribers."""

from __future__ import annotations

import asyncio
import json
from typing import Any, Awaitable, Callable

from sari_agent.debug.events import SCHEMA_VERSION, utc_timestamp
from sari_agent.debug.hub import DebugHub


class DebugWebSocketServer:
    """Serve DebugHub events at ws://host:port/debug.

    v1 streamed events server-to-client only.  v1.1 additionally accepts JSON
    text frames from clients (e.g. ``{"type": "client.run.start", "prompt":
    "..."}``) and forwards parsed dicts to ``on_client_message``; anything the
    handler does not understand is ignored so v1 clients stay harmless.
    """

    def __init__(
        self,
        hub: DebugHub,
        *,
        host: str = "localhost",
        port: int = 8765,
        on_client_message: Callable[[dict[str, Any]], Awaitable[None]] | None = None,
        status_provider: Callable[[], dict[str, Any]] | None = None,
    ) -> None:
        self.hub = hub
        self.host = host
        self.port = port
        self.on_client_message = on_client_message
        self.status_provider = status_provider
        self._server: Any | None = None

    @property
    def url(self) -> str:
        return f"ws://{self.host}:{self.port}/debug"

    async def start(self) -> None:
        if not self.hub.enabled or self._server is not None:
            return

        import websockets

        self._server = await websockets.serve(self._handle_client, self.host, self.port)

    async def stop(self) -> None:
        if self._server is None:
            return
        self._server.close()
        await self._server.wait_closed()
        self._server = None

    async def _handle_client(self, websocket: Any, path: str | None = None) -> None:
        route = self._route_for(websocket, path)
        if route not in (None, "/debug"):
            await websocket.close(code=1008, reason="Use /debug")
            return

        if self.status_provider is not None:
            # Direct status message (seq 0) so late joiners know the server
            # state even when the replay buffer has already overflowed.
            await websocket.send(self._status_message())

        queue = await self.hub.add_subscriber(replay=True)
        sender = asyncio.create_task(self._send_events(websocket, queue))
        receiver = asyncio.create_task(self._receive_client_messages(websocket))
        try:
            done, pending = await asyncio.wait(
                {sender, receiver},
                return_when=asyncio.FIRST_COMPLETED,
            )
            for task in pending:
                task.cancel()
            for task in done:
                task.result()
        finally:
            self.hub.remove_subscriber(queue)

    def _status_message(self) -> str:
        assert self.status_provider is not None
        return json.dumps(
            {
                "schema_version": SCHEMA_VERSION,
                "seq": 0,
                "run_id": self.hub.run_id,
                "timestamp": utc_timestamp(),
                "type": "server.status",
                "stage": None,
                "level": "info",
                "summary": "Server status",
                "payload": self.status_provider(),
            },
            ensure_ascii=False,
        )

    @staticmethod
    async def _send_events(websocket: Any, queue: asyncio.Queue[str]) -> None:
        while True:
            await websocket.send(await queue.get())

    async def _receive_client_messages(self, websocket: Any) -> None:
        async for raw in websocket:
            # Reading keeps close frames flowing so disconnected browser tabs
            # are cleaned up even when no handler is registered.
            if self.on_client_message is None:
                continue
            try:
                message = json.loads(raw)
            except (TypeError, ValueError):
                continue
            if not isinstance(message, dict):
                continue
            try:
                await self.on_client_message(message)
            except Exception:  # noqa: BLE001 - a bad message must not drop the socket
                continue

    @staticmethod
    def _route_for(websocket: Any, path: str | None) -> str | None:
        if path is not None:
            return path
        request = getattr(websocket, "request", None)
        if request is not None:
            return getattr(request, "path", None)
        return getattr(websocket, "path", None)
