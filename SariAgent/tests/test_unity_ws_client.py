from __future__ import annotations

import base64
import json

import pytest

from sari_agent.unity.ws_client import UnityCommandClient, UnityCommandError


PNG_BYTES = b"\x89PNG\r\n\x1a\n" + b"\x00" * 8


def test_json_string_response_normalization(tmp_path) -> None:
    client = UnityCommandClient(screenshot_dir=tmp_path)

    result = client.normalize_response("ResetHandPosition", json.dumps("Hand position reset"))

    assert result == {
        "command": "ResetHandPosition",
        "result": "Hand position reset",
    }


def test_json_object_response_normalization(tmp_path) -> None:
    client = UnityCommandClient(screenshot_dir=tmp_path)
    payload = {"current_position": [0, 0, 0], "collision": False}

    result = client.normalize_response("TranslateAgent", json.dumps(payload))

    assert result == {
        "command": "TranslateAgent",
        "result": payload,
    }


def test_legacy_text_response_is_rejected(tmp_path) -> None:
    client = UnityCommandClient(screenshot_dir=tmp_path)

    with pytest.raises(UnityCommandError, match="Legacy/v1 text responses are unsupported"):
        client.normalize_response("ResetHandPosition", "Hand position reset")


def test_screenshot_base64_is_saved_to_file(tmp_path) -> None:
    client = UnityCommandClient(screenshot_dir=tmp_path)
    response = base64.b64encode(PNG_BYTES).decode("ascii")

    result = client.normalize_response("RequestScreenshot", response)
    screenshot = result["screenshot"]

    assert result["command"] == "RequestScreenshot"
    assert screenshot["mime_type"] == "image/png"
    assert screenshot["bytes"] == len(PNG_BYTES)
    assert (tmp_path / screenshot["path"].split("/")[-1]).read_bytes() == PNG_BYTES
