from __future__ import annotations

import pytest

from sari_agent.tools.factory import (
    OAIToolFactory,
    Property,
    ToolRegistrationError,
    ToolRegistry,
    UnknownToolError,
)


def test_property_schema_and_required_fields() -> None:
    factory = OAIToolFactory()

    @factory.tool(
        desc="Move something.",
        properties=[
            Property("name", "string", "The target name."),
            Property("speed", "number", "Movement speed.", required=False),
        ],
    )
    def move(name: str, speed: float | None = None) -> dict[str, object]:
        return {"name": name, "speed": speed}

    definitions, _ = factory.assemble()
    parameters = definitions[0]["parameters"]

    assert definitions[0]["name"] == "move"
    assert parameters["additionalProperties"] is False
    assert parameters["properties"]["name"] == {
        "type": "string",
        "description": "The target name.",
    }
    assert parameters["required"] == ["name"]
    assert "speed" in parameters["properties"]


def test_duplicate_missing_and_invalid_tool_registration_errors() -> None:
    with pytest.raises(ToolRegistrationError, match="Unsupported schema type"):
        Property("bad", "float", "Nope.")

    with pytest.raises(ToolRegistrationError, match="Description is required"):
        Property("bad", "string", "")

    factory = OAIToolFactory()

    @factory.tool(desc="First tool.")
    def same() -> None:
        return None

    with pytest.raises(ToolRegistrationError, match="Duplicate tool name"):

        @factory.tool(desc="Second tool.")
        def same() -> None:  # type: ignore[no-redef]
            return None

    with pytest.raises(ToolRegistrationError, match="Tool description is required"):
        factory.tool(desc="")


@pytest.mark.asyncio
async def test_registry_dispatches_sync_async_and_rejects_unknown() -> None:
    registry = ToolRegistry()

    def sync_tool(value: int) -> int:
        return value + 1

    async def async_tool(value: int) -> int:
        return value + 2

    registry.register("sync_tool", sync_tool)
    registry.register("async_tool", async_tool)

    assert await registry.dispatch("sync_tool", {"value": 3}) == 4
    assert await registry.dispatch("async_tool", {"value": 3}) == 5

    with pytest.raises(UnknownToolError):
        await registry.dispatch("missing", {})
