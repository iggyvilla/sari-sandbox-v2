"""CLI entrypoint for the SariAgent harness."""

from __future__ import annotations

import argparse
import asyncio

from sari_agent.config import load_config
from sari_agent.context import AgentContext
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
from sari_agent.terminal import console, status
from sari_agent.tools.unity_commands import assemble as assemble_unity_tools
from sari_agent.unity.ws_client import UnityCommandClient


async def async_main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run the SariAgent Python harness.")
    parser.add_argument("prompt", nargs="+", help="User task for the agent.")
    parser.add_argument("--model", default=None)
    parser.add_argument("--host", default=None)
    parser.add_argument("--port", type=int, default=None)
    args = parser.parse_args(argv)

    config = load_config()
    if args.model:
        config.model = args.model
    if args.host:
        config.unity_host = args.host
    if args.port:
        config.unity_port = args.port

    unity_client = UnityCommandClient(
        host=config.unity_host,
        port=config.unity_port,
        screenshot_dir=config.screenshot_dir,
        timeout_seconds=config.request_timeout_seconds,
    )
    tool_definitions, registry = assemble_unity_tools(client=unity_client)
    responses_client = ResponsesClient(model=config.model)

    context = AgentContext(user_input=" ".join(args.prompt), config=config)
    loop = AgentLoop(
        [
            LoadMemoryStage(),
            PlanSubGoalsStage(),
            RunModelStage(responses_client, tool_definitions),
            ExecuteToolsStage(registry),
            MemoryAssemblyStage(),
            EndConditionStage(),
        ]
    )

    status(f"Running SariAgent with {config.model}")
    await loop.run(context)
    if context.response_text:
        console.print(context.response_text)
    return 0


def main(argv: list[str] | None = None) -> int:
    return asyncio.run(async_main(argv))


if __name__ == "__main__":
    raise SystemExit(main())
