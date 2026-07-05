"""Stable JSON event contract for the SariAgent debug stream."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 1


def utc_timestamp() -> str:
    """Return an ISO-8601 UTC timestamp that is friendly to browser parsers."""

    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def json_safe(value: Any) -> Any:
    """Coerce debug payloads into plain JSON values.

    OpenAI SDK events, pathlib paths, exceptions, and Unity responses can carry
    objects that json.dumps cannot serialize directly.  The debug channel should
    never crash the agent, so unknown values are represented with strings.
    """

    try:
        return json.loads(json.dumps(value, default=_json_default))
    except (TypeError, ValueError):
        return str(value)


def _json_default(value: Any) -> Any:
    if isinstance(value, Path):
        return str(value)
    if isinstance(value, bytes):
        return {"kind": "bytes", "bytes": len(value)}
    if hasattr(value, "model_dump"):
        return value.model_dump()
    if hasattr(value, "__dict__"):
        return dict(value.__dict__)
    return str(value)


@dataclass(slots=True)
class DebugEvent:
    """One websocket/JSONL event consumed by the future debug website."""

    seq: int
    run_id: str
    type: str
    timestamp: str = field(default_factory=utc_timestamp)
    stage: str | None = None
    level: str = "info"
    summary: str = ""
    payload: dict[str, Any] = field(default_factory=dict)
    schema_version: int = SCHEMA_VERSION

    def to_dict(self) -> dict[str, Any]:
        return {
            "schema_version": self.schema_version,
            "seq": self.seq,
            "run_id": self.run_id,
            "timestamp": self.timestamp,
            "type": self.type,
            "stage": self.stage,
            "level": self.level,
            "summary": self.summary,
            "payload": json_safe(self.payload),
        }

    def to_json(self) -> str:
        return json.dumps(self.to_dict(), ensure_ascii=False)
