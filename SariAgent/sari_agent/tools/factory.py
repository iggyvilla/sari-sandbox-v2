"""OpenAI tool definition assembly and local dispatch."""

from __future__ import annotations

import inspect
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable


JSON_SCHEMA_TYPES = {"string", "number", "integer", "boolean", "object", "array", "null"}


class ToolRegistrationError(ValueError):
    pass


class UnknownToolError(KeyError):
    pass


@dataclass(frozen=True, slots=True)
class Property:
    name: str
    schema_type: str
    description: str
    required: bool = True
    enum: list[Any] | None = None
    items: dict[str, Any] | None = None
    properties: dict[str, Any] | None = None
    additional: dict[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if not self.name:
            raise ToolRegistrationError("Property name is required")
        if self.schema_type not in JSON_SCHEMA_TYPES:
            raise ToolRegistrationError(
                f"Unsupported schema type {self.schema_type!r} for property {self.name!r}"
            )
        if not self.description:
            raise ToolRegistrationError(f"Description is required for property {self.name!r}")

    def to_schema(self) -> dict[str, Any]:
        schema: dict[str, Any] = {
            "type": self.schema_type,
            "description": self.description,
        }
        if self.enum is not None:
            schema["enum"] = self.enum
        if self.items is not None:
            schema["items"] = self.items
        if self.properties is not None:
            schema["properties"] = self.properties
        schema.update(self.additional)
        return schema


ToolCallable = Callable[..., Any] | Callable[..., Awaitable[Any]]


class ToolRegistry:
    def __init__(self) -> None:
        self._tools: dict[str, ToolCallable] = {}

    @property
    def names(self) -> set[str]:
        return set(self._tools)

    def register(self, name: str, func: ToolCallable) -> None:
        if name in self._tools:
            raise ToolRegistrationError(f"Duplicate tool name: {name}")
        self._tools[name] = func

    def merge(self, other: "ToolRegistry") -> None:
        for name, func in other._tools.items():
            self.register(name, func)

    def constrained(self, allowed_names: set[str] | None) -> "ToolRegistry":
        if allowed_names is None:
            return self
        registry = ToolRegistry()
        for name in allowed_names:
            if name not in self._tools:
                continue
            registry.register(name, self._tools[name])
        return registry

    async def dispatch(self, name: str, arguments: dict[str, Any] | None = None) -> Any:
        if name not in self._tools:
            raise UnknownToolError(f"Unknown tool call: {name}")
        result = self._tools[name](**(arguments or {}))
        if inspect.isawaitable(result):
            return await result
        return result


class OAIToolFactory:
    def __init__(self) -> None:
        self._definitions: list[dict[str, Any]] = []
        self._registry = ToolRegistry()

    def tool(self, *, desc: str, properties: list[Property] | None = None) -> Callable[[ToolCallable], ToolCallable]:
        if not desc:
            raise ToolRegistrationError("Tool description is required")

        def decorator(func: ToolCallable) -> ToolCallable:
            name = getattr(func, "__name__", "")
            if not name:
                raise ToolRegistrationError("Tool function must have a name")
            if name in self._registry.names:
                raise ToolRegistrationError(f"Duplicate tool name: {name}")

            property_list = properties or []
            schema_properties = {prop.name: prop.to_schema() for prop in property_list}
            required = [prop.name for prop in property_list if prop.required]
            parameters: dict[str, Any] = {
                "type": "object",
                "properties": schema_properties,
                "additionalProperties": False,
            }
            if required:
                parameters["required"] = required

            self._definitions.append(
                {
                    "type": "function",
                    "name": name,
                    "description": desc,
                    "parameters": parameters,
                }
            )
            self._registry.register(name, func)
            return func

        return decorator

    def assemble(self) -> tuple[list[dict[str, Any]], ToolRegistry]:
        return list(self._definitions), self._registry
