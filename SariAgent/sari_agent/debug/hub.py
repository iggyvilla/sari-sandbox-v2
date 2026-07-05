"""In-process broadcaster for SariAgent debug events."""

from __future__ import annotations

import asyncio
import uuid
from collections import deque
from pathlib import Path
from typing import Any

from sari_agent.debug.events import DebugEvent


class DebugHub:
    """Publish debug events to websocket clients and a per-run JSONL file.

    The hub is intentionally independent from the website.  Agent code emits
    semantic events here; the browser simply subscribes and renders them.
    """

    def __init__(
        self,
        *,
        enabled: bool = False,
        run_id: str | None = None,
        replay_limit: int = 1000,
        jsonl_dir: Path | None = None,
        include_raw_llm_events: bool = True,
        include_prompts: bool = True,
        image_max_edge: int = 512,
        subscriber_queue_size: int = 1000,
    ) -> None:
        self.enabled = enabled
        self.run_id = run_id or uuid.uuid4().hex
        self.replay_limit = max(0, replay_limit)
        self.include_raw_llm_events = include_raw_llm_events
        self.include_prompts = include_prompts
        self.image_max_edge = max(1, image_max_edge)
        self.subscriber_queue_size = max(1, subscriber_queue_size)

        self._seq = 0
        self._events: deque[DebugEvent] = deque(maxlen=self.replay_limit)
        self._subscribers: set[asyncio.Queue[str]] = set()
        self._lock = asyncio.Lock()
        self._jsonl_dir = jsonl_dir
        self.jsonl_path = (
            (jsonl_dir / f"{self.run_id}.jsonl") if enabled and jsonl_dir is not None else None
        )

    def begin_run(self, run_id: str | None = None) -> str:
        """Rotate to a fresh run identifier (serve mode runs several per process).

        The sequence counter stays monotonic and the replay buffer keeps events
        from earlier runs; subscribers group the stream by run_id.
        """

        self.run_id = run_id or uuid.uuid4().hex
        self.jsonl_path = (
            (self._jsonl_dir / f"{self.run_id}.jsonl")
            if self.enabled and self._jsonl_dir is not None
            else None
        )
        return self.run_id

    async def publish(
        self,
        event_type: str,
        *,
        stage: str | None = None,
        level: str = "info",
        summary: str = "",
        payload: dict[str, Any] | None = None,
    ) -> DebugEvent | None:
        """Create and broadcast an event.

        This is a no-op when debug mode is disabled so callers can keep their
        instrumentation simple and unconditional.
        """

        if not self.enabled:
            return None

        async with self._lock:
            self._seq += 1
            event = DebugEvent(
                seq=self._seq,
                run_id=self.run_id,
                type=event_type,
                stage=stage,
                level=level,
                summary=summary,
                payload=payload or {},
            )
            self._events.append(event)
            message = event.to_json()
            self._append_jsonl(message)
            for queue in tuple(self._subscribers):
                self._put_latest(queue, message)
            return event

    async def add_subscriber(self, *, replay: bool = True) -> asyncio.Queue[str]:
        """Register a websocket client queue and optionally seed replay events."""

        queue: asyncio.Queue[str] = asyncio.Queue(maxsize=self.subscriber_queue_size)
        if replay:
            for event in self._events:
                self._put_latest(queue, event.to_json())
        self._subscribers.add(queue)
        return queue

    def remove_subscriber(self, queue: asyncio.Queue[str]) -> None:
        self._subscribers.discard(queue)

    def replay_events(self) -> list[DebugEvent]:
        return list(self._events)

    @property
    def subscriber_count(self) -> int:
        return len(self._subscribers)

    def _append_jsonl(self, message: str) -> None:
        if self.jsonl_path is None:
            return
        self.jsonl_path.parent.mkdir(parents=True, exist_ok=True)
        with self.jsonl_path.open("a", encoding="utf-8") as file:
            file.write(message)
            file.write("\n")

    @staticmethod
    def _put_latest(queue: asyncio.Queue[str], message: str) -> None:
        """Drop the oldest queued event when a subscriber falls behind."""

        if queue.full():
            try:
                queue.get_nowait()
            except asyncio.QueueEmpty:
                pass
        queue.put_nowait(message)
