# SariAgent Debug WebSocket

SariAgent can run an embedded debug websocket for a browser-based inspector. The backend is implemented in Python only; Unity's `/commands` websocket protocol is unchanged.

Start the agent with:

```bash
sari-agent --debug "look around the store"   # single CLI run (UI attaches view-only)
sari-agent --serve                            # idle until the debug UI submits a prompt
```

By default, the debug UI should connect to:

```text
ws://localhost:8765/debug
```

The bundled web UI lives in `debug-ui/` (`npm install && npm run dev`, then open http://localhost:5173). Without OpenAI/Unity, `python scripts/demo_debug_run.py --serve` emits a realistic fake stream on the same port.

Every event is also written as JSONL under `debug_runs/{run_id}.jsonl` while debug mode is enabled.

## Configuration

```text
SARI_DEBUG_ENABLED=1
SARI_DEBUG_HOST=localhost
SARI_DEBUG_PORT=8765
SARI_DEBUG_REPLAY_EVENTS=1000
SARI_DEBUG_INCLUDE_RAW_LLM_EVENTS=1
SARI_DEBUG_INCLUDE_PROMPTS=1
SARI_DEBUG_IMAGE_MAX_EDGE=512
SARI_DEBUG_RUNS_DIR=debug_runs
```

`--debug` enables the stream for a single CLI run. `--debug-host` and `--debug-port` override the websocket bind address. `--serve` implies `--debug`, skips the positional prompt, and runs agent loops on demand — one per prompt received from the UI, with a fresh `run_id` each time.

## Serve Mode (v1.1 protocol)

With `--serve`, clients may send JSON text frames on the same `/debug` socket:

```json
{ "type": "client.run.start", "prompt": "look around the store" }
```

The server answers through the normal event stream:

- `server.status` — payload `{"state": "idle"|"running", "accepts_prompts": bool, "active_run_id": "..."|null, "mode": "serve"|"cli"}`. Published on every state change, and additionally sent directly (with `seq: 0`) to each client immediately on connect, before replay.
- `server.run.accepted` — payload `{"prompt": "...", "run_id": "..."}`, emitted just before `run.started`.
- `server.run.rejected` — level `warn`, payload `{"reason": "run_active"|"prompts_not_accepted"|"invalid_message", "prompt": "..."}`.

Anything that is not valid JSON, not an object, or not a known `client.*` type is ignored, so v1 read-only clients keep working. CLI runs (`--debug` with a positional prompt) report `mode: "cli"` and reject prompts.

## Event Envelope

All websocket messages are JSON objects with this stable envelope:

```json
{
  "schema_version": 1,
  "seq": 12,
  "run_id": "0f2d...",
  "timestamp": "2026-07-04T02:15:30.123456Z",
  "type": "tool.call.completed",
  "stage": "execute_tools",
  "level": "info",
  "summary": "Completed RequestScreenshot",
  "payload": {}
}
```

The frontend should sort by `seq` within a `run_id`. Late subscribers receive the latest replay buffer before live events.

## Important Event Types

- `run.started`, `run.completed`, `run.error`
- `pipeline.stage.started`, `pipeline.stage.completed`, `pipeline.stage.error`
- `pipeline.subgoals.planned`, `pipeline.subgoal.changed`
- `llm.request.started`, `llm.raw_event`, `llm.text.delta`, `llm.text.completed`, `llm.reasoning.delta`
- `tool.call.started`, `tool.call.completed`, `tool.call.error`
- `unity.command.sent`, `unity.command.received`, `unity.command.error`

`llm.reasoning.delta` is only emitted when the model provider explicitly exposes reasoning or reasoning summaries in streaming events. Hidden model chain-of-thought is not available to this backend.

## Rendering Tool Content

`tool.call.completed.payload.content` is the easiest website-facing shape. Text tools use:

```json
{ "kind": "text", "text": "{\"command\":\"TranslateAgent\"}" }
```

Screenshot tools use:

```json
{
  "kind": "image",
  "mime_type": "image/png",
  "path": "/absolute/path/to/screenshot.png",
  "thumbnail_data_url": "data:image/png;base64,...",
  "bytes": 12345
}
```

Use `thumbnail_data_url` for fast previews and `path` for full-resolution local inspection. The backend scales thumbnails to `SARI_DEBUG_IMAGE_MAX_EDGE`.

## Raw Unity Payloads

`unity.command.sent.payload.raw_json` is the exact JSON string sent to Unity. `unity.command.received.payload.raw_response` is one of:

```json
{ "kind": "text", "raw_text": "{\"current_position\":[0,0,0]}" }
```

```json
{ "kind": "binary", "bytes": 20480, "mime_type": "image/png" }
```

Screenshot bytes are intentionally summarized rather than echoed as raw binary in the websocket event.
