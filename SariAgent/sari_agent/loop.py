"""Async agent loop primitives and default stages."""

from __future__ import annotations

import json
from abc import ABC, abstractmethod
from enum import Enum
from typing import Any

from sari_agent.context import AgentContext, SubGoal
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

        while not context.completed and iterations < max_iterations:
            stage = self.stages[stage_index]
            result = await stage.run(context)
            iterations += 1
            context.metadata["loop_iterations"] = iterations

            if result == LoopResult.DONE:
                context.completed = True
                break
            if result == LoopResult.REPEAT:
                stage_index = 0
                continue
            if result == LoopResult.NEXT_SUB_GOAL:
                context.current_sub_goal_index += 1
                stage_index = 0
                continue
            stage_index = (stage_index + 1) % len(self.stages)

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
        return LoopResult.ADVANCE


class RunModelStage(LoopStage):
    name = "run_model"

    def __init__(self, client: ResponsesClient, tools: list[dict[str, Any]]) -> None:
        self.client = client
        self.tools = tools

    async def run(self, context: AgentContext) -> LoopResult:
        goal = context.current_sub_goal.description if context.current_sub_goal else context.user_input
        input_items = list(context.messages)
        if not input_items:
            input_items.append({"role": "user", "content": goal})

        result = await self.client.run_streamed(
            input_items=input_items,
            tools=self._allowed_tools(context),
            instructions=context.system_prompt or None,
        )
        context.response_text = result.text
        context.pending_tool_calls = result.tool_calls
        if result.text:
            context.messages.append({"role": "assistant", "content": result.text})
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
            result = await registry.dispatch(call["name"], call.get("arguments"))
            item = {
                "type": "function_call_output",
                "call_id": call.get("call_id") or call.get("id"),
                "output": json.dumps(result),
            }
            context.tool_results.append(item)
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
