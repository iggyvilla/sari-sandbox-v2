"""Import tool modules and merge their assemble() outputs."""

from __future__ import annotations

import importlib
from types import ModuleType
from typing import Any

from sari_agent.tools.factory import ToolRegistrationError, ToolRegistry


def _load_module(module: str | ModuleType) -> ModuleType:
    if isinstance(module, ModuleType):
        return module
    return importlib.import_module(module)


def load_tool_modules(modules: list[str | ModuleType], **assemble_kwargs: Any) -> tuple[list[dict[str, Any]], ToolRegistry]:
    definitions: list[dict[str, Any]] = []
    registry = ToolRegistry()
    seen_definition_names: set[str] = set()

    for module_ref in modules:
        module = _load_module(module_ref)
        assemble = getattr(module, "assemble", None)
        if assemble is None:
            raise ToolRegistrationError(f"Tool module {module.__name__} has no assemble()")
        module_definitions, module_registry = assemble(**assemble_kwargs)
        for definition in module_definitions:
            name = definition.get("name")
            if name in seen_definition_names:
                raise ToolRegistrationError(f"Duplicate tool definition: {name}")
            seen_definition_names.add(name)
            definitions.append(definition)
        registry.merge(module_registry)

    return definitions, registry
