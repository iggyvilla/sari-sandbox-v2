"""
Tests every accepted command on the /commands WebSocket endpoint.

Requires: pip install websocket-client
Usage:    python test_commands_client.py [--host localhost] [--port 8080]
          python test_commands_client.py --v1-compatibility

ResetEnvironment is intentionally tested last because it resets the scene state.
"""

import argparse
import base64
import binascii
import json
import re
import sys
import time
from datetime import datetime
from pathlib import Path

import websocket


HOST = "localhost"
PORT = 8080
MAX_TRANSLATION_STEP = 1.0
SMALL_AGENT_STEP = 0.1
SMALL_HAND_STEP = 0.05

ACCEPTED_COMMANDS = {
    "TransformAgent",
    "TransformHand",
    "TranslateAgent",
    "TranslateHand",
    "ResetHandPosition",
    "IsHoldingItem",
    "ToggleLeftHandGrip",
    "ToggleRightHandGrip",
    "ToggleRightPoke",
    "TogglePoke",
    "TogglePoint",
    "RequestScreenshot",
    "ResetEnvironment",
}

VECTOR_PATTERN = re.compile(
    r"\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)"
)

AGENT_STATE_KEYS = {
    "current_position",
    "current_rotation",
    "collision",
}

HAND_STATE_KEYS = {
    "current_left_hand_position",
    "current_left_hand_rotation",
    "left_hand_can_grab",
    "left_hand_gripping",
    "current_right_hand_position",
    "current_right_hand_rotation",
    "right_hand_can_grab",
    "right_hand_gripping",
}


class CommandTestError(RuntimeError):
    pass


class CommandTester:
    def __init__(self, ws, screenshot_dir, delay):
        self.ws = ws
        self.screenshot_dir = Path(screenshot_dir).expanduser()
        self.delay = delay
        self.sent_commands = set()
        self.passed = 0

    def send_command(self, command, payload=None, validator=None):
        message = {"command": command}
        if payload:
            message.update(payload)

        encoded = json.dumps(message)
        print(f"\n[send] {encoded}")
        self.ws.send(encoded)
        self.sent_commands.add(command)

        response = self.ws.recv()
        if isinstance(response, bytes):
            response = response.decode("utf-8")

        if validator is not None:
            validator(response)

        print(f"[pass] {command}: {summarize_response(response)}")
        self.passed += 1
        if self.delay > 0:
            time.sleep(self.delay)
        return response

    def assert_relative_translation(self, translation):
        assert_small_delta(translation, "relative translation")

    def assert_absolute_target(self, current_position, target_position):
        delta = [
            target_position[index] - current_position[index]
            for index in range(3)
        ]
        assert_small_delta(delta, "TransformAgent target delta")

    def save_screenshot(self, response):
        try:
            png_bytes = base64.b64decode(response, validate=True)
        except (binascii.Error, ValueError) as exc:
            raise CommandTestError("RequestScreenshot response was not valid base64") from exc

        if not png_bytes.startswith(b"\x89PNG\r\n\x1a\n"):
            raise CommandTestError("RequestScreenshot response was not a PNG image")

        self.screenshot_dir.mkdir(parents=True, exist_ok=True)
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
        screenshot_path = self.screenshot_dir / f"commands-screenshot-{timestamp}.png"
        screenshot_path.write_bytes(png_bytes)
        print(f"[info] Screenshot saved to {screenshot_path.resolve()}")


def assert_small_delta(delta, label):
    if len(delta) != 3:
        raise CommandTestError(f"{label} must contain exactly three values")

    oversized = [value for value in delta if abs(value) > MAX_TRANSLATION_STEP]
    if oversized:
        raise CommandTestError(
            f"{label} exceeds {MAX_TRANSLATION_STEP} unit per axis: {delta}"
        )


def expect_prefix(prefix):
    def validate(response):
        if not response.startswith(prefix):
            raise CommandTestError(
                f"Expected response starting with {prefix!r}, got {response!r}"
            )

    return validate


def expect_exact(*expected):
    def validate(response):
        if response not in expected:
            raise CommandTestError(
                f"Expected one of {expected!r}, got {response!r}"
            )

    return validate


def parse_agent_pose(response, v1_compatibility):
    if not v1_compatibility:
        state = parse_json_object(response, "agent state")
        expect_keys(state, AGENT_STATE_KEYS, "agent state")
        position = expect_vector(state["current_position"], "current_position")
        rotation = expect_vector(state["current_rotation"], "current_rotation")
        expect_bool(state["collision"], "collision")
        return position, rotation

    lines = response.splitlines()
    if len(lines) < 2:
        raise CommandTestError(f"Could not parse agent pose from {response!r}")

    position = parse_vector_line(lines[0], "Current position:")
    rotation = parse_vector_line(lines[1], "Current rotation:")
    return position, rotation


def parse_vector_line(line, prefix):
    if not line.startswith(prefix):
        raise CommandTestError(f"Expected {prefix!r} line, got {line!r}")

    match = VECTOR_PATTERN.search(line)
    if match is None:
        raise CommandTestError(f"Could not parse Vector3 from {line!r}")

    return [float(match.group(index)) for index in range(1, 4)]


def expect_agent_state(v1_compatibility):
    def validate(response):
        parse_agent_pose(response, v1_compatibility)

    return validate


def expect_hand_state(v1_compatibility):
    if v1_compatibility:
        return expect_prefix("Current left hand position:")

    def validate(response):
        state = parse_json_object(response, "hand state")
        expect_keys(state, HAND_STATE_KEYS, "hand state")
        expect_vector(state["current_left_hand_position"], "current_left_hand_position")
        expect_vector(state["current_left_hand_rotation"], "current_left_hand_rotation")
        # The idle left hand is nowhere near an item during this test.
        if state["left_hand_can_grab"] is not False:
            raise CommandTestError(
                f"left_hand_can_grab must be JSON false, got {state['left_hand_can_grab']!r}"
            )
        expect_bool(state["left_hand_gripping"], "left_hand_gripping")
        expect_vector(state["current_right_hand_position"], "current_right_hand_position")
        expect_vector(state["current_right_hand_rotation"], "current_right_hand_rotation")
        expect_bool(state["right_hand_can_grab"], "right_hand_can_grab")
        expect_bool(state["right_hand_gripping"], "right_hand_gripping")

    return validate


def expect_grip_state(hand_side, v1_compatibility):
    if v1_compatibility:
        return expect_prefix(f"{hand_side} Grip: ")

    return expect_hand_state(False)


def parse_json_object(response, label):
    try:
        value = json.loads(response)
    except json.JSONDecodeError as exc:
        raise CommandTestError(f"{label} response was not valid JSON: {response!r}") from exc

    if not isinstance(value, dict):
        raise CommandTestError(f"{label} response must be a JSON object, got {value!r}")
    return value


def expect_keys(value, expected_keys, label):
    actual_keys = set(value)
    if actual_keys != expected_keys:
        raise CommandTestError(
            f"{label} keys must be {sorted(expected_keys)!r}, got {sorted(actual_keys)!r}"
        )


def expect_vector(value, label):
    if (
        not isinstance(value, list)
        or len(value) != 3
        or any(
            isinstance(component, bool) or not isinstance(component, (int, float))
            for component in value
        )
    ):
        raise CommandTestError(f"{label} must be a three-number JSON array, got {value!r}")
    return value


def expect_bool(value, label):
    if not isinstance(value, bool):
        raise CommandTestError(f"{label} must be a JSON boolean, got {value!r}")


def summarize_response(response):
    if len(response) > 160:
        return f"<{len(response)} characters>"
    return response.replace("\n", " | ")


def run(
    host,
    port,
    timeout,
    delay,
    screenshot_dir,
    skip_reset_environment,
    v1_compatibility,
):
    url = f"ws://{host}:{port}/commands"
    print(f"Connecting to {url} ...")
    print(f"Expecting {'v1 compatibility' if v1_compatibility else 'new JSON'} mode.")

    ws = websocket.create_connection(url, timeout=timeout)
    tester = CommandTester(ws, screenshot_dir, delay)

    try:
        # Query the current pose without moving. In v1 mode, TransformAgent is
        # absolute, so this pose is used to keep its target only a small step away.
        tester.assert_relative_translation([0.0, 0.0, 0.0])
        pose_response = tester.send_command(
            "TranslateAgent",
            {"translation": [0.0, 0.0, 0.0], "rotation": [0.0, 0.0, 0.0]},
            expect_agent_state(v1_compatibility),
        )
        current_position, current_rotation = parse_agent_pose(
            pose_response,
            v1_compatibility,
        )

        if v1_compatibility:
            transform_translation = [
                current_position[0],
                current_position[1],
                current_position[2] + SMALL_AGENT_STEP,
            ]
            transform_rotation = current_rotation
            tester.assert_absolute_target(current_position, transform_translation)
        else:
            transform_translation = [0.0, 0.0, SMALL_AGENT_STEP]
            transform_rotation = [0.0, 1.0, 0.0]
            tester.assert_relative_translation(transform_translation)

        tester.send_command(
            "TransformAgent",
            {"translation": transform_translation, "rotation": transform_rotation},
            expect_agent_state(v1_compatibility),
        )

        tester.assert_relative_translation([0.0, 0.0, -SMALL_AGENT_STEP])
        tester.send_command(
            "TranslateAgent",
            {
                "translation": [0.0, 0.0, -SMALL_AGENT_STEP],
                "rotation": [0.0, 1.0, 0.0],
            },
            expect_agent_state(v1_compatibility),
        )

        tester.send_command(
            "ResetHandPosition",
            validator=expect_exact("Hand position reset"),
        )

        if v1_compatibility:
            transform_hand_payload = {
                "handPosition": [0.0, 0.0, 0.2],
                "handRotation": [0.0, 0.0, 0.0],
            }
            assert_small_delta(
                transform_hand_payload["handPosition"],
                "TransformHand local position",
            )
        else:
            transform_hand_payload = {
                "translation": [SMALL_HAND_STEP, 0.0, 0.0],
                "rotation": [0.0, 1.0, 0.0],
            }
            tester.assert_relative_translation(transform_hand_payload["translation"])

        tester.send_command(
            "TransformHand",
            transform_hand_payload,
            expect_hand_state(v1_compatibility),
        )

        tester.assert_relative_translation([SMALL_HAND_STEP, 0.0, 0.0])
        tester.send_command(
            "TranslateHand",
            {
                "translation": [SMALL_HAND_STEP, 0.0, 0.0],
                "rotation": [0.0, 1.0, 0.0],
            },
            expect_hand_state(v1_compatibility),
        )

        tester.send_command(
            "ResetHandPosition",
            validator=expect_exact("Hand position reset"),
        )
        tester.send_command(
            "IsHoldingItem",
            validator=expect_exact("true", "false"),
        )

        tester.send_command(
            "ToggleRightHandGrip",
            validator=expect_grip_state("Right", v1_compatibility),
        )
        tester.send_command(
            "ToggleLeftHandGrip",
            validator=expect_grip_state("Left", v1_compatibility),
        )

        tester.send_command(
            "ToggleRightPoke",
            validator=expect_prefix("Right Poke: "),
        )
        tester.send_command(
            "TogglePoke",
            validator=expect_prefix("Right Poke: "),
        )
        tester.send_command(
            "TogglePoint",
            validator=expect_prefix("Right Poke: "),
        )

        tester.send_command(
            "RequestScreenshot",
            validator=tester.save_screenshot,
        )

        if skip_reset_environment:
            print("\n[skip] ResetEnvironment")
        else:
            tester.send_command(
                "ResetEnvironment",
                validator=expect_exact("Environment reset"),
            )

        expected_commands = ACCEPTED_COMMANDS.copy()
        if skip_reset_environment:
            expected_commands.remove("ResetEnvironment")
        missing_commands = expected_commands - tester.sent_commands
        if missing_commands:
            raise CommandTestError(
                f"Commands were not tested: {sorted(missing_commands)}"
            )

        print(f"\nAll {tester.passed} command checks passed.")
    finally:
        ws.close()


def main():
    default_screenshot_dir = Path(__file__).resolve().parent / "screenshots"
    parser = argparse.ArgumentParser(description="Sari /commands WebSocket test client")
    parser.add_argument("--host", default=HOST)
    parser.add_argument("--port", type=int, default=PORT)
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument("--delay", type=float, default=0.1)
    parser.add_argument("--screenshot-dir", default=default_screenshot_dir)
    parser.add_argument(
        "--v1-compatibility",
        action="store_true",
        help="Expect the WebSocketHandler's Sari Sandbox v1 compatibility layer.",
    )
    parser.add_argument(
        "--skip-reset-environment",
        action="store_true",
        help="Skip the destructive ResetEnvironment command.",
    )
    args = parser.parse_args()

    try:
        run(
            args.host,
            args.port,
            args.timeout,
            args.delay,
            args.screenshot_dir,
            args.skip_reset_environment,
            args.v1_compatibility,
        )
    except (CommandTestError, OSError, websocket.WebSocketException) as exc:
        print(f"\n[fail] {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
