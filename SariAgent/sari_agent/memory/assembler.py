"""Memory assembly hooks for completed sub-goals."""

from __future__ import annotations

import json
from copy import deepcopy
from dataclasses import dataclass
from typing import Any

from sari_agent.context import AgentContext


COMPACTION_STAGE = "memory_assembly"
SUMMARY_PREFIX = "Context summary from completed sub-goals:\n"
OLDER_IMAGE_MARKER = (
    "Earlier screenshot omitted; visual details should be captured in the summary."
)
RETAINED_IMAGE_MARKER = "Recent screenshot retained below for visual reference."

COMPACTION_INSTRUCTIONS = """You compact SARI agent context between sub-goals.
Return a concise plain-text handoff summary only.

Preserve:
- completed and failed sub-goals, with outcomes
- user intent and remaining constraints
- important store observations, item locations, object state, and navigation state
- tool results, failed attempts, decisions, and assumptions that affect later work
- visual details from screenshots when useful

Do not include markdown fences or meta commentary about summarizing."""


@dataclass(slots=True)
class MemoryAssemblyResult:
    compacted: bool = False
    usage: dict[str, Any] | None = None
    summary: str = ""
    retained_image_count: int = 0
    omitted_image_count: int = 0
    used_fallback: bool = False
    error: str | None = None


@dataclass(slots=True)
class MessageImageCompactionResult:
    messages: list[dict[str, Any]]
    retained_image_count: int = 0
    omitted_image_count: int = 0


def compact_message_images(
    messages: list[dict[str, Any]],
    *,
    api_style: str,
    image_keep_count: int = 2,
) -> MessageImageCompactionResult:
    """Replace older image blocks while preserving the latest visual context."""

    kept_image_indices = _kept_image_indices(messages, image_keep_count)
    compacted_messages: list[dict[str, Any]] = []
    image_index = 0
    omitted_count = 0

    for message in messages:
        content = message.get("content")
        if not isinstance(content, list):
            compacted_messages.append(deepcopy(message))
            continue

        next_content: list[Any] = []
        for block in content:
            if isinstance(block, dict) and _is_image_block(block):
                if image_index in kept_image_indices:
                    next_content.append(deepcopy(block))
                else:
                    next_content.append(_text_block(OLDER_IMAGE_MARKER, api_style))
                    omitted_count += 1
                image_index += 1
            else:
                next_content.append(deepcopy(block))

        next_message = {
            key: deepcopy(value)
            for key, value in message.items()
            if key != "content"
        }
        next_message["content"] = next_content
        compacted_messages.append(next_message)

    return MessageImageCompactionResult(
        messages=compacted_messages,
        retained_image_count=len(kept_image_indices),
        omitted_image_count=omitted_count,
    )


class MemoryAssembler:
    """Assemble compact handoff context after completed sub-goals."""

    def __init__(self, client: Any | None = None, *, image_keep_count: int = 2) -> None:
        self.client = client
        self.image_keep_count = max(0, image_keep_count)

    async def after_sub_goal(self, context: AgentContext) -> MemoryAssemblyResult:
        context.metadata.setdefault("memory_assembly_events", 0)
        context.metadata["memory_assembly_events"] += 1

        if not self._should_compact(context):
            return MemoryAssemblyResult()

        transcript, retained_image_messages, retained_count, omitted_count = (
            self._build_compaction_context(context)
        )
        input_items = [
            {"role": "user", "content": self._compaction_prompt(context, transcript)},
            *retained_image_messages,
        ]

        summary = ""
        usage: dict[str, Any] | None = None
        error: str | None = None
        used_fallback = False

        await _publish_debug(
            context,
            "pipeline.memory.compaction.started",
            summary="Started context compaction",
            payload={
                "input_item_count": len(input_items),
                "retained_image_count": retained_count,
                "omitted_image_count": omitted_count,
            },
        )

        if self.client is not None:
            try:
                result = await self.client.run_streamed(
                    input_items=input_items,
                    tools=[],
                    instructions=COMPACTION_INSTRUCTIONS,
                    stage=COMPACTION_STAGE,
                )
                usage = result.usage
                context.record_usage(COMPACTION_STAGE, usage)
                summary = result.text.strip()
                if not summary:
                    error = "Compaction LLM returned an empty summary."
            except Exception as exc:  # noqa: BLE001 - compaction must not block the run
                error = f"{type(exc).__name__}: {exc}"

        if not summary:
            used_fallback = True
            summary = self._fallback_summary(context)

        context.messages = [
            {"role": "user", "content": f"{SUMMARY_PREFIX}{summary}"},
            *retained_image_messages,
        ]
        context.metadata["last_context_compaction"] = {
            "sub_goal_index": context.current_sub_goal_index,
            "retained_image_count": retained_count,
            "omitted_image_count": omitted_count,
            "used_fallback": used_fallback,
            "error": error,
        }

        if error:
            await _publish_debug(
                context,
                "pipeline.memory.compaction.failed",
                level="warning",
                summary="Context compaction fell back to deterministic summary",
                payload={
                    "error": error,
                    "retained_image_count": retained_count,
                    "omitted_image_count": omitted_count,
                },
            )
        else:
            await _publish_debug(
                context,
                "pipeline.memory.compaction.completed",
                summary="Compacted context for next sub-goal",
                payload={
                    "summary": summary,
                    "retained_image_count": retained_count,
                    "omitted_image_count": omitted_count,
                    "used_fallback": used_fallback,
                },
            )

        return MemoryAssemblyResult(
            compacted=True,
            usage=usage,
            summary=summary,
            retained_image_count=retained_count,
            omitted_image_count=omitted_count,
            used_fallback=used_fallback,
            error=error,
        )

    def _should_compact(self, context: AgentContext) -> bool:
        current = context.current_sub_goal
        if current is None or current.status not in {"completed", "failed"}:
            return False
        if context.current_sub_goal_index + 1 >= len(context.sub_goals):
            return False

        turn_limit_stop = context.metadata.get("sub_goal_turn_limit_stop")
        return not (
            isinstance(turn_limit_stop, dict)
            and turn_limit_stop.get("sub_goal_index") == context.current_sub_goal_index
        )

    def _build_compaction_context(
        self,
        context: AgentContext,
    ) -> tuple[str, list[dict[str, Any]], int, int]:
        kept_image_indices = self._kept_image_indices(context.messages)
        transcript = self._transcript_from_messages(context.messages, kept_image_indices)
        retained_messages = self._retained_image_messages(context, kept_image_indices)
        retained_count = len(kept_image_indices)
        omitted_count = max(0, _image_count(context.messages) - retained_count)
        return transcript, retained_messages, retained_count, omitted_count

    def _compaction_prompt(self, context: AgentContext, transcript: str) -> str:
        sub_goal_lines = []
        for index, sub_goal in enumerate(context.sub_goals):
            result = f" Result: {sub_goal.result}" if sub_goal.result else ""
            sub_goal_lines.append(
                f"{index + 1}. [{sub_goal.status}] {sub_goal.description}.{result}"
            )

        sub_goal_text = "\n".join(sub_goal_lines)
        image_note = (
            "\n\nThe latest retained screenshot image message(s) follow this transcript."
            if self._kept_image_indices(context.messages)
            else ""
        )
        return (
            "Compact this SARI run context for the next sub-goal.\n\n"
            f"Original user request:\n{context.user_input}\n\n"
            "Sub-goals:\n"
            f"{sub_goal_text}\n\n"
            "Transcript:\n"
            f"{transcript}"
            f"{image_note}"
        )

    def _fallback_summary(self, context: AgentContext) -> str:
        lines = [f"Original user request: {context.user_input}", "Completed sub-goals:"]
        completed = context.sub_goals[: context.current_sub_goal_index + 1]
        for index, sub_goal in enumerate(completed):
            result = sub_goal.result or "No result recorded."
            lines.append(
                f"{index + 1}. {sub_goal.description} - {sub_goal.status}: {result}"
            )
        return "\n".join(lines)

    def _kept_image_indices(self, messages: list[dict[str, Any]]) -> set[int]:
        return _kept_image_indices(messages, self.image_keep_count)

    def _transcript_from_messages(
        self,
        messages: list[dict[str, Any]],
        kept_image_indices: set[int],
    ) -> str:
        lines: list[str] = []
        image_index = 0
        for index, message in enumerate(messages, start=1):
            label, text, image_index = self._message_to_transcript(
                message,
                image_index,
                kept_image_indices,
            )
            if text:
                lines.append(f"{index}. {label}: {text}")
        return "\n".join(lines) if lines else "(No prior messages.)"

    def _message_to_transcript(
        self,
        message: dict[str, Any],
        image_index: int,
        kept_image_indices: set[int],
    ) -> tuple[str, str, int]:
        message_type = message.get("type")
        if message_type == "function_call":
            name = message.get("name", "unknown_tool")
            call_id = message.get("call_id") or message.get("id")
            arguments = message.get("arguments", "")
            return (
                "assistant tool call",
                f"{name} call_id={call_id} arguments={arguments}",
                image_index,
            )
        if message_type == "function_call_output":
            call_id = message.get("call_id")
            output = message.get("output", "")
            return "tool output", f"call_id={call_id} output={output}", image_index

        role = str(message.get("role", "message"))
        content, image_index = self._content_to_transcript(
            message.get("content"),
            image_index,
            kept_image_indices,
        )
        tool_calls = message.get("tool_calls")
        if tool_calls:
            tool_call_text = json.dumps(tool_calls, ensure_ascii=False, default=str)
            content = f"{content}\nTool calls: {tool_call_text}".strip()
        if role == "tool" and message.get("tool_call_id"):
            content = f"tool_call_id={message['tool_call_id']} {content}".strip()
        return role, content, image_index

    def _content_to_transcript(
        self,
        content: Any,
        image_index: int,
        kept_image_indices: set[int],
    ) -> tuple[str, int]:
        if isinstance(content, str):
            return content, image_index
        if content is None:
            return "", image_index
        if not isinstance(content, list):
            return json.dumps(content, ensure_ascii=False, default=str), image_index

        parts: list[str] = []
        for block in content:
            if not isinstance(block, dict):
                parts.append(str(block))
                continue
            if _is_image_block(block):
                marker = (
                    RETAINED_IMAGE_MARKER
                    if image_index in kept_image_indices
                    else OLDER_IMAGE_MARKER
                )
                parts.append(f"[image {image_index + 1}] {marker}")
                image_index += 1
                continue
            text = _text_from_block(block)
            if text:
                parts.append(text)
            else:
                parts.append(json.dumps(block, ensure_ascii=False, default=str))
        return "\n".join(parts), image_index

    def _retained_image_messages(
        self,
        context: AgentContext,
        kept_image_indices: set[int],
    ) -> list[dict[str, Any]]:
        retained_messages: list[dict[str, Any]] = []
        image_index = 0
        for message in context.messages:
            content = message.get("content")
            if not isinstance(content, list):
                continue

            retained = False
            next_content: list[Any] = []
            for block in content:
                if isinstance(block, dict) and _is_image_block(block):
                    if image_index in kept_image_indices:
                        next_content.append(deepcopy(block))
                        retained = True
                    else:
                        next_content.append(
                            _text_block(OLDER_IMAGE_MARKER, context.config.openai_api_style)
                        )
                    image_index += 1
                else:
                    next_content.append(deepcopy(block))

            if retained:
                next_message = {
                    key: deepcopy(value)
                    for key, value in message.items()
                    if key != "content"
                }
                next_message["content"] = next_content
                retained_messages.append(next_message)
        return retained_messages


def _content_blocks(message: dict[str, Any]) -> list[Any]:
    content = message.get("content")
    return content if isinstance(content, list) else []


def _image_count(messages: list[dict[str, Any]]) -> int:
    return sum(
        1
        for message in messages
        for block in _content_blocks(message)
        if _is_image_block(block)
    )


def _kept_image_indices(messages: list[dict[str, Any]], image_keep_count: int) -> set[int]:
    image_count = _image_count(messages)
    if image_keep_count <= 0 or image_count == 0:
        return set()
    first_kept = max(0, image_count - image_keep_count)
    return set(range(first_kept, image_count))


def _is_image_block(block: dict[str, Any]) -> bool:
    return block.get("type") in {"input_image", "image_url"} and "image_url" in block


def _text_from_block(block: dict[str, Any]) -> str:
    text = block.get("text")
    if isinstance(text, str):
        return text
    content = block.get("content")
    if isinstance(content, str):
        return content
    return ""


def _text_block(text: str, api_style: str) -> dict[str, str]:
    if api_style == "chat_completions":
        return {"type": "text", "text": text}
    return {"type": "input_text", "text": text}


async def _publish_debug(
    context: AgentContext,
    event_type: str,
    *,
    level: str = "info",
    summary: str = "",
    payload: dict[str, Any] | None = None,
) -> None:
    hub = context.debug_hub
    if hub is not None and getattr(hub, "enabled", False):
        await hub.publish(
            event_type,
            stage=COMPACTION_STAGE,
            level=level,
            summary=summary,
            payload=payload,
        )
