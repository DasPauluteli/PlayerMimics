#!/usr/bin/env bash
# PlayerMimics AI server (Linux). Run it before hosting and keep it open while you play; the mod finds it at
# http://127.0.0.1:5279. Extra arguments go to server.py, e.g.  ./start.sh --llm-model gemma3:12b --cache-dir voices
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"

PY=.venv/bin/python
[ -x "$PY" ] || { echo "The Python environment is missing. Run ./setup.sh first."; exit 1; }
command -v ollama >/dev/null || { echo "Ollama is not installed: https://ollama.com"; exit 1; }

# Start Ollama if it isn't running yet, and stop it again when this script exits. Vulkan lets it use AMD and
# integrated GPUs (it skips integrated ones unless OLLAMA_IGPU_ENABLE is set). The prompts need a 4k context.
if ! curl -sf http://127.0.0.1:11434/api/tags >/dev/null; then
    OLLAMA_VULKAN=1 OLLAMA_IGPU_ENABLE=1 OLLAMA_CONTEXT_LENGTH=4096 ollama serve >/dev/null 2>&1 &
    OLLAMA_PID=$!
    trap 'kill $OLLAMA_PID 2>/dev/null' EXIT
    for _ in $(seq 20); do curl -sf http://127.0.0.1:11434/api/tags >/dev/null && break; sleep 0.5; done
fi

"$PY" server.py --log-file "$PWD/server.log" "$@"
