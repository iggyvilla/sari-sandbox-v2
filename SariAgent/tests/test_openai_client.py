from __future__ import annotations

from sari_agent.openai_client import ResponseStreamAccumulator, image_content_block


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
