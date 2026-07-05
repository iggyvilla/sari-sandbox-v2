"""CLI entrypoint for the SariAgent harness."""

from __future__ import annotations

import argparse
import asyncio

from sari_agent.config import AgentConfig, load_config
from sari_agent.context import AgentContext
from sari_agent.debug import DebugHub, DebugWebSocketServer
from sari_agent.loop import (
    AgentLoop,
    EndConditionStage,
    ExecuteToolsStage,
    LoadMemoryStage,
    MemoryAssemblyStage,
    PlanSubGoalsStage,
    RunModelStage,
)
from sari_agent.openai_client import ResponsesClient
from sari_agent.serve import ServeController
from sari_agent.terminal import console, status
from sari_agent.tools.unity_commands import assemble as assemble_unity_tools
from sari_agent.unity.ws_client import UnityCommandClient


def _build_loop(config: AgentConfig, debug_hub: DebugHub) -> AgentLoop:
    unity_client = UnityCommandClient(
        host=config.unity_host,
        port=config.unity_port,
        screenshot_dir=config.screenshot_dir,
        timeout_seconds=config.request_timeout_seconds,
        max_message_bytes=config.unity_max_message_bytes,
        debug_hub=debug_hub,
    )
    tool_definitions, registry = assemble_unity_tools(client=unity_client)
    responses_client = ResponsesClient(
        model=config.model,
        api_key=config.openai_api_key,
        base_url=config.openai_base_url,
        api_style=config.openai_api_style,
        debug_hub=debug_hub,
    )
    return AgentLoop(
        [
            LoadMemoryStage(),
            PlanSubGoalsStage(),
            RunModelStage(responses_client, tool_definitions),
            ExecuteToolsStage(registry),
            MemoryAssemblyStage(),
            EndConditionStage(),
        ]
    )


async def _run_once(
    prompt: str,
    config: AgentConfig,
    debug_hub: DebugHub,
    loop: AgentLoop,
) -> None:
    context = AgentContext(
        user_input=prompt,
        config=config,
        debug_hub=debug_hub,
    )
    status(f"Running SariAgent with {config.model}")
    await loop.run(context)
    if context.response_text:
        console.print(context.response_text)


async def _serve_forever(
    config: AgentConfig,
    debug_hub: DebugHub,
    controller: ServeController,
    loop: AgentLoop,
) -> None:
    while True:
        controller.state = "idle"
        await controller.publish_status()
        status("Waiting for a prompt from the debug website...")
        prompt = await controller.next_prompt()

        debug_hub.begin_run()
        controller.state = "running"
        await debug_hub.publish(
            "server.run.accepted",
            summary="Prompt accepted",
            payload={"prompt": prompt, "run_id": debug_hub.run_id},
        )
        await controller.publish_status()
        try:
            await _run_once(prompt, config, debug_hub, loop)
        except Exception as error:  # noqa: BLE001 - run.error is already published
            status(f"Run failed: {error!r}")


async def async_main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run the SariAgent Python harness.")
    parser.add_argument("prompt", nargs="*", help="User task for the agent.")
    parser.add_argument("--model", default=None)
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--base-url", default=None)
    parser.add_argument("--api-style", choices=["responses", "chat_completions"], default=None)
    parser.add_argument("--host", default=None)
    parser.add_argument("--port", type=int, default=None)
    parser.add_argument("--debug", action="store_true", help="Enable the local debug websocket.")
    parser.add_argument("--debug-host", default=None)
    parser.add_argument("--debug-port", type=int, default=None)
    parser.add_argument(
        "--serve",
        action="store_true",
        help="Idle until the debug website submits a prompt; implies --debug.",
    )
    args = parser.parse_args(argv)
    if not args.prompt and not args.serve:
        parser.error("prompt is required unless --serve is given")

    config = load_config()
    if args.model:
        config.model = args.model
    if args.api_key:
        config.openai_api_key = args.api_key
    if args.base_url:
        config.openai_base_url = args.base_url
    if args.api_style:
        config.openai_api_style = args.api_style
    if args.host:
        config.unity_host = args.host
    if args.port:
        config.unity_port = args.port
    if args.debug or args.serve:
        config.debug_enabled = True
    if args.debug_host:
        config.debug_host = args.debug_host
    if args.debug_port:
        config.debug_port = args.debug_port

    debug_hub = DebugHub(
        enabled=config.debug_enabled,
        replay_limit=config.debug_replay_events,
        jsonl_dir=config.debug_runs_dir,
        include_raw_llm_events=config.debug_include_raw_llm_events,
        include_prompts=config.debug_include_prompts,
        image_max_edge=config.debug_image_max_edge,
    )
    controller = ServeController(debug_hub, mode="serve" if args.serve else "cli")
    debug_server = DebugWebSocketServer(
        debug_hub,
        host=config.debug_host,
        port=config.debug_port,
        on_client_message=controller.handle_client_message,
        status_provider=controller.status_payload,
    )
    await debug_server.start()
    if config.debug_enabled:
        status(f"Debug websocket listening on {debug_server.url}")
        if debug_hub.jsonl_path is not None:
            status(f"Debug JSONL recording: {debug_hub.jsonl_path}")

    loop = _build_loop(config, debug_hub)

    try:
        if args.serve:
            await _serve_forever(config, debug_hub, controller, loop)
        else:
            controller.state = "running"
            await controller.publish_status()
            await _run_once(" ".join(args.prompt), config, debug_hub, loop)
        return 0
    finally:
        await debug_server.stop()


def main(argv: list[str] | None = None) -> int:
    try:
        return asyncio.run(async_main(argv))
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
