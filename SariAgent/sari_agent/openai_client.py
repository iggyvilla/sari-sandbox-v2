"""Async Responses API streaming wrapper and event normalization."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Any, AsyncIterator


def _event_get(event: Any, key: str, default: Any = None) -> Any:
    if isinstance(event, dict):
        return event.get(key, default)
    return getattr(event, key, default)


def _as_dict(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    if hasattr(value, "model_dump"):
        return value.model_dump()
    if hasattr(value, "__dict__"):
        return dict(value.__dict__)
    return {}


@dataclass(slots=True)
class StreamedToolCall:
    id: str
    name: str
    arguments_json: str = ""
    call_id: str | None = None

    def to_dict(self) -> dict[str, Any]:
        try:
            arguments = json.loads(self.arguments_json or "{}")
        except json.JSONDecodeError:
            arguments = {"_raw": self.arguments_json}
        return {
            "id": self.id,
            "call_id": self.call_id or self.id,
            "name": self.name,
            "arguments": arguments,
            "arguments_json": self.arguments_json,
        }


@dataclass(slots=True)
class ResponseStreamResult:
    text: str = ""
    tool_calls: list[dict[str, Any]] = field(default_factory=list)
    raw_events: list[dict[str, Any]] = field(default_factory=list)


class ResponseStreamAccumulator:
    def __init__(self) -> None:
        self.text_parts: list[str] = []
        self.calls: dict[str, StreamedToolCall] = {}
        self.index_to_id: dict[int, str] = {}
        self.raw_events: list[dict[str, Any]] = []

    def process_event(self, event: Any) -> None:
        event_type = _event_get(event, "type", "")
        event_dict = _as_dict(event)
        self.raw_events.append(event_dict or {"type": event_type})

        if event_type == "response.output_text.delta":
            self.text_parts.append(_event_get(event, "delta", ""))
            return

        if event_type == "response.output_item.added":
            self._handle_output_item_added(event)
            return

        if event_type == "response.function_call_arguments.delta":
            key = self._call_key_for_event(event)
            if key is not None:
                self.calls[key].arguments_json += _event_get(event, "delta", "")
            return

        if event_type == "response.output_item.done":
            self._handle_output_item_done(event)
            return

        if event_type == "response.completed":
            self._handle_completed(event)

    def result(self) -> ResponseStreamResult:
        return ResponseStreamResult(
            text="".join(self.text_parts),
            tool_calls=[call.to_dict() for call in self.calls.values()],
            raw_events=self.raw_events,
        )

    def _handle_output_item_added(self, event: Any) -> None:
        item = _as_dict(_event_get(event, "item", {}))
        if item.get("type") != "function_call":
            return
        item_id = item.get("id") or item.get("call_id") or str(_event_get(event, "output_index", len(self.calls)))
        call = StreamedToolCall(
            id=item_id,
            call_id=item.get("call_id"),
            name=item.get("name", ""),
            arguments_json=item.get("arguments", "") or "",
        )
        self.calls[item_id] = call
        output_index = _event_get(event, "output_index", None)
        if output_index is not None:
            self.index_to_id[int(output_index)] = item_id

    def _handle_output_item_done(self, event: Any) -> None:
        item = _as_dict(_event_get(event, "item", {}))
        if item.get("type") != "function_call":
            return
        item_id = item.get("id") or item.get("call_id")
        if item_id is None:
            return
        call = self.calls.get(item_id)
        if call is None:
            call = StreamedToolCall(id=item_id, call_id=item.get("call_id"), name=item.get("name", ""))
            self.calls[item_id] = call
        call.name = item.get("name") or call.name
        call.call_id = item.get("call_id") or call.call_id
        if item.get("arguments"):
            call.arguments_json = item["arguments"]

    def _handle_completed(self, event: Any) -> None:
        response = _as_dict(_event_get(event, "response", {}))
        for output in response.get("output", []) or []:
            if output.get("type") == "message":
                for content in output.get("content", []) or []:
                    if content.get("type") in {"output_text", "text"} and content.get("text"):
                        text = content["text"]
                        if text not in self.text_parts:
                            self.text_parts.append(text)
            elif output.get("type") == "function_call":
                item_id = output.get("id") or output.get("call_id")
                if item_id and item_id not in self.calls:
                    self.calls[item_id] = StreamedToolCall(
                        id=item_id,
                        call_id=output.get("call_id"),
                        name=output.get("name", ""),
                        arguments_json=output.get("arguments", "") or "",
                    )

    def _call_key_for_event(self, event: Any) -> str | None:
        item_id = _event_get(event, "item_id", None)
        if item_id in self.calls:
            return item_id
        output_index = _event_get(event, "output_index", None)
        if output_index is not None:
            mapped = self.index_to_id.get(int(output_index))
            if mapped is not None:
                return mapped
        if len(self.calls) == 1:
            return next(iter(self.calls))
        return None


class ResponsesClient:
    def __init__(self, *, model: str, client: Any | None = None) -> None:
        self.model = model
        if client is None:
            from openai import AsyncOpenAI

            client = AsyncOpenAI()
        self.client = client

    async def stream(
        self,
        *,
        input_items: list[dict[str, Any]],
        tools: list[dict[str, Any]] | None = None,
        instructions: str | None = None,
    ) -> AsyncIterator[Any]:
        async with self.client.responses.stream(
            model=self.model,
            input=input_items,
            tools=tools or [],
            instructions=instructions,
        ) as stream:
            async for event in stream:
                yield event

    async def run_streamed(
        self,
        *,
        input_items: list[dict[str, Any]],
        tools: list[dict[str, Any]] | None = None,
        instructions: str | None = None,
    ) -> ResponseStreamResult:
        accumulator = ResponseStreamAccumulator()
        async for event in self.stream(input_items=input_items, tools=tools, instructions=instructions):
            accumulator.process_event(event)
        return accumulator.result()


def image_content_block(model: str, mime_type: str, base64_data: str) -> dict[str, Any]:
    url = f"data:{mime_type};base64,{base64_data}"
    if "qwen" in model.lower():
        return {"type": "image_url", "image_url": {"url": url}}
    return {"type": "input_image", "image_url": url}
