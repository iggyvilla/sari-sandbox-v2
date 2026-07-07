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
    message_items: list[dict[str, Any]] = field(default_factory=list)
    usage: dict[str, int] | None = None


def _normalize_usage(raw: Any) -> dict[str, int] | None:
    """Map provider usage objects onto input/output/total token counts."""

    data = _as_dict(raw)
    if not data:
        return None

    def _first_int(*keys: str) -> int | None:
        for key in keys:
            value = data.get(key)
            if isinstance(value, (int, float)) and not isinstance(value, bool):
                return int(value)
        return None

    input_tokens = _first_int("input_tokens", "prompt_tokens")
    output_tokens = _first_int("output_tokens", "completion_tokens")
    total_tokens = _first_int("total_tokens")
    if input_tokens is None and output_tokens is None and total_tokens is None:
        return None
    if total_tokens is None:
        total_tokens = (input_tokens or 0) + (output_tokens or 0)
    return {
        "input_tokens": input_tokens or 0,
        "output_tokens": output_tokens or 0,
        "total_tokens": total_tokens,
    }


class ResponseStreamAccumulator:
    def __init__(self) -> None:
        self.text_parts: list[str] = []
        self.calls: dict[str, StreamedToolCall] = {}
        self.index_to_id: dict[int, str] = {}
        self.raw_events: list[dict[str, Any]] = []
        self.usage: dict[str, int] | None = None

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
        text = "".join(self.text_parts)
        calls = list(self.calls.values())
        message_items: list[dict[str, Any]] = []
        if text:
            message_items.append({"role": "assistant", "content": text})
        message_items.extend(
            {
                "type": "function_call",
                "id": call.id,
                "call_id": call.call_id or call.id,
                "name": call.name,
                "arguments": call.arguments_json or "{}",
            }
            for call in calls
        )
        return ResponseStreamResult(
            text=text,
            tool_calls=[call.to_dict() for call in calls],
            raw_events=self.raw_events,
            message_items=message_items,
            usage=self.usage,
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
        if response.get("usage"):
            self.usage = _normalize_usage(response["usage"])
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
    def __init__(
        self,
        *,
        model: str,
        api_key: str | None = None,
        base_url: str | None = None,
        api_style: str = "responses",
        client: Any | None = None,
        debug_hub: Any | None = None,
    ) -> None:
        self.model = model
        self.api_style = api_style
        self.debug_hub = debug_hub
        if client is None:
            from openai import AsyncOpenAI

            client_kwargs = {}
            if api_key:
                client_kwargs["api_key"] = api_key
            if base_url:
                client_kwargs["base_url"] = base_url
            client = AsyncOpenAI(**client_kwargs)
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
        stage: str | None = None,
    ) -> ResponseStreamResult:
        await self._publish_llm_request(
            input_items=input_items,
            tools=tools,
            instructions=instructions,
            stage=stage,
        )
        if self.api_style == "chat_completions":
            result = await self._run_chat_completions_streamed(
                input_items=input_items,
                tools=tools,
                instructions=instructions,
                stage=stage,
            )
            await self._publish_llm_completed(result, stage=stage)
            return result
        if self.api_style != "responses":
            raise ValueError(
                "SARI_OPENAI_API_STYLE must be 'responses' or 'chat_completions', "
                f"got {self.api_style!r}"
            )

        accumulator = ResponseStreamAccumulator()
        async for event in self.stream(input_items=input_items, tools=tools, instructions=instructions):
            await self._publish_stream_event(event, stage=stage, api_style="responses")
            accumulator.process_event(event)
        result = accumulator.result()
        await self._publish_llm_completed(result, stage=stage)
        return result

    async def _run_chat_completions_streamed(
        self,
        *,
        input_items: list[dict[str, Any]],
        tools: list[dict[str, Any]] | None = None,
        instructions: str | None = None,
        stage: str | None = None,
    ) -> ResponseStreamResult:
        accumulator = ChatCompletionsStreamAccumulator()
        create_kwargs: dict[str, Any] = {
            "model": self.model,
            "messages": _to_chat_messages(input_items, instructions, model=self.model),
            "stream": True,
            "stream_options": {"include_usage": True},
        }
        chat_tools = [_to_chat_tool(tool) for tool in tools or []]
        if chat_tools:
            create_kwargs["tools"] = chat_tools

        stream = await self.client.chat.completions.create(**create_kwargs)
        async for event in stream:
            await self._publish_stream_event(event, stage=stage, api_style="chat_completions")
            accumulator.process_event(event)
        return accumulator.result()

    async def _publish_llm_request(
        self,
        *,
        input_items: list[dict[str, Any]],
        tools: list[dict[str, Any]] | None,
        instructions: str | None,
        stage: str | None,
    ) -> None:
        if not self._debug_enabled():
            return

        payload: dict[str, Any] = {
            "model": self.model,
            "api_style": self.api_style,
            "tool_names": [tool.get("name") for tool in tools or []],
            "input_item_count": len(input_items),
            "instructions_present": bool(instructions),
        }
        if self.debug_hub.include_prompts:
            payload["input_items"] = input_items
            payload["instructions"] = instructions

        await self.debug_hub.publish(
            "llm.request.started",
            stage=stage,
            summary=f"Started LLM request to {self.model}",
            payload=payload,
        )

    async def _publish_stream_event(self, event: Any, *, stage: str | None, api_style: str) -> None:
        if not self._debug_enabled():
            return

        event_dict = _as_dict(event)
        if not event_dict:
            event_dict = {"type": _event_get(event, "type", "")}

        if self.debug_hub.include_raw_llm_events:
            await self.debug_hub.publish(
                "llm.raw_event",
                stage=stage,
                summary=str(event_dict.get("type") or api_style),
                payload={"api_style": api_style, "event": event_dict},
            )

        for text in _text_deltas_from_event(event, event_dict, api_style):
            await self.debug_hub.publish(
                "llm.text.delta",
                stage=stage,
                summary="LLM text delta",
                payload={"text": text},
            )

        for text in _reasoning_deltas_from_event(event_dict):
            await self.debug_hub.publish(
                "llm.reasoning.delta",
                stage=stage,
                summary="Provider-exposed reasoning delta",
                payload={"text": text},
            )

    async def _publish_llm_completed(
        self,
        result: ResponseStreamResult,
        *,
        stage: str | None,
    ) -> None:
        if not self._debug_enabled() or not result.text:
            return
        await self.debug_hub.publish(
            "llm.text.completed",
            stage=stage,
            summary="LLM text completed",
            payload={"text": result.text},
        )

    def _debug_enabled(self) -> bool:
        return self.debug_hub is not None and getattr(self.debug_hub, "enabled", False)


class ChatCompletionsStreamAccumulator:
    def __init__(self) -> None:
        self.text_parts: list[str] = []
        self.calls_by_index: dict[int, StreamedToolCall] = {}
        self.raw_events: list[dict[str, Any]] = []
        self.usage: dict[str, int] | None = None

    def process_event(self, event: Any) -> None:
        event_dict = _as_dict(event)
        self.raw_events.append(event_dict)
        # With stream_options.include_usage the final chunk carries usage and
        # has an empty choices list.
        if event_dict.get("usage"):
            self.usage = _normalize_usage(event_dict["usage"])
        for choice in event_dict.get("choices", []) or []:
            delta = _as_dict(choice.get("delta", {}))
            content = delta.get("content")
            if content:
                self.text_parts.append(content)
            for tool_call in delta.get("tool_calls", []) or []:
                self._handle_tool_call_delta(_as_dict(tool_call))

    def result(self) -> ResponseStreamResult:
        text = "".join(self.text_parts)
        calls = [self.calls_by_index[index] for index in sorted(self.calls_by_index)]
        message: dict[str, Any] = {"role": "assistant", "content": text or None}
        if calls:
            message["tool_calls"] = [
                {
                    "id": call.call_id or call.id,
                    "type": "function",
                    "function": {
                        "name": call.name,
                        "arguments": call.arguments_json or "{}",
                    },
                }
                for call in calls
            ]

        return ResponseStreamResult(
            text=text,
            tool_calls=[call.to_dict() for call in calls],
            raw_events=self.raw_events,
            message_items=[message] if text or calls else [],
            usage=self.usage,
        )

    def _handle_tool_call_delta(self, tool_call: dict[str, Any]) -> None:
        index = int(tool_call.get("index", len(self.calls_by_index)))
        call = self.calls_by_index.get(index)
        if call is None:
            call_id = tool_call.get("id") or f"tool_call_{index}"
            call = StreamedToolCall(id=call_id, call_id=call_id, name="")
            self.calls_by_index[index] = call
        elif tool_call.get("id"):
            call.id = tool_call["id"]
            call.call_id = tool_call["id"]

        function = _as_dict(tool_call.get("function", {}))
        if function.get("name"):
            call.name += function["name"]
        if function.get("arguments"):
            call.arguments_json += function["arguments"]


def _to_chat_messages(
    input_items: list[dict[str, Any]],
    instructions: str | None,
    *,
    model: str = "",
) -> list[dict[str, Any]]:
    messages: list[dict[str, Any]] = []
    if instructions:
        messages.append({"role": "system", "content": instructions})

    for item in input_items:
        if item.get("type") == "function_call_output":
            messages.append(
                {
                    "role": "tool",
                    "tool_call_id": item.get("call_id"),
                    "content": item.get("output", ""),
                }
            )
            continue

        role = item.get("role")
        if role in {"system", "user", "assistant", "tool"}:
            message = {key: value for key, value in item.items() if key != "type"}
            if "content" in message:
                message["content"] = _normalize_message_content_for_model(
                    message["content"],
                    model,
                )
            messages.append(message)

    return messages


def _normalize_message_content_for_model(content: Any, model: str) -> Any:
    if not _is_qwen_model(model) or not isinstance(content, list):
        return content

    return [
        _normalize_qwen_image_block(block) if isinstance(block, dict) else block
        for block in content
    ]


def _normalize_qwen_image_block(block: dict[str, Any]) -> dict[str, Any]:
    if block.get("type") == "input_image" and isinstance(block.get("image_url"), str):
        return {"type": "image_url", "image_url": {"url": block["image_url"]}}
    if block.get("type") == "image_url" and isinstance(block.get("image_url"), str):
        return {"type": "image_url", "image_url": {"url": block["image_url"]}}
    return block


def _to_chat_tool(tool: dict[str, Any]) -> dict[str, Any]:
    return {
        "type": "function",
        "function": {
            "name": tool["name"],
            "description": tool.get("description", ""),
            "parameters": tool.get("parameters", {"type": "object", "properties": {}}),
        },
    }


def _is_qwen_model(model: str) -> bool:
    return "qwen" in model.lower()


def _text_deltas_from_event(event: Any, event_dict: dict[str, Any], api_style: str) -> list[str]:
    if api_style == "responses" and _event_get(event, "type", "") == "response.output_text.delta":
        delta = _event_get(event, "delta", "")
        return [delta] if delta else []

    if api_style != "chat_completions":
        return []

    deltas: list[str] = []
    for choice in event_dict.get("choices", []) or []:
        delta = _as_dict(choice.get("delta", {}))
        content = delta.get("content")
        if content:
            deltas.append(content)
    return deltas


def _reasoning_deltas_from_event(event_dict: dict[str, Any]) -> list[str]:
    """Extract reasoning text only when the provider explicitly exposes it."""

    deltas: list[str] = []
    event_type = str(event_dict.get("type", "")).lower()
    if "reasoning" in event_type:
        for key in ("delta", "text", "summary", "reasoning", "reasoning_content"):
            _append_text_value(deltas, event_dict.get(key))

    for choice in event_dict.get("choices", []) or []:
        delta = _as_dict(choice.get("delta", {}))
        for key in ("reasoning_content", "reasoning", "reasoning_summary"):
            _append_text_value(deltas, delta.get(key))

    return deltas


def _append_text_value(target: list[str], value: Any) -> None:
    if isinstance(value, str) and value:
        target.append(value)
    elif isinstance(value, dict):
        for nested in value.values():
            _append_text_value(target, nested)
    elif isinstance(value, list):
        for nested in value:
            _append_text_value(target, nested)


def image_content_block(model: str, mime_type: str, base64_data: str) -> dict[str, Any]:
    url = f"data:{mime_type};base64,{base64_data}"
    if _is_qwen_model(model):
        return {"type": "image_url", "image_url": {"url": url}}
    return {"type": "input_image", "image_url": url}
