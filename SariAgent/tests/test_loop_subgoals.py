from __future__ import annotations

from typing import Any

import pytest

from sari_agent.context import AgentContext, SubGoal
from sari_agent.debug import DebugHub
from sari_agent.loop import LoopResult, PlanSubGoalsStage, RunModelStage
from sari_agent.openai_client import ResponseStreamResult


class FakeClient:
    def __init__(
        self,
        *texts: str,
        include_messages: bool = False,
        usage: dict[str, int] | None = None,
    ) -> None:
        self.texts = list(texts)
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
        text = self.texts.pop(0) if self.texts else "ok"
        message_items = [{"role": "assistant", "content": text}] if self.include_messages else []
        return ResponseStreamResult(text=text, message_items=message_items, usage=self.usage)


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
async def test_run_model_stage_appends_user_message_for_next_sub_goal() -> None:
    client = FakeClient("first done", "second done", include_messages=True)
    context = AgentContext(user_input="complete both tasks")
    context.sub_goals = [SubGoal("first task"), SubGoal("second task")]
    stage = RunModelStage(client, tools=[])

    await stage.run(context)
    context.current_sub_goal_index = 1
    await stage.run(context)

    assert client.calls[0]["input_items"] == [{"role": "user", "content": "first task"}]
    assert client.calls[1]["input_items"][-1] == {
        "role": "user",
        "content": "second task",
    }


@pytest.mark.asyncio
async def test_run_model_stage_accumulates_usage_across_passes() -> None:
    usage = {"input_tokens": 100, "output_tokens": 20, "total_tokens": 120}
    client = FakeClient("one", "two", usage=usage)
    context = AgentContext(user_input="task")
    stage = RunModelStage(client, tools=[])

    await stage.run(context)
    assert context.stage_output is not None
    assert context.stage_output.text == "Model call: 120 tokens (100 in / 20 out)"
    assert context.stage_output.usage == usage

    await stage.run(context)
    assert context.stage_usage["run_model"] == {
        "input_tokens": 200,
        "output_tokens": 40,
        "total_tokens": 240,
    }


@pytest.mark.asyncio
async def test_run_model_stage_without_usage_reports_unavailable() -> None:
    context = AgentContext(user_input="task")
    stage = RunModelStage(FakeClient("ok"), tools=[])

    await stage.run(context)

    assert context.stage_output is not None
    assert context.stage_output.text == "Model call: token usage unavailable"
    assert context.stage_output.usage is None
    assert "run_model" not in context.stage_usage
