from __future__ import annotations

import pytest

from sari_agent.context import AgentContext
from sari_agent.debug import DebugHub
from sari_agent.loop import AgentLoop, ExecuteToolsStage, LoopResult, LoopStage
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


@pytest.mark.asyncio
async def test_execute_tools_stage_publishes_tool_events() -> None:
    async def Echo(value: str) -> dict[str, str]:
        return {"echo": value}

    registry = ToolRegistry()
    registry.register("Echo", Echo)
    hub = DebugHub(enabled=True, run_id="run", replay_limit=10)
    context = AgentContext(user_input="inspect", debug_hub=hub)
    context.pending_tool_calls = [
        {
            "id": "fc_1",
            "call_id": "call_1",
            "name": "Echo",
            "arguments": {"value": "hi"},
            "arguments_json": '{"value":"hi"}',
        }
    ]

    result = await ExecuteToolsStage(registry).run(context)
    events = [event.to_dict() for event in hub.replay_events()]

    assert result == LoopResult.REPEAT
    assert [event["type"] for event in events] == [
        "tool.call.started",
        "tool.call.completed",
    ]
    assert events[0]["payload"]["arguments"] == {"value": "hi"}
    assert events[1]["payload"]["content"] == [
        {"kind": "text", "text": '{"echo": "hi"}'}
    ]
