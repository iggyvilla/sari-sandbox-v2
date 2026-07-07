# SariAgent

Python harness that drives an LLM agent against the Sari Sandbox Unity scene over a websocket (`ws://<unity_host>:<unity_port>/commands`), with an optional debug websocket + web UI for inspecting runs.

## Quick start

```bash
./debug.sh --demo     # fake backend + debug UI, no OpenAI/Unity needed
./debug.sh            # real agent in serve mode + debug UI
./run.sh "look around the store"   # one CLI run, no UI
```

Configuration is read from environment variables and `SariAgent/.env` (see `.env.example`).

## Runnable entry points

### `sari-agent` / `python -m sari_agent.main` ([sari_agent/main.py](sari_agent/main.py))

The agent CLI. Installed as the `sari-agent` console script (`pip install -e .`), or run it without installing via `.venv/bin/python -m sari_agent.main` or `./run.sh`.

```bash
sari-agent [flags] "prompt for the agent"   # one run, then exit
sari-agent --serve                          # no prompt: idle until the debug UI submits one
```

| Flag | Description |
|---|---|
| `prompt` (positional) | User task. Required unless `--serve`. The agent asks the configured model to convert the prompt into concise ordered sub-goals before execution. |
| `--serve` | Serve mode: implies `--debug`, waits for `client.run.start` messages from the debug UI, runs one agent loop per prompt (fresh `run_id` each), returns to idle after each run. Ctrl-C to stop. |
| `--model` | Override `SARI_MODEL` (default `gpt-4.1`). |
| `--api-key` | Override `SARI_OPENAI_API_KEY` / `OPENAI_API_KEY`. |
| `--base-url` | Override `SARI_OPENAI_BASE_URL` / `OPENAI_BASE_URL`. |
| `--api-style` | `responses` or `chat_completions` (default: `responses`, or `chat_completions` when a base URL is set). |
| `--host`, `--port` | Unity websocket host/port (default `localhost:8080`). |
| `--debug` | Enable the debug websocket for this run (UI attaches view-only). |
| `--debug-host`, `--debug-port` | Debug websocket bind address (default `localhost:8765`). |

### `scripts/demo_debug_run.py`

Fake agent run for developing/demoing the debug UI — real `DebugHub`/`AgentLoop`/websocket server, fake model + tool stages (streamed reasoning/text deltas, tool calls, Unity command events, a screenshot thumbnail). Needs no OpenAI key and no Unity.

```bash
.venv/bin/python scripts/demo_debug_run.py [--serve] [--host H] [--port P] [--prompt "..."]
```

| Flag | Description |
|---|---|
| `--serve` | Wait for prompts from the debug UI (repeatable runs). |
| `--prompt` | Prompt for the canned run when `--serve` is not given (default `"look around the store"`). |
| `--host`, `--port` | Debug websocket bind address (default `localhost:8765`). |

Without `--serve` it plays one canned run immediately and then stays up so a browser can attach and replay it.

## Shell scripts

| Script | What it does |
|---|---|
| `./debug.sh [--demo] [backend flags…]` | Runs the backend **and** the debug UI dev server together; Ctrl-C stops both. `--demo` swaps in `scripts/demo_debug_run.py`; any other args go to the backend (e.g. `./debug.sh --model gpt-4.1`). Installs `debug-ui/node_modules` on first use. |
| `./run.sh "prompt" [flags…]` | Single CLI agent run (`python3 -m sari_agent.main`). |
| `./test.sh [pytest args…]` | Runs the pytest suite. Needs `pip install -e ".[test]"` (pytest + pytest-asyncio). |

## Debug UI (`debug-ui/`)

Chat-style viewer for the debug event stream: prompt box (serve mode), per-stage section headers, streamed reasoning (collapsible) and response text, expandable tool-call rows with raw sent/received payloads and screenshot thumbnails.

```bash
npm install --prefix debug-ui        # first time
npm run dev --prefix debug-ui        # dev server on http://localhost:5173
npm run build --prefix debug-ui      # production build to debug-ui/dist
npm run preview --prefix debug-ui    # serve the production build
```

The UI connects to `ws://<page-hostname>:8765/debug` by default. Override with:

- URL parameter: `http://localhost:5173/?ws=ws://otherhost:9000/debug`
- Env var at build/dev time: `VITE_SARI_DEBUG_WS=ws://otherhost:9000/debug npm run dev --prefix debug-ui`

The prompt box is enabled only when the backend runs with `--serve`; runs started from the CLI stream view-only. Protocol and event schema: [DEBUG_WEBSOCKET.md](DEBUG_WEBSOCKET.md).

## Environment variables

Read from the environment or `SariAgent/.env` (`SARI_ENV_FILE` points elsewhere). Core: `SARI_MODEL`, `SARI_OPENAI_API_KEY`, `SARI_OPENAI_BASE_URL`, `SARI_OPENAI_API_STYLE`, `SARI_UNITY_HOST`, `SARI_UNITY_PORT`, `SARI_MEMORY_DIR`, `SARI_SCREENSHOT_DIR`, `SARI_MAX_LOOP_ITERATIONS`, `SARI_UNITY_MAX_MESSAGE_BYTES`. Debug websocket: `SARI_DEBUG_ENABLED`, `SARI_DEBUG_HOST`, `SARI_DEBUG_PORT`, `SARI_DEBUG_REPLAY_EVENTS`, `SARI_DEBUG_INCLUDE_RAW_LLM_EVENTS`, `SARI_DEBUG_INCLUDE_PROMPTS`, `SARI_DEBUG_IMAGE_MAX_EDGE`, `SARI_DEBUG_RUNS_DIR` — details in [DEBUG_WEBSOCKET.md](DEBUG_WEBSOCKET.md).
