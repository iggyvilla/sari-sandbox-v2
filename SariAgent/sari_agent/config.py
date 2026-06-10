"""Configuration defaults for the SariAgent harness."""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path


PACKAGE_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_MEMORY_DIR = PACKAGE_ROOT / "memory"
DEFAULT_SCREENSHOT_DIR = PACKAGE_ROOT / "screenshots"


def _env_int(name: str, default: int) -> int:
    value = os.environ.get(name)
    if value is None:
        return default
    try:
        return int(value)
    except ValueError as exc:
        raise ValueError(f"{name} must be an integer, got {value!r}") from exc


@dataclass(slots=True)
class AgentConfig:
    """Runtime configuration loaded from environment-friendly defaults."""

    model: str = field(default_factory=lambda: os.environ.get("SARI_MODEL", "gpt-4.1"))
    unity_host: str = field(default_factory=lambda: os.environ.get("SARI_UNITY_HOST", "localhost"))
    unity_port: int = field(default_factory=lambda: _env_int("SARI_UNITY_PORT", 8080))
    memory_dir: Path = field(
        default_factory=lambda: Path(os.environ.get("SARI_MEMORY_DIR", DEFAULT_MEMORY_DIR))
    )
    screenshot_dir: Path = field(
        default_factory=lambda: Path(os.environ.get("SARI_SCREENSHOT_DIR", DEFAULT_SCREENSHOT_DIR))
    )
    max_loop_iterations: int = field(default_factory=lambda: _env_int("SARI_MAX_LOOP_ITERATIONS", 8))
    request_timeout_seconds: float = 30.0

    @property
    def commands_url(self) -> str:
        return f"ws://{self.unity_host}:{self.unity_port}/commands"


def load_config() -> AgentConfig:
    return AgentConfig()
