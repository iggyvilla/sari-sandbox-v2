"""Prompt intake for serve mode: the debug website can start agent runs."""

from __future__ import annotations

import asyncio
from typing import Any

from sari_agent.debug import DebugHub


class ServeController:
    """Track run state and accept ``client.run.start`` messages from the UI.

    In ``serve`` mode a prompt submitted while idle starts a run; in ``cli``
    mode the controller only advertises state so attached UIs disable their
    prompt box.
    """

    def __init__(self, hub: DebugHub, *, mode: str = "serve") -> None:
        self.hub = hub
        self.mode = mode
        self.state = "idle"
        self._prompts: asyncio.Queue[str] = asyncio.Queue(maxsize=1)

    @property
    def accepts_prompts(self) -> bool:
        return self.mode == "serve" and self.state == "idle" and self._prompts.empty()

    def status_payload(self) -> dict[str, Any]:
        return {
            "state": self.state,
            "accepts_prompts": self.accepts_prompts,
            "active_run_id": self.hub.run_id if self.state == "running" else None,
            "mode": self.mode,
        }

    async def publish_status(self) -> None:
        await self.hub.publish(
            "server.status",
            summary=f"Server {self.state}",
            payload=self.status_payload(),
        )

    async def next_prompt(self) -> str:
        return await self._prompts.get()

    async def handle_client_message(self, message: dict[str, Any]) -> None:
        if message.get("type") != "client.run.start":
            return

        prompt = message.get("prompt")
        prompt = prompt.strip() if isinstance(prompt, str) else ""
        if not prompt:
            await self._reject("invalid_message", prompt)
            return
        if self.mode != "serve":
            await self._reject("prompts_not_accepted", prompt)
            return
        if self.state == "running":
            await self._reject("run_active", prompt)
            return
        try:
            self._prompts.put_nowait(prompt)
        except asyncio.QueueFull:
            await self._reject("run_active", prompt)

    async def _reject(self, reason: str, prompt: str) -> None:
        await self.hub.publish(
            "server.run.rejected",
            level="warn",
            summary=f"Rejected prompt: {reason}",
            payload={"reason": reason, "prompt": prompt},
        )
