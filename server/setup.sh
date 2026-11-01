#!/usr/bin/env bash
# PlayerMimics AI server - one-time setup on Linux (host only; nobody else needs this).
# Makes .venv next to this script with PyTorch, the speech and voice libraries, downloads the voice and speech models
# (~4 GB) and pulls the LLM into Ollama (~17 GB for the default).
#
#   ./setup.sh               NVIDIA GPU (CUDA 12.8 build of PyTorch), or the CPU build when there's no nvidia-smi
#   ./setup.sh --cpu         CPU build of PyTorch
#   ./setup.sh --torch-index https://download.pytorch.org/whl/rocm6.4    AMD GPU: the ROCm build that fits your
#                            card and driver (see https://pytorch.org/get-started/locally/)
#   LLM_MODEL=gemma3:12b ./setup.sh    another Ollama model (start it with ./start.sh --llm-model gemma3:12b)
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"

INDEX=""
while [ $# -gt 0 ]; do
    case "$1" in
        --cpu) INDEX=https://download.pytorch.org/whl/cpu ;;
        --torch-index) INDEX="$2"; shift ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
    shift
done
if [ -z "$INDEX" ]; then
    if command -v nvidia-smi >/dev/null && nvidia-smi -L >/dev/null 2>&1; then
        INDEX=https://download.pytorch.org/whl/cu128
    else
        echo "No NVIDIA GPU found: using the CPU build (AMD: pass --torch-index with a ROCm build)."
        INDEX=https://download.pytorch.org/whl/cpu
    fi
fi
LLM_MODEL="${LLM_MODEL:-hf.co/bartowski/TheDrummer_Orion-26B-A4B-v1.1-GGUF:Q4_K_M}"

command -v uv >/dev/null || { echo "uv is needed: https://docs.astral.sh/uv/ (curl -LsSf https://astral.sh/uv/install.sh | sh)"; exit 1; }
echo "==> Python 3.11 environment in $PWD/.venv"
uv venv .venv --python 3.11 --allow-existing
echo "==> PyTorch from $INDEX"
uv pip install --python .venv/bin/python --index-url "$INDEX" torch
echo "==> Speech and voice libraries"
uv pip install --python .venv/bin/python -r requirements.txt
.venv/bin/python -c "import torch; print('PyTorch', torch.__version__, '| GPU:', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'none (CPU)')"

echo "==> Voice model (Edge0/Audio8-TTS-Realistic-0.6b) and Whisper (openai/whisper-large-v3-turbo, GPU only)"
WHISPER=openai/whisper-large-v3-turbo
case "$INDEX" in *cpu) WHISPER="" ;; esac
HF_HUB_DISABLE_IMPLICIT_TOKEN=1 .venv/bin/python -c "import sys; from huggingface_hub import snapshot_download as d; d('Edge0/Audio8-TTS-Realistic-0.6b', local_dir='models/realistic-0.6b'); sys.argv[1:] and sys.argv[1] and d(sys.argv[1], local_dir='models/whisper-large-v3-turbo')" "$WHISPER"

echo "==> LLM: $LLM_MODEL"
if command -v ollama >/dev/null; then
    ollama pull "$LLM_MODEL" || echo "ollama pull failed. Is Ollama running? (ollama serve), then: ollama pull $LLM_MODEL"
else
    echo "Install Ollama (https://ollama.com or your distro's package), then: ollama pull $LLM_MODEL"
fi
echo "Done. Before hosting run ./start.sh and keep it open while you play."
