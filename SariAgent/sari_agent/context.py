"""Shared state carried through the agent loop."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from sari_agent.config import AgentConfig


@dataclass(slots=True)
class SubGoal:
    description: str
    status: str = "pending"
    result: str | None = None


@dataclass(slots=True)
class StageOutput:
    """Definitive per-stage output surfaced on pipeline.stage.completed."""

    kind: str = "text"  # "text" | "code"
    text: str = ""
    language: str | None = None
    usage: dict[str, Any] | None = None

    def to_payload(self) -> dict[str, Any]:
        return {
            "kind": self.kind,
            "text": self.text,
            "language": self.language,
            "usage": self.usage,
        }


@dataclass(slots=True)
class AgentStateConfig:
    name: str
    system_prompt_fragments: list[str] = field(default_factory=list)
    allowed_tool_names: set[str] | None = None
    context_window_policy: str = "reuse"
    loop_stage_names: list[str] | None = None


@dataclass(slots=True)
class AgentContext:
    user_input: str
    config: AgentConfig = field(default_factory=AgentConfig)
    state_name: str = "default"
    state_configs: dict[str, AgentStateConfig] = field(default_factory=dict)
    memory_documents: dict[str, str] = field(default_factory=dict)
    system_prompt: str = ""
    sub_goals: list[SubGoal] = field(default_factory=list)
    current_sub_goal_index: int = 0
    messages: list[dict[str, Any]] = field(default_factory=list)
    response_text: str = ""
    pending_tool_calls: list[dict[str, Any]] = field(default_factory=list)
    tool_results: list[dict[str, Any]] = field(default_factory=list)
    completed: bool = False
    metadata: dict[str, Any] = field(default_factory=dict)
    # Optional DebugHub used by the embedded websocket backend.  It is typed as
    # Any to keep the context module free of runtime dependencies on debug code.
    debug_hub: Any | None = None
    # Definitive output set by the current stage; AgentLoop pops it into the
    # pipeline.stage.completed event after each stage run.
    stage_output: StageOutput | None = None
    # Token usage accumulated per stage name across all passes.
    stage_usage: dict[str, dict[str, int]] = field(default_factory=dict)
    # perf_counter() value captured when AgentLoop.run starts.
    run_started_at: float | None = None

    @property
    def current_state(self) -> AgentStateConfig:
        return self.state_configs.get(self.state_name, AgentStateConfig(name=self.state_name))

    @property
    def current_sub_goal(self) -> SubGoal | None:
        if not self.sub_goals:
            return None
        if self.current_sub_goal_index >= len(self.sub_goals):
            return None
        return self.sub_goals[self.current_sub_goal_index]

    def allowed_tool_names(self) -> set[str] | None:
        return self.current_state.allowed_tool_names

    def record_usage(self, stage: str, usage: dict[str, Any] | None) -> None:
        if not usage:
            return
        totals = self.stage_usage.setdefault(
            stage, {"input_tokens": 0, "output_tokens": 0, "total_tokens": 0}
        )
        for key in totals:
            value = usage.get(key)
            if isinstance(value, (int, float)) and not isinstance(value, bool):
                totals[key] += int(value)

    def total_usage(self) -> dict[str, int]:
        totals = {"input_tokens": 0, "output_tokens": 0, "total_tokens": 0}
        for usage in self.stage_usage.values():
            for key in totals:
                totals[key] += usage.get(key, 0)
        return totals
