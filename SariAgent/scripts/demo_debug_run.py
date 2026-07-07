"""Emit a realistic debug event stream without OpenAI or Unity.

Drives the real AgentLoop/DebugHub/DebugWebSocketServer with fake model and
tool stages so the debug website can be developed and demoed standalone.

    python scripts/demo_debug_run.py            # one canned run, then idle
    python scripts/demo_debug_run.py --serve    # wait for prompts from the UI
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from sari_agent.context import AgentContext, StageOutput
from sari_agent.debug import DebugHub, DebugWebSocketServer
from sari_agent.debug.images import thumbnail_data_url
from sari_agent.loop import AgentLoop, EndConditionStage, LoopResult, LoopStage, PlanSubGoalsStage
from sari_agent.serve import ServeController

PNG_BYTES = (
    b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01"
    b"\x08\x06\x00\x00\x00\x1f\x15\xc4\x89\x00\x00\x00\rIDATx\x9cc\xf8\xff"
    b"\xff?\x00\x05\xfe\x02\xfeA\xe2%\xb3\x00\x00\x00\x00IEND\xaeB`\x82"
)

REASONING = (
    "The user wants me to explore the store. I should start by taking a "
    "screenshot to see my surroundings, then translate toward the shelves."
)
FIRST_TEXT = "Let me take a look around and move toward the shelf aisle."
FINAL_TEXT = (
    "I explored the store: the entrance opens onto three aisles, with snacks "
    "on the left, drinks in the center chillers, and household goods at the back."
)


class FakeLoadMemoryStage(LoopStage):
    name = "load_memory"

    async def run(self, context: AgentContext) -> LoopResult:
        await asyncio.sleep(0.2)
        context.stage_output = StageOutput(
            text="Loaded 2 memory document(s): SARI.md, store.md"
        )
        return LoopResult.ADVANCE


class FakeRunModelStage(LoopStage):
    name = "run_model"

    async def run(self, context: AgentContext) -> LoopResult:
        hub = context.debug_hub
        first_pass = not context.metadata.get("model_ran")
        context.metadata["model_ran"] = True

        await hub.publish(
            "llm.request.started",
            stage=self.name,
            summary="Started LLM request to demo-model",
            payload={
                "model": "demo-model",
                "api_style": "responses",
                "tool_names": ["RequestScreenshot", "TranslateAgent"],
                "input_item_count": 1 if first_pass else 3,
                "instructions_present": True,
                "instructions": "You are SariAgent, a store exploration assistant.",
                "input_items": [{"role": "user", "content": context.user_input}],
            },
        )
        for word in REASONING.split(" "):
            await hub.publish(
                "llm.reasoning.delta",
                stage=self.name,
                summary="Provider-exposed reasoning delta",
                payload={"text": word + " "},
            )
            await asyncio.sleep(0.04)

        text = FIRST_TEXT if first_pass else FINAL_TEXT
        for word in text.split(" "):
            await hub.publish(
                "llm.text.delta",
                stage=self.name,
                summary="LLM text delta",
                payload={"text": word + " "},
            )
            await asyncio.sleep(0.05)
        await hub.publish(
            "llm.text.completed",
            stage=self.name,
            summary="LLM text completed",
            payload={"text": text},
        )

        usage = (
            {"input_tokens": 850, "output_tokens": 240, "total_tokens": 1090}
            if first_pass
            else {"input_tokens": 1420, "output_tokens": 180, "total_tokens": 1600}
        )
        context.record_usage(self.name, usage)
        context.stage_output = StageOutput(
            text=(
                f"Model call: {usage['total_tokens']:,} tokens "
                f"({usage['input_tokens']:,} in / {usage['output_tokens']:,} out)"
            ),
            usage=usage,
        )

        context.response_text = text
        if first_pass:
            context.pending_tool_calls = [
                {
                    "call_id": "call_1",
                    "name": "RequestScreenshot",
                    "arguments": {},
                    "arguments_json": "{}",
                },
                {
                    "call_id": "call_2",
                    "name": "TranslateAgent",
                    "arguments": {"direction": "forward", "distance": 2.0},
                    "arguments_json": '{"direction": "forward", "distance": 2.0}',
                },
            ]
        return LoopResult.ADVANCE


class FakeExecuteToolsStage(LoopStage):
    name = "execute_tools"

    async def run(self, context: AgentContext) -> LoopResult:
        if not context.pending_tool_calls:
            return LoopResult.ADVANCE
        hub = context.debug_hub
        for call in context.pending_tool_calls:
            call_id = call["call_id"]
            tool_name = call["name"]
            tool_call = {
                "call_id": call_id,
                "tool_name": tool_name,
                "arguments_json": call["arguments_json"],
            }
            await hub.publish(
                "tool.call.started",
                stage=self.name,
                summary=f"Called {tool_name}",
                payload={
                    "call_id": call_id,
                    "tool_name": tool_name,
                    "arguments": call["arguments"],
                    "arguments_json": call["arguments_json"],
                },
            )
            await hub.publish(
                "unity.command.sent",
                stage=self.name,
                summary=f"Sent {tool_name} to Unity",
                payload={
                    "url": "ws://localhost:8080/commands",
                    "command": tool_name,
                    "payload": {"command": tool_name, **call["arguments"]},
                    "raw_json": call["arguments_json"],
                    "tool_call": tool_call,
                },
            )
            await asyncio.sleep(0.4)

            if tool_name == "RequestScreenshot":
                result = {
                    "command": tool_name,
                    "screenshot": {"path": "/tmp/demo.png", "bytes": len(PNG_BYTES), "mime_type": "image/png"},
                }
                raw_response = {"kind": "base64", "bytes": len(PNG_BYTES), "mime_type": "image/png"}
                content = [
                    {
                        "kind": "image",
                        "mime_type": "image/png",
                        "path": "/tmp/demo.png",
                        "bytes": len(PNG_BYTES),
                        "thumbnail_data_url": thumbnail_data_url(PNG_BYTES, max_edge=64),
                    }
                ]
            else:
                result = {"command": tool_name, "result": {"moved": True, "position": [1.0, 0.0, 3.5]}}
                raw_response = {"kind": "text", "raw_text": '{"moved": true, "position": [1.0, 0.0, 3.5]}'}
                content = [{"kind": "text", "text": '{"moved": true, "position": [1.0, 0.0, 3.5]}'}]

            await hub.publish(
                "unity.command.received",
                stage=self.name,
                summary=f"Received {tool_name} response from Unity",
                payload={
                    "url": "ws://localhost:8080/commands",
                    "command": tool_name,
                    "duration_ms": 412.5,
                    "tool_call": tool_call,
                    "raw_response": raw_response,
                    "normalized_result": result,
                    "content": content,
                },
            )
            await hub.publish(
                "tool.call.completed",
                stage=self.name,
                summary=f"Completed {tool_name}",
                payload={
                    "call_id": call_id,
                    "tool_name": tool_name,
                    "duration_ms": 415.2,
                    "result": result,
                    "content": content,
                },
            )
        executed = [call["name"] for call in context.pending_tool_calls]
        context.stage_output = StageOutput(
            text=f"Executed {len(executed)} tool call(s): {', '.join(executed)}"
        )
        context.pending_tool_calls = []
        return LoopResult.REPEAT


class FakeMemoryAssemblyStage(LoopStage):
    name = "memory_assembly"

    async def run(self, context: AgentContext) -> LoopResult:
        current = context.current_sub_goal
        if current is not None and current.status != "completed":
            current.status = "completed"
            current.result = context.response_text
        return LoopResult.ADVANCE


def build_loop() -> AgentLoop:
    return AgentLoop(
        [
            FakeLoadMemoryStage(),
            PlanSubGoalsStage(),
            FakeRunModelStage(),
            FakeExecuteToolsStage(),
            FakeMemoryAssemblyStage(),
            EndConditionStage(),
        ],
        max_iterations=40,
    )


async def run_once(hub: DebugHub, prompt: str) -> None:
    context = AgentContext(user_input=prompt, debug_hub=hub)
    await build_loop().run(context)


async def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serve", action="store_true", help="Wait for prompts from the debug UI.")
    parser.add_argument("--host", default="localhost")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--prompt", default="look around the store", help="Prompt for the canned run.")
    args = parser.parse_args()

    hub = DebugHub(enabled=True)
    controller = ServeController(hub, mode="serve" if args.serve else "cli")
    server = DebugWebSocketServer(
        hub,
        host=args.host,
        port=args.port,
        on_client_message=controller.handle_client_message,
        status_provider=controller.status_payload,
    )
    await server.start()
    print(f"Demo debug websocket on {server.url}")

    try:
        if args.serve:
            while True:
                controller.state = "idle"
                await controller.publish_status()
                print("Waiting for a prompt from the debug UI...")
                prompt = await controller.next_prompt()
                hub.begin_run()
                controller.state = "running"
                await hub.publish(
                    "server.run.accepted",
                    summary="Prompt accepted",
                    payload={"prompt": prompt, "run_id": hub.run_id},
                )
                await controller.publish_status()
                await run_once(hub, prompt)
        else:
            controller.state = "running"
            await controller.publish_status()
            await run_once(hub, args.prompt)
            controller.state = "idle"
            controller.mode = "cli"
            await controller.publish_status()
            print("Run finished; server stays up for replay. Ctrl-C to exit.")
            await asyncio.Event().wait()
    finally:
        await server.stop()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
