from __future__ import annotations

import pytest

from sari_agent.debug import DebugHub
from sari_agent.openai_client import (
    ChatCompletionsStreamAccumulator,
    ResponseStreamAccumulator,
    ResponsesClient,
    _normalize_usage,
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


def test_normalize_usage_handles_both_naming_conventions() -> None:
    assert _normalize_usage({"input_tokens": 10, "output_tokens": 5, "total_tokens": 15}) == {
        "input_tokens": 10,
        "output_tokens": 5,
        "total_tokens": 15,
    }
    assert _normalize_usage({"prompt_tokens": 10, "completion_tokens": 5}) == {
        "input_tokens": 10,
        "output_tokens": 5,
        "total_tokens": 15,
    }
    assert _normalize_usage(None) is None
    assert _normalize_usage({"unrelated": "value"}) is None


def test_responses_accumulator_captures_usage() -> None:
    accumulator = ResponseStreamAccumulator()
    accumulator.process_event({"type": "response.output_text.delta", "delta": "hi"})
    accumulator.process_event(
        {
            "type": "response.completed",
            "response": {
                "output": [],
                "usage": {"input_tokens": 512, "output_tokens": 88, "total_tokens": 600},
            },
        }
    )

    result = accumulator.result()

    assert result.usage == {"input_tokens": 512, "output_tokens": 88, "total_tokens": 600}


def test_chat_accumulator_captures_usage_only_final_chunk() -> None:
    accumulator = ChatCompletionsStreamAccumulator()
    accumulator.process_event({"choices": [{"delta": {"content": "hello"}}]})
    accumulator.process_event(
        {
            "choices": [],
            "usage": {"prompt_tokens": 42, "completion_tokens": 7, "total_tokens": 49},
        }
    )

    result = accumulator.result()

    assert result.text == "hello"
    assert result.usage == {"input_tokens": 42, "output_tokens": 7, "total_tokens": 49}


def test_image_content_block_handles_qwen_and_openai_shapes() -> None:
    qwen = image_content_block("Qwen-VL", "image/jpeg", "abc")
    openai = image_content_block("gpt-4.1", "image/jpeg", "abc")
    openai_chat = image_content_block(
        "gpt-4.1",
        "image/jpeg",
        "abc",
        api_style="chat_completions",
    )

    assert qwen == {
        "type": "image_url",
        "image_url": {"url": "data:image/jpeg;base64,abc"},
    }
    assert openai == {
        "type": "input_image",
        "image_url": "data:image/jpeg;base64,abc",
    }
    assert openai_chat == {
        "type": "image_url",
        "image_url": {"url": "data:image/jpeg;base64,abc"},
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


class FakeChatCompletions:
    def __init__(self, events: list[dict[str, object]]) -> None:
        self.events = events
        self.create_kwargs: dict[str, object] = {}

    async def create(self, **kwargs):
        self.create_kwargs = kwargs

        async def _iterate():
            for event in self.events:
                yield event

        return _iterate()


class FakeChatClient:
    def __init__(self, events: list[dict[str, object]]) -> None:
        self.chat = type("Chat", (), {})()
        self.chat.completions = FakeChatCompletions(events)


@pytest.mark.asyncio
async def test_chat_completions_run_requests_and_returns_usage() -> None:
    fake = FakeChatClient(
        [
            {"choices": [{"delta": {"content": "hello"}}]},
            {
                "choices": [],
                "usage": {"prompt_tokens": 12, "completion_tokens": 3, "total_tokens": 15},
            },
        ]
    )
    client = ResponsesClient(model="demo", api_style="chat_completions", client=fake)

    result = await client.run_streamed(
        input_items=[{"role": "user", "content": "say hello"}],
        tools=[],
        stage="agent_turn",
    )

    assert fake.chat.completions.create_kwargs["stream_options"] == {"include_usage": True}
    assert "tools" not in fake.chat.completions.create_kwargs
    assert result.text == "hello"
    assert result.usage == {"input_tokens": 12, "output_tokens": 3, "total_tokens": 15}


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
        stage="agent_turn",
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
