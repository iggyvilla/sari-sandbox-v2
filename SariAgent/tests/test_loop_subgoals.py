from __future__ import annotations

import base64
import json
from typing import Any

import pytest

from sari_agent.context import AgentContext, SubGoal
from sari_agent.debug import DebugHub
from sari_agent.loop import (
    AgentTurnStage,
    LoopResult,
    MemoryAssemblyStage,
    PlanSubGoalsStage,
)
from sari_agent.openai_client import ResponseStreamResult
from sari_agent.tools.factory import ToolRegistry


PNG_BYTES = b"\x89PNG\r\n\x1a\n" + b"\x00" * 8


class FakeClient:
    """Scripted ResponsesClient: each run_streamed call pops the next output.

    Outputs may be plain strings (text-only responses) or full
    ResponseStreamResult objects (e.g. with tool calls).
    """

    def __init__(
        self,
        *outputs: str | ResponseStreamResult,
        include_messages: bool = False,
        usage: dict[str, int] | None = None,
    ) -> None:
        self.outputs = list(outputs)
        self.include_messages = include_messages
        self.usage = usage
        self.calls: list[dict[str, Any]] = []

    async def run_streamed(
        self,
        *,
        input_items: list[dict[str, Any]],
        tools: list[dict[str, Any]] | None = None,
        instructions: str | None = None,
        stage: str | None = None,
    ) -> ResponseStreamResult:
        self.calls.append(
            {
                "input_items": input_items,
                "tools": tools,
                "instructions": instructions,
                "stage": stage,
            }
        )
        output = self.outputs.pop(0) if self.outputs else "ok"
        if isinstance(output, ResponseStreamResult):
            return output
        message_items = [{"role": "assistant", "content": output}] if self.include_messages else []
        return ResponseStreamResult(text=output, message_items=message_items, usage=self.usage)


def tool_call_result(
    name: str,
    arguments: dict[str, Any],
    *,
    call_id: str = "call_1",
    text: str = "",
    usage: dict[str, int] | None = None,
) -> ResponseStreamResult:
    arguments_json = json.dumps(arguments)
    return ResponseStreamResult(
        text=text,
        tool_calls=[
            {
                "id": call_id,
                "call_id": call_id,
                "name": name,
                "arguments": arguments,
                "arguments_json": arguments_json,
            }
        ],
        message_items=[
            {
                "type": "function_call",
                "id": call_id,
                "call_id": call_id,
                "name": name,
                "arguments": arguments_json,
            }
        ],
        usage=usage,
    )


def make_turn_stage(
    client: FakeClient,
    *,
    tools: list[dict[str, Any]] | None = None,
    registry: ToolRegistry | None = None,
    max_turns: int | None = None,
) -> AgentTurnStage:
    return AgentTurnStage(client, tools or [], registry or ToolRegistry(), max_turns=max_turns)


@pytest.mark.asyncio
async def test_plan_sub_goals_stage_uses_llm_json() -> None:
    client = FakeClient(
        '{"sub_goals":[{"description":"Locate the milk section"},'
        '{"description":"Pick up a bag of chips"}]}'
    )
    context = AgentContext(user_input="Get milk and chips")

    result = await PlanSubGoalsStage(client).run(context)

    assert result == LoopResult.ADVANCE
    assert [sub_goal.description for sub_goal in context.sub_goals] == [
        "Locate the milk section",
        "Pick up a bag of chips",
    ]
    assert client.calls[0]["tools"] == []
    assert client.calls[0]["stage"] == "plan_sub_goals"
    assert "Get milk and chips" in client.calls[0]["input_items"][0]["content"]
    assert "sub_goals" in client.calls[0]["instructions"]


@pytest.mark.parametrize(
    "planner_text",
    [
        "not json",
        '{"sub_goals":[]}',
        '{"sub_goals":[{"description":"   "},{}]}',
    ],
)
@pytest.mark.asyncio
async def test_plan_sub_goals_stage_falls_back_to_original_prompt(planner_text: str) -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=10)
    context = AgentContext(user_input="look around the store", debug_hub=hub)

    result = await PlanSubGoalsStage(FakeClient(planner_text)).run(context)

    assert result == LoopResult.ADVANCE
    assert [sub_goal.description for sub_goal in context.sub_goals] == [
        "look around the store"
    ]
    assert "pipeline.subgoals.plan_failed" in [
        event.to_dict()["type"] for event in hub.replay_events()
    ]


@pytest.mark.asyncio
async def test_plan_sub_goals_stage_sets_output_and_records_usage() -> None:
    usage = {"input_tokens": 512, "output_tokens": 88, "total_tokens": 600}
    client = FakeClient(
        '{"sub_goals":[{"description":"Locate the milk section"},'
        '{"description":"Pick up a bag of chips"}]}',
        usage=usage,
    )
    context = AgentContext(user_input="Get milk and chips")

    await PlanSubGoalsStage(client).run(context)

    assert context.stage_usage["plan_sub_goals"] == usage
    assert context.stage_output is not None
    assert context.stage_output.text == (
        "1. Locate the milk section\n2. Pick up a bag of chips"
    )
    assert context.stage_output.usage == usage


@pytest.mark.asyncio
async def test_plan_sub_goals_stage_without_client_keeps_multiline_fallback() -> None:
    context = AgentContext(user_input="look left\n\nlook right")

    await PlanSubGoalsStage().run(context)

    assert [sub_goal.description for sub_goal in context.sub_goals] == [
        "look left",
        "look right",
    ]


@pytest.mark.asyncio
async def test_agent_turn_appends_user_message_for_next_sub_goal() -> None:
    client = FakeClient("first done", "second done", include_messages=True)
    context = AgentContext(user_input="complete both tasks")
    context.sub_goals = [SubGoal("first task"), SubGoal("second task")]
    stage = make_turn_stage(client)

    await stage.run(context)
    context.current_sub_goal_index = 1
    await stage.run(context)

    assert client.calls[0]["input_items"] == [{"role": "user", "content": "first task"}]
    assert client.calls[1]["input_items"][-1] == {
        "role": "user",
        "content": "second task",
    }


@pytest.mark.asyncio
async def test_agent_turn_always_offers_control_tools() -> None:
    client = FakeClient("done")
    context = AgentContext(user_input="task")
    stage = make_turn_stage(client, tools=[{"type": "function", "name": "Echo"}])

    await stage.run(context)

    tool_names = [tool["name"] for tool in client.calls[0]["tools"]]
    assert tool_names == ["Echo", "complete_sub_goal", "revise_sub_goals"]


@pytest.mark.asyncio
async def test_agent_turn_loops_over_tool_calls_until_text_response() -> None:
    seen: list[str] = []

    async def Echo(value: str) -> dict[str, str]:
        seen.append(value)
        return {"echo": value}

    registry = ToolRegistry()
    registry.register("Echo", Echo)
    client = FakeClient(tool_call_result("Echo", {"value": "hi"}), "all done")
    context = AgentContext(user_input="task")
    context.sub_goals = [SubGoal("task")]
    stage = make_turn_stage(client, registry=registry)

    result = await stage.run(context)

    assert result == LoopResult.ADVANCE
    assert seen == ["hi"]
    assert len(client.calls) == 2
    outputs = [item for item in context.messages if item.get("type") == "function_call_output"]
    assert outputs == [
        {"type": "function_call_output", "call_id": "call_1", "output": '{"echo": "hi"}'}
    ]
    # The second model call sees the tool output.
    assert outputs[0] in client.calls[1]["input_items"]
    assert context.stage_output is not None
    assert context.stage_output.text.startswith("2 model turn(s), 1 tool call(s)")


@pytest.mark.asyncio
async def test_agent_turn_attaches_screenshot_image_to_next_model_turn(tmp_path) -> None:
    screenshot_path = tmp_path / "shot.png"
    screenshot_path.write_bytes(PNG_BYTES)

    async def RequestScreenshot() -> dict[str, Any]:
        return {
            "command": "RequestScreenshot",
            "screenshot": {
                "path": str(screenshot_path),
                "bytes": len(PNG_BYTES),
                "mime_type": "image/png",
            },
        }

    registry = ToolRegistry()
    registry.register("RequestScreenshot", RequestScreenshot)
    client = FakeClient(tool_call_result("RequestScreenshot", {}), "I can see it")
    context = AgentContext(user_input="describe view")
    context.sub_goals = [SubGoal("describe view")]

    await make_turn_stage(client, registry=registry, max_turns=2).run(context)

    expected_data_url = (
        "data:image/png;base64," + base64.b64encode(PNG_BYTES).decode("ascii")
    )
    assert len(client.calls) == 2
    second_turn_items = client.calls[1]["input_items"]
    assert second_turn_items[-2] == {
        "type": "function_call_output",
        "call_id": "call_1",
        "output": json.dumps(
            {
                "command": "RequestScreenshot",
                "screenshot": {
                    "path": str(screenshot_path),
                    "bytes": len(PNG_BYTES),
                    "mime_type": "image/png",
                },
            }
        ),
    }
    assert second_turn_items[-1] == {
        "role": "user",
        "content": [
            {
                "type": "input_text",
                "text": (
                    "Screenshot captured from the agent's egocentric Unity camera. "
                    "Use this image for visual observations."
                ),
            },
            {"type": "input_image", "image_url": expected_data_url},
        ],
    }


@pytest.mark.asyncio
async def test_agent_turn_soft_completes_on_plain_text() -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=20)
    client = FakeClient("I think I am done here")
    context = AgentContext(user_input="task", debug_hub=hub)
    context.sub_goals = [SubGoal("task")]

    await make_turn_stage(client).run(context)

    assert context.sub_goals[0].status == "completed"
    assert context.sub_goals[0].result == "I think I am done here"
    assert "pipeline.subgoal.soft_completed" in [
        event.to_dict()["type"] for event in hub.replay_events()
    ]


@pytest.mark.asyncio
async def test_agent_turn_complete_sub_goal_marks_status_and_result() -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=20)
    client = FakeClient(
        tool_call_result("complete_sub_goal", {"result": "Found the milk aisle."})
    )
    context = AgentContext(user_input="find milk", debug_hub=hub)
    context.sub_goals = [SubGoal("find milk"), SubGoal("grab chips")]

    result = await make_turn_stage(client).run(context)

    assert result == LoopResult.ADVANCE
    assert len(client.calls) == 1  # completion ends the turn loop
    assert context.sub_goals[0].status == "completed"
    assert context.sub_goals[0].result == "Found the milk aisle."
    outputs = [item for item in context.messages if item.get("type") == "function_call_output"]
    assert json.loads(outputs[0]["output"]) == {"ok": True, "status": "completed"}
    assert "pipeline.subgoal.completed" in [
        event.to_dict()["type"] for event in hub.replay_events()
    ]


@pytest.mark.asyncio
async def test_agent_turn_complete_sub_goal_supports_failed_status() -> None:
    client = FakeClient(
        tool_call_result(
            "complete_sub_goal",
            {"result": "The shelf is empty.", "status": "failed"},
        )
    )
    context = AgentContext(user_input="find milk")
    context.sub_goals = [SubGoal("find milk")]

    await make_turn_stage(client).run(context)

    assert context.sub_goals[0].status == "failed"
    assert context.sub_goals[0].result == "The shelf is empty."


@pytest.mark.asyncio
async def test_agent_turn_revise_sub_goals_replaces_pending_plan() -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=20)
    client = FakeClient(
        tool_call_result(
            "revise_sub_goals",
            {"sub_goals": ["Check the chiller aisle"], "reason": "No milk on shelves"},
        ),
        tool_call_result("complete_sub_goal", {"result": "done"}, call_id="call_2"),
    )
    context = AgentContext(user_input="find milk", debug_hub=hub)
    context.sub_goals = [SubGoal("find milk"), SubGoal("old goal 2"), SubGoal("old goal 3")]

    await make_turn_stage(client).run(context)

    assert [sub_goal.description for sub_goal in context.sub_goals] == [
        "find milk",
        "Check the chiller aisle",
    ]
    assert context.sub_goals[0].status == "completed"
    assert context.sub_goals[1].status == "pending"
    revised = [
        event.to_dict()
        for event in hub.replay_events()
        if event.to_dict()["type"] == "pipeline.subgoals.revised"
    ]
    assert len(revised) == 1
    assert revised[0]["payload"]["reason"] == "No milk on shelves"


@pytest.mark.asyncio
async def test_agent_turn_revise_sub_goals_rejects_bad_arguments() -> None:
    client = FakeClient(
        tool_call_result("revise_sub_goals", {"sub_goals": "not a list"}),
        "giving up on revising",
    )
    context = AgentContext(user_input="task")
    context.sub_goals = [SubGoal("task"), SubGoal("later")]

    await make_turn_stage(client).run(context)

    assert [sub_goal.description for sub_goal in context.sub_goals] == ["task", "later"]
    outputs = [item for item in context.messages if item.get("type") == "function_call_output"]
    assert json.loads(outputs[0]["output"])["ok"] is False


@pytest.mark.asyncio
async def test_agent_turn_marks_sub_goal_failed_at_turn_limit() -> None:
    async def Echo(value: str) -> dict[str, str]:
        return {"echo": value}

    registry = ToolRegistry()
    registry.register("Echo", Echo)
    hub = DebugHub(enabled=True, run_id="run", replay_limit=20)
    client = FakeClient(
        tool_call_result("Echo", {"value": "1"}, call_id="call_1"),
        tool_call_result("Echo", {"value": "2"}, call_id="call_2"),
    )
    context = AgentContext(user_input="task", debug_hub=hub)
    context.sub_goals = [SubGoal("task")]
    stage = make_turn_stage(client, registry=registry, max_turns=2)

    result = await stage.run(context)

    assert result == LoopResult.ADVANCE
    assert len(client.calls) == 2
    assert context.sub_goals[0].status == "failed"
    assert "pipeline.subgoal.turn_limit" in [
        event.to_dict()["type"] for event in hub.replay_events()
    ]


@pytest.mark.asyncio
async def test_agent_turn_accumulates_usage_across_turns() -> None:
    usage = {"input_tokens": 100, "output_tokens": 20, "total_tokens": 120}
    client = FakeClient(
        tool_call_result("complete_sub_goal", {"result": "one"}, usage=usage),
        usage=usage,
    )
    context = AgentContext(user_input="task")
    context.sub_goals = [SubGoal("task")]
    stage = make_turn_stage(client)

    await stage.run(context)
    assert context.stage_output is not None
    assert context.stage_output.text == (
        "1 model turn(s), 1 tool call(s) • 120 tokens (100 in / 20 out)"
    )
    assert context.stage_output.usage == usage

    context.current_sub_goal_index = 0
    context.sub_goals = [SubGoal("again")]
    context.metadata.pop("active_sub_goal_index")
    await stage.run(context)
    assert context.stage_usage["agent_turn"] == {
        "input_tokens": 200,
        "output_tokens": 40,
        "total_tokens": 240,
    }


@pytest.mark.asyncio
async def test_agent_turn_without_usage_reports_unavailable() -> None:
    context = AgentContext(user_input="task")
    context.sub_goals = [SubGoal("task")]

    await make_turn_stage(FakeClient("ok")).run(context)

    assert context.stage_output is not None
    assert context.stage_output.text == "1 model turn(s), 0 tool call(s) • token usage unavailable"
    assert context.stage_output.usage is None
    assert "agent_turn" not in context.stage_usage


@pytest.mark.asyncio
async def test_memory_assembly_stage_assembles_terminal_sub_goals_only() -> None:
    context = AgentContext(user_input="task")
    context.sub_goals = [SubGoal("task")]
    stage = MemoryAssemblyStage()

    await stage.run(context)
    assert context.metadata.get("memory_assembly_events") is None
    assert context.sub_goals[0].status == "pending"

    context.sub_goals[0].status = "completed"
    context.sub_goals[0].result = "model-authored result"
    await stage.run(context)
    assert context.metadata["memory_assembly_events"] == 1
    assert context.sub_goals[0].result == "model-authored result"
