"""Configuration defaults for the SariAgent harness."""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

PACKAGE_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_ENV_FILE = PACKAGE_ROOT / ".env"
DEFAULT_MEMORY_DIR = PACKAGE_ROOT / "memory"
DEFAULT_SCREENSHOT_DIR = PACKAGE_ROOT / "screenshots"
DEFAULT_DEBUG_RUNS_DIR = PACKAGE_ROOT / "debug_runs"


def load_dotenv(path: Path = DEFAULT_ENV_FILE, *, override: bool = False) -> None:
    """Load simple KEY=VALUE pairs from a .env file into os.environ."""

    if not path.exists():
        return

    for line_number, raw_line in enumerate(
        path.read_text(encoding="utf-8").splitlines(), start=1
    ):
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line.removeprefix("export ").strip()
        if "=" not in line:
            raise ValueError(f"{path}:{line_number} must be KEY=VALUE")

        key, value = line.split("=", 1)
        key = key.strip()
        if not key:
            raise ValueError(f"{path}:{line_number} has an empty key")
        if not override and key in os.environ:
            continue

        os.environ[key] = _strip_env_value(value)


def _strip_env_value(value: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in {"'", '"'}:
        return value[1:-1]
    return value


def _env_first(*names: str) -> str | None:
    for name in names:
        value = os.environ.get(name)
        if value and value.strip():
            return value.strip()
    return None


def _env_path(name: str, default: Path) -> Path:
    value = os.environ.get(name)
    if not value:
        return default
    path = Path(value)
    if not path.is_absolute():
        return PACKAGE_ROOT / path
    return path


def _env_int(name: str, default: int) -> int:
    value = os.environ.get(name)
    if value is None:
        return default
    try:
        return int(value)
    except ValueError as exc:
        raise ValueError(f"{name} must be an integer, got {value!r}") from exc


def _env_bool(name: str, default: bool = False) -> bool:
    value = os.environ.get(name)
    if value is None:
        return default
    normalized = value.strip().lower()
    if normalized in {"1", "true", "yes", "y", "on"}:
        return True
    if normalized in {"0", "false", "no", "n", "off"}:
        return False
    raise ValueError(f"{name} must be a boolean value, got {value!r}")


def _env_optional_positive_int(name: str, default: int) -> int | None:
    value = _env_int(name, default)
    if value <= 0:
        return None
    return value


def _default_api_style() -> str:
    configured = _env_first("SARI_OPENAI_API_STYLE", "SARI_OPENAI_API_MODE")
    if configured:
        return configured
    if _env_first("SARI_OPENAI_BASE_URL", "OPENAI_BASE_URL"):
        return "chat_completions"
    return "responses"


@dataclass(slots=True)
class AgentConfig:
    """Runtime configuration loaded from environment-friendly defaults."""

    model: str = field(default_factory=lambda: os.environ.get("SARI_MODEL", "gpt-4.1"))
    openai_api_key: str | None = field(
        default_factory=lambda: _env_first("SARI_OPENAI_API_KEY", "OPENAI_API_KEY")
    )
    openai_base_url: str | None = field(
        default_factory=lambda: _env_first("SARI_OPENAI_BASE_URL", "OPENAI_BASE_URL")
    )
    openai_api_style: str = field(default_factory=_default_api_style)
    unity_host: str = field(
        default_factory=lambda: os.environ.get("SARI_UNITY_HOST", "localhost")
    )
    unity_port: int = field(default_factory=lambda: _env_int("SARI_UNITY_PORT", 8080))
    memory_dir: Path = field(
        default_factory=lambda: _env_path("SARI_MEMORY_DIR", DEFAULT_MEMORY_DIR)
    )
    screenshot_dir: Path = field(
        default_factory=lambda: _env_path("SARI_SCREENSHOT_DIR", DEFAULT_SCREENSHOT_DIR)
    )
    # Safety net on total pipeline stage executions across the whole run; the
    # per-sub-goal model/tool loop is bounded by max_turns_per_sub_goal.
    max_loop_iterations: int = field(
        default_factory=lambda: _env_int("SARI_MAX_LOOP_ITERATIONS", 64)
    )
    max_turns_per_sub_goal: int = field(
        default_factory=lambda: _env_int("SARI_MAX_TURNS_PER_SUB_GOAL", 8)
    )
    # Wall-clock cap on the whole run, checked between pipeline stages; a value
    # <= 0 disables it (None). Defaults to 45 minutes.
    max_runtime_seconds: int | None = field(
        default_factory=lambda: _env_optional_positive_int(
            "SARI_MAX_RUNTIME_SECONDS", 45 * 60
        )
    )
    unity_max_message_bytes: int | None = field(
        default_factory=lambda: _env_optional_positive_int(
            "SARI_UNITY_MAX_MESSAGE_BYTES",
            16 * 1024 * 1024,
        )
    )
    request_timeout_seconds: float = 30.0
    debug_enabled: bool = field(
        default_factory=lambda: _env_bool("SARI_DEBUG_ENABLED", False)
    )
    debug_host: str = field(
        default_factory=lambda: os.environ.get("SARI_DEBUG_HOST", "localhost")
    )
    debug_port: int = field(default_factory=lambda: _env_int("SARI_DEBUG_PORT", 8765))
    debug_replay_events: int = field(
        default_factory=lambda: _env_int("SARI_DEBUG_REPLAY_EVENTS", 1000)
    )
    debug_include_raw_llm_events: bool = field(
        default_factory=lambda: _env_bool("SARI_DEBUG_INCLUDE_RAW_LLM_EVENTS", True)
    )
    debug_include_prompts: bool = field(
        default_factory=lambda: _env_bool("SARI_DEBUG_INCLUDE_PROMPTS", True)
    )
    debug_image_max_edge: int = field(
        default_factory=lambda: _env_int("SARI_DEBUG_IMAGE_MAX_EDGE", 512)
    )
    debug_runs_dir: Path = field(
        default_factory=lambda: _env_path("SARI_DEBUG_RUNS_DIR", DEFAULT_DEBUG_RUNS_DIR)
    )

    @property
    def commands_url(self) -> str:
        return f"ws://{self.unity_host}:{self.unity_port}/commands"


def load_config() -> AgentConfig:
    load_dotenv(Path(os.environ.get("SARI_ENV_FILE", DEFAULT_ENV_FILE)))
    return AgentConfig()
