"""Context variables that correlate nested debug events."""

from __future__ import annotations

from contextlib import contextmanager
from contextvars import ContextVar
from dataclasses import dataclass
from typing import Iterator


@dataclass(frozen=True, slots=True)
class ToolTraceContext:
    call_id: str
    tool_name: str
    arguments_json: str | None = None

    def to_dict(self) -> dict[str, str | None]:
        return {
            "call_id": self.call_id,
            "tool_name": self.tool_name,
            "arguments_json": self.arguments_json,
        }


_current_tool_trace: ContextVar[ToolTraceContext | None] = ContextVar(
    "sari_debug_tool_trace",
    default=None,
)


def get_tool_trace() -> ToolTraceContext | None:
    return _current_tool_trace.get()


@contextmanager
def tool_trace(trace: ToolTraceContext) -> Iterator[None]:
    token = _current_tool_trace.set(trace)
    try:
        yield
    finally:
        _current_tool_trace.reset(token)
