from __future__ import annotations

import pytest

from sari_agent.debug import DebugHub
from sari_agent.openai_client import (
    ChatCompletionsStreamAccumulator,
    ResponseStreamAccumulator,
    ResponsesClient,
    _to_chat_messages,
    _to_chat_tool,
    image_content_block,
)


class FakeResponsesStream:
    def __init__(self, events: list[dict[str, object]]) -> None:
        self.events = events

    async def __aenter__(self) -> "FakeResponsesStream":
        return self

    async def __aexit__(self, exc_type, exc, tb) -> None:
        return None

    def __aiter__(self):
        return self._iterate()

    async def _iterate(self):
        for event in self.events:
            yield event


class FakeResponses:
    def __init__(self, events: list[dict[str, object]]) -> None:
        self.events = events

    def stream(self, **kwargs) -> FakeResponsesStream:
        return FakeResponsesStream(self.events)


class FakeResponsesClient:
    def __init__(self, events: list[dict[str, object]]) -> None:
        self.responses = FakeResponses(events)


def test_streamed_function_call_argument_accumulation() -> None:
    accumulator = ResponseStreamAccumulator()

    accumulator.process_event(
        {
            "type": "response.output_item.added",
            "output_index": 0,
            "item": {
                "type": "function_call",
                "id": "fc_1",
                "call_id": "call_1",
                "name": "TranslateAgent",
            },
        }
    )
    accumulator.process_event(
        {
            "type": "response.function_call_arguments.delta",
            "output_index": 0,
            "delta": '{"translation":[0,0,',
        }
    )
    accumulator.process_event(
        {
            "type": "response.function_call_arguments.delta",
            "output_index": 0,
            "delta": '0.1],"rotation":[0,0,0]}',
        }
    )

    result = accumulator.result()

    assert result.tool_calls == [
        {
            "id": "fc_1",
            "call_id": "call_1",
            "name": "TranslateAgent",
            "arguments": {"translation": [0, 0, 0.1], "rotation": [0, 0, 0]},
            "arguments_json": '{"translation":[0,0,0.1],"rotation":[0,0,0]}',
        }
    ]


def test_image_content_block_handles_qwen_and_openai_shapes() -> None:
    qwen = image_content_block("Qwen-VL", "image/jpeg", "abc")
    openai = image_content_block("gpt-4.1", "image/jpeg", "abc")

    assert qwen == {
        "type": "image_url",
        "image_url": {"url": "data:image/jpeg;base64,abc"},
    }
    assert openai == {
        "type": "input_image",
        "image_url": "data:image/jpeg;base64,abc",
    }


def test_chat_completion_tool_call_argument_accumulation() -> None:
    accumulator = ChatCompletionsStreamAccumulator()

    accumulator.process_event(
        {
            "choices": [
                {
                    "delta": {
                        "tool_calls": [
                            {
                                "index": 0,
                                "id": "call_1",
                                "function": {"name": "TranslateAgent"},
                            }
                        ]
                    }
                }
            ]
        }
    )
    accumulator.process_event(
        {
            "choices": [
                {
                    "delta": {
                        "tool_calls": [
                            {
                                "index": 0,
                                "function": {"arguments": '{"translation":[0,0,'},
                            }
                        ]
                    }
                }
            ]
        }
    )
    accumulator.process_event(
        {
            "choices": [
                {
                    "delta": {
                        "tool_calls": [
                            {
                                "index": 0,
                                "function": {"arguments": '0.1],"rotation":[0,0,0]}'},
                            }
                        ]
                    }
                }
            ]
        }
    )

    result = accumulator.result()

    assert result.tool_calls == [
        {
            "id": "call_1",
            "call_id": "call_1",
            "name": "TranslateAgent",
            "arguments": {"translation": [0, 0, 0.1], "rotation": [0, 0, 0]},
            "arguments_json": '{"translation":[0,0,0.1],"rotation":[0,0,0]}',
        }
    ]
    assert result.message_items == [
        {
            "role": "assistant",
            "content": None,
            "tool_calls": [
                {
                    "id": "call_1",
                    "type": "function",
                    "function": {
                        "name": "TranslateAgent",
                        "arguments": '{"translation":[0,0,0.1],"rotation":[0,0,0]}',
                    },
                }
            ],
        }
    ]


def test_responses_items_are_converted_to_chat_messages_and_tools() -> None:
    messages = _to_chat_messages(
        [
            {"role": "user", "content": "look around"},
            {
                "type": "function_call_output",
                "call_id": "call_1",
                "output": '{"ok": true}',
            },
        ],
        "system prompt",
    )
    tool = _to_chat_tool(
        {
            "type": "function",
            "name": "RequestScreenshot",
            "description": "Take a screenshot.",
            "parameters": {"type": "object", "properties": {}},
        }
    )

    assert messages == [
        {"role": "system", "content": "system prompt"},
        {"role": "user", "content": "look around"},
        {"role": "tool", "tool_call_id": "call_1", "content": '{"ok": true}'},
    ]
    assert tool == {
        "type": "function",
        "function": {
            "name": "RequestScreenshot",
            "description": "Take a screenshot.",
            "parameters": {"type": "object", "properties": {}},
        },
    }


def test_qwen_chat_messages_use_nested_image_url_blocks() -> None:
    messages = _to_chat_messages(
        [
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": "What is in this image?"},
                    {
                        "type": "input_image",
                        "image_url": "data:image/jpeg;base64,abc",
                    },
                ],
            }
        ],
        None,
        model="Qwen/Qwen3.6-27B",
    )

    assert messages == [
        {
            "role": "user",
            "content": [
                {"type": "text", "text": "What is in this image?"},
                {
                    "type": "image_url",
                    "image_url": {"url": "data:image/jpeg;base64,abc"},
                },
            ],
        }
    ]


@pytest.mark.asyncio
async def test_responses_client_publishes_debug_stream_events() -> None:
    hub = DebugHub(enabled=True, run_id="run", replay_limit=20)
    client = ResponsesClient(
        model="debug-model",
        client=FakeResponsesClient(
            [
                {"type": "response.output_text.delta", "delta": "hello"},
                {"type": "response.reasoning_summary_text.delta", "delta": "thinking"},
                {
                    "type": "response.completed",
                    "response": {
                        "output": [
                            {
                                "type": "message",
                                "content": [
                                    {"type": "output_text", "text": "hello"},
                                ],
                            }
                        ]
                    },
                },
            ]
        ),
        debug_hub=hub,
    )

    result = await client.run_streamed(
        input_items=[{"role": "user", "content": "say hello"}],
        tools=[{"type": "function", "name": "RequestScreenshot"}],
        instructions="system prompt",
        stage="run_model",
    )

    events = [event.to_dict() for event in hub.replay_events()]
    event_types = [event["type"] for event in events]

    assert result.text == "hello"
    assert event_types[0] == "llm.request.started"
    assert event_types.count("llm.raw_event") == 3
    assert "llm.text.delta" in event_types
    assert "llm.reasoning.delta" in event_types
    assert event_types[-1] == "llm.text.completed"
    assert events[0]["payload"]["input_items"] == [{"role": "user", "content": "say hello"}]
