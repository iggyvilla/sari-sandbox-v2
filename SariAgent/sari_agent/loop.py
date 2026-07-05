"""Async agent loop primitives and default stages."""

from __future__ import annotations

import json
import traceback
from abc import ABC, abstractmethod
from enum import Enum
from pathlib import Path
from time import perf_counter
from typing import Any

from sari_agent.context import AgentContext, SubGoal
from sari_agent.debug.images import thumbnail_data_url
from sari_agent.debug.trace import ToolTraceContext, tool_trace
from sari_agent.memory.assembler import MemoryAssembler
from sari_agent.memory.reader import load_markdown_memories
from sari_agent.openai_client import ResponsesClient
from sari_agent.tools.factory import ToolRegistry


class LoopResult(str, Enum):
    ADVANCE = "advance"
    REPEAT = "repeat"
    NEXT_SUB_GOAL = "next_sub_goal"
    STATE_TRANSITION = "state_transition"
    DONE = "done"


class LoopStage(ABC):
    name: str = "stage"

    @abstractmethod
    async def run(self, context: AgentContext) -> LoopResult:
        raise NotImplementedError


class AgentLoop:
    def __init__(self, stages: list[LoopStage], *, max_iterations: int | None = None) -> None:
        self.stages = stages
        self.max_iterations = max_iterations

    async def run(self, context: AgentContext) -> AgentContext:
        max_iterations = self.max_iterations or context.config.max_loop_iterations
        iterations = 0
        stage_index = 0

        await _publish_debug(
            context,
            "run.started",
            summary="SariAgent run started",
            payload={
                "max_iterations": max_iterations,
                "stage_names": [stage.name for stage in self.stages],
                "user_input": context.user_input,
            },
        )

        try:
            while not context.completed and iterations < max_iterations:
                stage = self.stages[stage_index]
                await _publish_debug(
                    context,
                    "pipeline.stage.started",
                    stage=stage.name,
                    summary=f"Started {stage.name}",
                    payload=_pipeline_payload(context, iterations),
                )

                started_at = perf_counter()
                try:
                    result = await stage.run(context)
                except Exception as exc:
                    await _publish_debug(
                        context,
                        "pipeline.stage.error",
                        stage=stage.name,
                        level="error",
                        summary=f"{stage.name} failed",
                        payload={
                            **_pipeline_payload(context, iterations),
                            "duration_ms": _duration_ms(started_at),
                            "error": _exception_payload(exc),
                        },
                    )
                    raise

                iterations += 1
                context.metadata["loop_iterations"] = iterations
                await _publish_debug(
                    context,
                    "pipeline.stage.completed",
                    stage=stage.name,
                    summary=f"Completed {stage.name}",
                    payload={
                        **_pipeline_payload(context, iterations),
                        "result": result.value,
                        "duration_ms": _duration_ms(started_at),
                    },
                )

                if result == LoopResult.DONE:
                    context.completed = True
                    break
                if result == LoopResult.REPEAT:
                    stage_index = 0
                    continue
                if result == LoopResult.NEXT_SUB_GOAL:
                    context.current_sub_goal_index += 1
                    await _publish_debug(
                        context,
                        "pipeline.subgoal.changed",
                        summary="Advanced to next sub-goal",
                        payload=_pipeline_payload(context, iterations),
                    )
                    stage_index = 0
                    continue
                stage_index = (stage_index + 1) % len(self.stages)
        except Exception as exc:
            await _publish_debug(
                context,
                "run.error",
                level="error",
                summary="SariAgent run failed",
                payload={
                    **_pipeline_payload(context, iterations),
                    "error": _exception_payload(exc),
                },
            )
            raise
        else:
            await _publish_debug(
                context,
                "run.completed",
                summary="SariAgent run completed",
                payload={
                    **_pipeline_payload(context, iterations),
                    "completed": context.completed,
                    "hit_iteration_limit": not context.completed and iterations >= max_iterations,
                },
            )

        return context


class LoadMemoryStage(LoopStage):
    name = "load_memory"

    def __init__(self, selected_names: list[str] | None = None) -> None:
        self.selected_names = selected_names

    async def run(self, context: AgentContext) -> LoopResult:
        if context.memory_documents:
            return LoopResult.ADVANCE
        context.memory_documents = load_markdown_memories(context.config.memory_dir, self.selected_names)
        fragments = [context.memory_documents["SARI.md"]]
        fragments.extend(context.current_state.system_prompt_fragments)
        for name, body in context.memory_documents.items():
            if name != "SARI.md":
                fragments.append(f"# Memory: {name}\n\n{body}")
        context.system_prompt = "\n\n".join(fragments)
        return LoopResult.ADVANCE


class PlanSubGoalsStage(LoopStage):
    name = "plan_sub_goals"

    async def run(self, context: AgentContext) -> LoopResult:
        if context.sub_goals:
            return LoopResult.ADVANCE
        parts = [part.strip() for part in context.user_input.split("\n") if part.strip()]
        if len(parts) <= 1:
            context.sub_goals = [SubGoal(description=context.user_input)]
        else:
            context.sub_goals = [SubGoal(description=part) for part in parts]
        await _publish_debug(
            context,
            "pipeline.subgoals.planned",
            stage=self.name,
            summary=f"Planned {len(context.sub_goals)} sub-goal(s)",
            payload={
                "sub_goals": [
                    {"index": index, "description": sub_goal.description, "status": sub_goal.status}
                    for index, sub_goal in enumerate(context.sub_goals)
                ]
            },
        )
        return LoopResult.ADVANCE


class RunModelStage(LoopStage):
    name = "run_model"

    def __init__(self, client: ResponsesClient, tools: list[dict[str, Any]]) -> None:
        self.client = client
        self.tools = tools

    async def run(self, context: AgentContext) -> LoopResult:
        goal = context.current_sub_goal.description if context.current_sub_goal else context.user_input
        if not context.messages:
            context.messages.append({"role": "user", "content": goal})
        input_items = list(context.messages)

        result = await self.client.run_streamed(
            input_items=input_items,
            tools=self._allowed_tools(context),
            instructions=context.system_prompt or None,
            stage=self.name,
        )
        context.response_text = result.text
        context.pending_tool_calls = result.tool_calls
        context.messages.extend(result.message_items)
        return LoopResult.ADVANCE

    def _allowed_tools(self, context: AgentContext) -> list[dict[str, Any]]:
        allowed = context.allowed_tool_names()
        if allowed is None:
            return self.tools
        return [tool for tool in self.tools if tool.get("name") in allowed]


class ExecuteToolsStage(LoopStage):
    name = "execute_tools"

    def __init__(self, registry: ToolRegistry) -> None:
        self.registry = registry

    async def run(self, context: AgentContext) -> LoopResult:
        if not context.pending_tool_calls:
            return LoopResult.ADVANCE
        registry = self.registry.constrained(context.allowed_tool_names())
        context.tool_results = []
        for call in context.pending_tool_calls:
            call_id = call.get("call_id") or call.get("id") or ""
            tool_name = call["name"]
            arguments_json = call.get("arguments_json")
            started_at = perf_counter()

            await _publish_debug(
                context,
                "tool.call.started",
                stage=self.name,
                summary=f"Called {tool_name}",
                payload={
                    "call_id": call_id,
                    "tool_name": tool_name,
                    "arguments": call.get("arguments"),
                    "arguments_json": arguments_json,
                },
            )

            try:
                with tool_trace(
                    ToolTraceContext(
                        call_id=call_id,
                        tool_name=tool_name,
                        arguments_json=arguments_json,
                    )
                ):
                    result = await registry.dispatch(tool_name, call.get("arguments"))
            except Exception as exc:
                await _publish_debug(
                    context,
                    "tool.call.error",
                    stage=self.name,
                    level="error",
                    summary=f"{tool_name} failed",
                    payload={
                        "call_id": call_id,
                        "tool_name": tool_name,
                        "duration_ms": _duration_ms(started_at),
                        "error": _exception_payload(exc),
                    },
                )
                raise

            item = {
                "type": "function_call_output",
                "call_id": call_id,
                "output": json.dumps(result),
            }
            context.tool_results.append(item)
            content = _tool_result_content_blocks(context, result) if _debug_enabled(context) else []
            await _publish_debug(
                context,
                "tool.call.completed",
                stage=self.name,
                summary=f"Completed {tool_name}",
                payload={
                    "call_id": call_id,
                    "tool_name": tool_name,
                    "duration_ms": _duration_ms(started_at),
                    "result": result,
                    "content": content,
                },
            )
            if context.config.openai_api_style == "chat_completions":
                context.messages.append(
                    {
                        "role": "tool",
                        "tool_call_id": item["call_id"],
                        "content": item["output"],
                    }
                )
            else:
                context.messages.append(item)
        context.pending_tool_calls = []
        return LoopResult.REPEAT


class MemoryAssemblyStage(LoopStage):
    name = "memory_assembly"

    def __init__(self, assembler: MemoryAssembler | None = None) -> None:
        self.assembler = assembler or MemoryAssembler()

    async def run(self, context: AgentContext) -> LoopResult:
        current = context.current_sub_goal
        if current is not None and current.status != "completed":
            current.status = "completed"
            current.result = context.response_text
            await self.assembler.after_sub_goal(context)
        return LoopResult.ADVANCE


class EndConditionStage(LoopStage):
    name = "end_condition"

    async def run(self, context: AgentContext) -> LoopResult:
        if context.current_sub_goal_index + 1 < len(context.sub_goals):
            return LoopResult.NEXT_SUB_GOAL
        return LoopResult.DONE


async def _publish_debug(
    context: AgentContext,
    event_type: str,
    *,
    stage: str | None = None,
    level: str = "info",
    summary: str = "",
    payload: dict[str, Any] | None = None,
) -> None:
    hub = context.debug_hub
    if _debug_enabled(context):
        await hub.publish(
            event_type,
            stage=stage,
            level=level,
            summary=summary,
            payload=payload,
        )


def _debug_enabled(context: AgentContext) -> bool:
    return context.debug_hub is not None and getattr(context.debug_hub, "enabled", False)


def _pipeline_payload(context: AgentContext, iterations: int) -> dict[str, Any]:
    current = context.current_sub_goal
    return {
        "iterations": iterations,
        "completed": context.completed,
        "state_name": context.state_name,
        "current_sub_goal_index": context.current_sub_goal_index,
        "current_sub_goal": (
            {
                "description": current.description,
                "status": current.status,
                "result": current.result,
            }
            if current is not None
            else None
        ),
        "sub_goal_count": len(context.sub_goals),
        "pending_tool_call_count": len(context.pending_tool_calls),
    }


def _duration_ms(started_at: float) -> float:
    return round((perf_counter() - started_at) * 1000, 3)


def _exception_payload(exc: Exception) -> dict[str, Any]:
    return {
        "type": type(exc).__name__,
        "message": str(exc),
        "traceback": "".join(traceback.format_exception(type(exc), exc, exc.__traceback__)),
    }


def _tool_result_content_blocks(context: AgentContext, result: Any) -> list[dict[str, Any]]:
    if isinstance(result, dict) and isinstance(result.get("screenshot"), dict):
        screenshot = result["screenshot"]
        block: dict[str, Any] = {
            "kind": "image",
            "mime_type": screenshot.get("mime_type", "image/png"),
            "path": screenshot.get("path"),
            "bytes": screenshot.get("bytes"),
        }
        path = screenshot.get("path")
        if path:
            try:
                png_bytes = Path(path).read_bytes()
                max_edge = getattr(context.debug_hub, "image_max_edge", 512)
                block["thumbnail_data_url"] = thumbnail_data_url(
                    png_bytes,
                    max_edge=max_edge,
                )
            except OSError as exc:
                block["thumbnail_error"] = str(exc)
        return [block]

    if isinstance(result, str):
        text = result
    else:
        text = json.dumps(result, ensure_ascii=False, default=str)
    return [{"kind": "text", "text": text}]
