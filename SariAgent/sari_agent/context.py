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
