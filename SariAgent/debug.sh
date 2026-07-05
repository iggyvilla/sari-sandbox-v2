#!/usr/bin/env bash
# Run the SariAgent backend (serve mode) and the debug UI dev server together.
#
#   ./debug.sh                 # sari-agent --serve  +  vite dev server
#   ./debug.sh --demo          # fake backend (no OpenAI/Unity needed)  +  vite
#   ./debug.sh --debug-port 9000   # extra args are passed to the backend
#
# Open http://localhost:5173 once both are up. Ctrl-C stops both.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

PYTHON=python3
if [[ -x .venv/bin/python ]]; then
  PYTHON=.venv/bin/python
fi

BACKEND=("$PYTHON" -m sari_agent.main --serve)
if [[ "${1:-}" == "--demo" ]]; then
  shift
  BACKEND=("$PYTHON" scripts/demo_debug_run.py --serve)
fi
BACKEND+=("$@")

if [[ ! -d debug-ui/node_modules ]]; then
  echo "Installing debug-ui dependencies..."
  npm install --prefix debug-ui --no-fund --no-audit
fi

PIDS=()
cleanup() {
  trap - INT TERM EXIT
  kill "${PIDS[@]}" 2>/dev/null || true
  wait 2>/dev/null || true
}
trap cleanup INT TERM EXIT

"${BACKEND[@]}" &
PIDS+=($!)

npm run dev --prefix debug-ui &
PIDS+=($!)

# bash 3.2 (macOS default) has no `wait -n`; wait for the backend and stop
# the UI when it exits (the INT/TERM/EXIT trap handles Ctrl-C for both).
wait "${PIDS[0]}"
