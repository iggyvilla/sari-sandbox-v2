from __future__ import annotations

from sari_agent.config import PACKAGE_ROOT, load_config, load_dotenv


def test_load_dotenv_sets_values_without_overriding_existing_env(
    monkeypatch, tmp_path
) -> None:
    env_file = tmp_path / ".env"
    env_file.write_text(
        "\n".join(
            [
                "SARI_MODEL=from-file",
                "SARI_UNITY_HOST=from-file-host",
            ]
        ),
        encoding="utf-8",
    )
    monkeypatch.setenv("SARI_MODEL", "from-shell")
    monkeypatch.setenv("SARI_ENV_FILE", str(env_file))
    monkeypatch.delenv("SARI_UNITY_HOST", raising=False)

    load_dotenv(env_file)

    assert load_config().model == "from-shell"
    assert load_config().unity_host == "from-file-host"


def test_load_config_reads_env_file_endpoint_settings(monkeypatch, tmp_path) -> None:
    env_file = tmp_path / ".env"
    env_file.write_text(
        "\n".join(
            [
                "SARI_MODEL=qwen-local",
                "SARI_OPENAI_API_KEY=test-key",
                "SARI_OPENAI_BASE_URL=http://localhost:8000/v1",
                "SARI_OPENAI_API_STYLE=responses",
                "SARI_MEMORY_DIR=custom-memory",
                "SARI_SCREENSHOT_DIR=custom-screenshots",
                "SARI_MAX_LOOP_ITERATIONS=3",
                "SARI_UNITY_MAX_MESSAGE_BYTES=2097152",
                "SARI_DEBUG_ENABLED=1",
                "SARI_DEBUG_HOST=127.0.0.1",
                "SARI_DEBUG_PORT=9999",
                "SARI_DEBUG_REPLAY_EVENTS=25",
                "SARI_DEBUG_INCLUDE_RAW_LLM_EVENTS=0",
                "SARI_DEBUG_INCLUDE_PROMPTS=false",
                "SARI_DEBUG_IMAGE_MAX_EDGE=256",
                "SARI_DEBUG_RUNS_DIR=custom-debug-runs",
            ]
        ),
        encoding="utf-8",
    )

    for name in (
        "SARI_MODEL",
        "SARI_OPENAI_API_KEY",
        "SARI_OPENAI_BASE_URL",
        "SARI_OPENAI_API_STYLE",
        "SARI_MEMORY_DIR",
        "SARI_SCREENSHOT_DIR",
        "SARI_MAX_LOOP_ITERATIONS",
        "SARI_UNITY_MAX_MESSAGE_BYTES",
        "SARI_DEBUG_ENABLED",
        "SARI_DEBUG_HOST",
        "SARI_DEBUG_PORT",
        "SARI_DEBUG_REPLAY_EVENTS",
        "SARI_DEBUG_INCLUDE_RAW_LLM_EVENTS",
        "SARI_DEBUG_INCLUDE_PROMPTS",
        "SARI_DEBUG_IMAGE_MAX_EDGE",
        "SARI_DEBUG_RUNS_DIR",
    ):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setenv("SARI_ENV_FILE", str(env_file))

    config = load_config()

    assert config.model == "qwen-local"
    assert config.openai_api_key == "test-key"
    assert config.openai_base_url == "http://localhost:8000/v1"
    assert config.openai_api_style == "responses"
    assert config.memory_dir == PACKAGE_ROOT / "custom-memory"
    assert config.screenshot_dir == PACKAGE_ROOT / "custom-screenshots"
    assert config.max_loop_iterations == 3
    assert config.unity_max_message_bytes == 2097152
    assert config.debug_enabled is True
    assert config.debug_host == "127.0.0.1"
    assert config.debug_port == 9999
    assert config.debug_replay_events == 25
    assert config.debug_include_raw_llm_events is False
    assert config.debug_include_prompts is False
    assert config.debug_image_max_edge == 256
    assert config.debug_runs_dir == PACKAGE_ROOT / "custom-debug-runs"


def test_load_config_allows_unlimited_unity_message_size(
    monkeypatch, tmp_path
) -> None:
    env_file = tmp_path / ".env"
    env_file.write_text("SARI_UNITY_MAX_MESSAGE_BYTES=0", encoding="utf-8")
    monkeypatch.delenv("SARI_UNITY_MAX_MESSAGE_BYTES", raising=False)
    monkeypatch.setenv("SARI_ENV_FILE", str(env_file))

    assert load_config().unity_max_message_bytes is None


def test_load_config_treats_whitespace_only_endpoint_settings_as_missing(
    monkeypatch, tmp_path
) -> None:
    env_file = tmp_path / ".env"
    env_file.write_text(
        "\n".join(
            [
                'SARI_OPENAI_API_KEY=" "',
                "SARI_OPENAI_BASE_URL=   ",
            ]
        ),
        encoding="utf-8",
    )

    monkeypatch.delenv("SARI_OPENAI_API_KEY", raising=False)
    monkeypatch.delenv("SARI_OPENAI_BASE_URL", raising=False)
    monkeypatch.delenv("OPENAI_API_KEY", raising=False)
    monkeypatch.delenv("OPENAI_BASE_URL", raising=False)
    monkeypatch.setenv("SARI_ENV_FILE", str(env_file))

    config = load_config()

    assert config.openai_api_key is None
    assert config.openai_base_url is None


def test_load_config_defaults_external_endpoint_to_chat_completions(
    monkeypatch, tmp_path
) -> None:
    env_file = tmp_path / ".env"
    env_file.write_text(
        "SARI_OPENAI_BASE_URL=http://localhost:8000/v1",
        encoding="utf-8",
    )
    monkeypatch.delenv("SARI_OPENAI_API_STYLE", raising=False)
    monkeypatch.delenv("SARI_OPENAI_API_MODE", raising=False)
    monkeypatch.delenv("SARI_OPENAI_BASE_URL", raising=False)
    monkeypatch.delenv("OPENAI_BASE_URL", raising=False)
    monkeypatch.setenv("SARI_ENV_FILE", str(env_file))

    assert load_config().openai_api_style == "chat_completions"
