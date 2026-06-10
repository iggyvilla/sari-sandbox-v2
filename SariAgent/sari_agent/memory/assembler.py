"""Memory assembly hooks for completed sub-goals."""

from __future__ import annotations

from sari_agent.context import AgentContext


class MemoryAssembler:
    """Placeholder for episodic and semantic memory assembly."""

    async def after_sub_goal(self, context: AgentContext) -> None:
        context.metadata.setdefault("memory_assembly_events", 0)
        context.metadata["memory_assembly_events"] += 1
