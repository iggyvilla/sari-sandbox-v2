"""JSON-only Unity /commands tool set."""

from __future__ import annotations

from pathlib import Path

from sari_agent.tools.factory import OAIToolFactory, Property, ToolRegistry
from sari_agent.unity.ws_client import UnityCommandClient


VECTOR_ITEMS = {"type": "number"}
VECTOR_ADDITIONAL = {"minItems": 3, "maxItems": 3}


def _vector_property(name: str, description: str) -> Property:
    return Property(
        name=name,
        schema_type="array",
        description=description,
        items=VECTOR_ITEMS,
        additional=VECTOR_ADDITIONAL,
    )


def assemble(
    client: UnityCommandClient | None = None,
    screenshot_dir: Path | None = None,
    **_: object,
) -> tuple[list[dict[str, object]], ToolRegistry]:
    if client is None:
        client = UnityCommandClient(screenshot_dir=screenshot_dir or Path("screenshots"))
    elif screenshot_dir is not None:
        client.screenshot_dir = screenshot_dir

    factory = OAIToolFactory()

    @factory.tool(
        desc="Move the agent by a relative translation and rotation.",
        properties=[
            _vector_property(
                "translation",
                "Egocentric x/y/z translation in meters relative to current facing: "
                "+z forward, -z backward, +x strafe right, -x strafe left, +y up.",
            ),
            _vector_property(
                "rotation",
                "Relative x/y/z Euler rotation in degrees: -y turns left, +y turns "
                "right, -x pitches view up, +x pitches view down.",
            ),
        ],
    )
    async def TranslateAgent(translation: list[float], rotation: list[float]) -> dict[str, object]:
        return await client.command("TranslateAgent", translation=translation, rotation=rotation)

    @factory.tool(
        desc="Transform the agent using JSON-mode Unity movement semantics.",
        properties=[
            _vector_property("translation", "JSON-mode relative x/y/z translation in Unity units."),
            _vector_property("rotation", "JSON-mode relative x/y/z Euler rotation in degrees."),
        ],
    )
    async def TransformAgent(translation: list[float], rotation: list[float]) -> dict[str, object]:
        return await client.command("TransformAgent", translation=translation, rotation=rotation)

    @factory.tool(
        desc="Move the active hand by a relative translation and rotation.",
        properties=[
            _vector_property("translation", "Relative x/y/z hand translation in Unity units."),
            _vector_property("rotation", "Relative x/y/z hand Euler rotation in degrees."),
        ],
    )
    async def TranslateHand(translation: list[float], rotation: list[float]) -> dict[str, object]:
        return await client.command("TranslateHand", translation=translation, rotation=rotation)

    @factory.tool(
        desc="Transform the active hand using JSON-mode Unity movement semantics.",
        properties=[
            _vector_property("translation", "JSON-mode relative x/y/z hand translation in Unity units."),
            _vector_property("rotation", "JSON-mode relative x/y/z hand Euler rotation in degrees."),
        ],
    )
    async def TransformHand(translation: list[float], rotation: list[float]) -> dict[str, object]:
        return await client.command("TransformHand", translation=translation, rotation=rotation)

    @factory.tool(desc="Reset the active hand position.", properties=[])
    async def ResetHandPosition() -> dict[str, object]:
        return await client.command("ResetHandPosition")

    @factory.tool(desc="Return whether the active hand is holding an item.", properties=[])
    async def IsHoldingItem() -> dict[str, object]:
        return await client.command("IsHoldingItem")

    @factory.tool(desc="Toggle the right-hand grip state.", properties=[])
    async def ToggleRightGrip() -> dict[str, object]:
        return await client.command("ToggleRightGrip")

    @factory.tool(desc="Toggle the active grip state.", properties=[])
    async def ToggleGrip() -> dict[str, object]:
        return await client.command("ToggleGrip")

    @factory.tool(desc="Toggle the right-hand poke state.", properties=[])
    async def ToggleRightPoke() -> dict[str, object]:
        return await client.command("ToggleRightPoke")

    @factory.tool(desc="Toggle the active poke state.", properties=[])
    async def TogglePoke() -> dict[str, object]:
        return await client.command("TogglePoke")

    @factory.tool(desc="Toggle the pointing gesture.", properties=[])
    async def TogglePoint() -> dict[str, object]:
        return await client.command("TogglePoint")

    @factory.tool(desc="Request an egocentric screenshot and save it to disk.", properties=[])
    async def RequestScreenshot() -> dict[str, object]:
        return await client.command("RequestScreenshot")

    @factory.tool(desc="Reset the Unity environment.", properties=[])
    async def ResetEnvironment() -> dict[str, object]:
        return await client.command("ResetEnvironment")

    return factory.assemble()
