#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ $# -eq 0 ]]; then
  echo "Usage: ./run.sh \"prompt for SariAgent\""
  exit 1
fi

cd "$SCRIPT_DIR"
exec python3 -m sari_agent.main "$@"
