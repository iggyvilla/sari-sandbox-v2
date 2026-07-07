from __future__ import annotations

import json
from typing import Any

import pytest

from sari_agent.context import AgentContext, StageOutput, SubGoal
from sari_agent.debug import DebugHub
from sari_agent.loop import (
    AgentLoop,
    AgentTurnStage,
    EndConditionStage,
    LoopResult,
    LoopStage,
)
from sari_agent.openai_client import ResponseStreamResult
from sari_agent.tools.factory import ToolRegistry


class StaticStage(LoopStage):
    def __init__(self, name: str, result: LoopResult) -> None:
        self.name = name
        self.result = result

    async def run(self, context: AgentContext) -> LoopResult:
        context.metadata[self.name] = True
        return self.result


class FailingStage(LoopStage):
    name = "explode"

    async def run(self, context: AgentContext) -> LoopResult:
        raise RuntimeError("boom")


@pytest.mark.asyncio
async def test_agent_loop_publishes_stage_and_run_events() -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=20)
    context = AgentContext(user_input="inspect", debug_hub=hub)
    loop = AgentLoop(
        [
            StaticStage("first", LoopResult.ADVANCE),
            StaticStage("finish", LoopResult.DONE),
        ]
    )

    await loop.run(context)

    events = [event.to_dict() for event in hub.replay_events()]
    assert [event["type"] for event in events] == [
        "run.started",
        "pipeline.stage.started",
        "pipeline.stage.completed",
        "pipeline.stage.started",
        "pipeline.stage.completed",
        "run.completed",
    ]
    assert events[1]["stage"] == "first"
    assert events[-1]["payload"]["completed"] is True


class OutputStage(LoopStage):
    name = "with_output"

    async def run(self, context: AgentContext) -> LoopResult:
        context.record_usage(self.name, {"input_tokens": 10, "output_tokens": 5, "total_tokens": 15})
        context.stage_output = StageOutput(text="definitive output")
        return LoopResult.DONE


@pytest.mark.asyncio
async def test_agent_loop_embeds_stage_output_and_run_totals() -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=20)
    context = AgentContext(user_input="inspect", debug_hub=hub)
    loop = AgentLoop([StaticStage("plain", LoopResult.ADVANCE), OutputStage()])

    await loop.run(context)

    events = [event.to_dict() for event in hub.replay_events()]
    completed = [event for event in events if event["type"] == "pipeline.stage.completed"]
    assert "output" not in completed[0]["payload"]
    assert completed[1]["payload"]["output"] == {
        "kind": "text",
        "text": "definitive output",
        "language": None,
        "usage": None,
    }
    assert context.stage_output is None

    run_completed = events[-1]
    assert run_completed["type"] == "run.completed"
    assert run_completed["payload"]["usage"] == {
        "per_stage": {"with_output": {"input_tokens": 10, "output_tokens": 5, "total_tokens": 15}},
        "totals": {"input_tokens": 10, "output_tokens": 5, "total_tokens": 15},
    }
    assert run_completed["payload"]["total_runtime_ms"] > 0


@pytest.mark.asyncio
async def test_end_condition_done_emits_run_summary() -> None:
    context = AgentContext(user_input="inspect")
    context.sub_goals = [SubGoal("only goal")]
    context.run_started_at = 0.0
    context.record_usage("plan_sub_goals", {"input_tokens": 100, "output_tokens": 30, "total_tokens": 130})
    context.record_usage("agent_turn", {"input_tokens": 900, "output_tokens": 200, "total_tokens": 1100})

    result = await EndConditionStage().run(context)

    assert result == LoopResult.DONE
    output = context.stage_output
    assert output is not None
    assert output.kind == "code"
    assert "plan_sub_goals" in output.text
    assert "agent_turn" in output.text
    assert "TOTAL" in output.text
    assert "1,230" in output.text
    assert "Runtime:" in output.text
    assert output.usage == {"input_tokens": 1000, "output_tokens": 230, "total_tokens": 1230}


@pytest.mark.asyncio
async def test_end_condition_next_sub_goal_emits_advance_note() -> None:
    context = AgentContext(user_input="inspect")
    context.sub_goals = [SubGoal("first"), SubGoal("second")]

    result = await EndConditionStage().run(context)

    assert result == LoopResult.NEXT_SUB_GOAL
    assert context.stage_output is not None
    assert context.stage_output.text == "Advancing to sub-goal 2/2"


@pytest.mark.asyncio
async def test_agent_loop_publishes_errors() -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=20)
    context = AgentContext(user_input="inspect", debug_hub=hub)
    loop = AgentLoop([FailingStage()])

    with pytest.raises(RuntimeError, match="boom"):
        await loop.run(context)

    events = [event.to_dict() for event in hub.replay_events()]
    assert "pipeline.stage.error" in [event["type"] for event in events]
    assert events[-1]["type"] == "run.error"
    assert events[-1]["payload"]["error"]["type"] == "RuntimeError"


class ScriptedClient:
    """Minimal ResponsesClient stub returning canned stream results in order."""

    def __init__(self, *results: ResponseStreamResult) -> None:
        self.results = list(results)

    async def run_streamed(self, **_: Any) -> ResponseStreamResult:
        return self.results.pop(0)


@pytest.mark.asyncio
async def test_agent_turn_stage_publishes_tool_events() -> None:
    async def Echo(value: str) -> dict[str, str]:
        return {"echo": value}

    registry = ToolRegistry()
    registry.register("Echo", Echo)
    hub = DebugHub(enabled=True, run_id="run", replay_limit=10)
    context = AgentContext(user_input="inspect", debug_hub=hub)
    context.sub_goals = [SubGoal("inspect")]
    arguments_json = json.dumps({"value": "hi"})
    client = ScriptedClient(
        ResponseStreamResult(
            tool_calls=[
                {
                    "id": "fc_1",
                    "call_id": "call_1",
                    "name": "Echo",
                    "arguments": {"value": "hi"},
                    "arguments_json": arguments_json,
                }
            ],
            message_items=[
                {
                    "type": "function_call",
                    "id": "fc_1",
                    "call_id": "call_1",
                    "name": "Echo",
                    "arguments": arguments_json,
                }
            ],
        ),
        ResponseStreamResult(
            text="done",
            tool_calls=[
                {
                    "id": "fc_2",
                    "call_id": "call_2",
                    "name": "complete_sub_goal",
                    "arguments": {"result": "done"},
                    "arguments_json": json.dumps({"result": "done"}),
                }
            ],
            message_items=[
                {
                    "type": "function_call",
                    "id": "fc_2",
                    "call_id": "call_2",
                    "name": "complete_sub_goal",
                    "arguments": json.dumps({"result": "done"}),
                }
            ],
        ),
    )

    result = await AgentTurnStage(client, [], registry).run(context)
    events = [event.to_dict() for event in hub.replay_events()]

    assert result == LoopResult.ADVANCE
    assert [event["type"] for event in events] == [
        "tool.call.started",
        "tool.call.completed",
        "tool.call.started",
        "pipeline.subgoal.completed",
        "tool.call.completed",
    ]
    assert events[0]["payload"]["arguments"] == {"value": "hi"}
    assert events[1]["payload"]["content"] == [
        {"kind": "text", "text": '{"echo": "hi"}'}
    ]
    assert events[3]["payload"]["status"] == "completed"
    assert context.sub_goals[0].status == "completed"
    assert context.sub_goals[0].result == "done"
