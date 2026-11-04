# PlayerMimics AI server

The host-only part of [PlayerMimics](https://thunderstore.io/c/lethal-company/p/Pauluteli/PlayerMimics/). It runs on
the host's PC next to the game and gives the Masked their brain and their voices:

- **Speech recognition:** Whisper large-v3-turbo transcribes what players say (only players who allowed it in the
  consent popup).
- **Voice cloning:** Audio8-TTS-Realistic-0.6B speaks in a player's voice, streamed so lines start quickly. The voice
  gets better as more clean speech comes in.
- **Brain:** an LLM on a local [Ollama](https://ollama.com) decides what each Masked does and says. The default is
  Orion 26B-A4B (an uncensored Gemma 4 finetune that talks like friends on voice chat).

Everything runs locally. Nothing is sent anywhere, and nothing is saved unless you ask for it (`--cache-dir`, or
`Brain > CollectTrainingData` in the mod).
Only the host needs this; the other players only need the mod.

## What you need

- Windows 10/11 or Linux, about 25 GB of disk space.
- A GPU. The default setup uses about 20 GB of VRAM (LLM ~16 GB, voice and speech ~4 GB). With less VRAM, use a
  smaller LLM (see below). NVIDIA works out of the box. AMD works on Linux with a ROCm build of PyTorch, and Ollama
  uses AMD GPUs on both systems. Without a usable GPU the voice and speech models run on the CPU: it works, but lines
  take longer to start.

## Setup (once)

**Windows:** double-click `setup.bat`. It installs [uv](https://docs.astral.sh/uv/) (Python) and Ollama with winget
if they're missing (it asks first), PyTorch (CUDA with an NVIDIA GPU, else the CPU build), the models (~4 GB) and the
LLM (~17 GB).

**Linux:** install [uv](https://docs.astral.sh/uv/) and [Ollama](https://ollama.com), then run `./setup.sh`.
On an AMD GPU pass the ROCm build of PyTorch that fits your card, for example
`./setup.sh --torch-index https://download.pytorch.org/whl/rocm6.4` (see
[pytorch.org](https://pytorch.org/get-started/locally/)).

## Every time you host

Start `start.bat` (Windows) or `./start.sh` (Linux) before hosting and keep it open while you play. The mod finds it at
`http://127.0.0.1:5279`; the first start takes a minute while the models load.

Useful options (add them after `start.bat` / `./start.sh`):

| Option | What it does |
| --- | --- |
| `--llm-model gemma3:12b` | Another Ollama model, for smaller GPUs or to try others. A Hugging Face GGUF works too: `hf.co/<user>/<repo>:Q4_K_M`. A missing model is downloaded on first start. |
| `--cache-dir voices` | Keep voices, speaking style and nicknames between games (saved in that folder). |
| `--language German` | The language players speak (default: English; `Auto` detects it for every clip, which is slower and mishears short clips). |
| `--training-dir data` | Where decisions are saved as text when the mod's `Brain > CollectTrainingData` is on (default: `%LOCALAPPDATA%/LethalMimicsAI/training` on Windows, `~/.local/share/LethalMimicsAI/training` on Linux). |
| `--llm-ctx 8192` | The LLM context in tokens, if you raised `OLLAMA_CONTEXT_LENGTH`. Longer prompts get trimmed to fit (default: 4096). |
| `--tts onnx` | The small 0.1B voice model on the CPU, for when the GPU is busy or too small. Lines start a bit later (about 2 s instead of 0.7 s). It needs [Edge0/audio8-TTS-0.1B-ONNX-INT8](https://huggingface.co/Edge0/audio8-TTS-0.1B-ONNX-INT8) in `onnx/model` and the `onnx_runtime_0_1b_int8` folder of [Audio8_TTS](https://github.com/Edge0-AI/Audio8_TTS) in `onnx/repo`. |
| `--llm-api openai --llm-base http://127.0.0.1:1234/v1` | Any OpenAI-compatible server (LM Studio, llama.cpp) instead of Ollama. |

Run `python server.py --help` in the `.venv` for all of them. The log is in `server.log`.

## Files

- `server.py`: the HTTP server, voice bank and LLM brain.
- `torch_tts.py`, `onnx_tts.py`: the voice models. `gpu_whisper.py`: Whisper on the GPU.
- `setup.ps1` / `setup.bat`, `start.ps1` / `start.bat`: Windows. `setup.sh`, `start.sh`: Linux.

## Credits

Based on the AI server of brant's [LethalMimics](https://github.com/BKPatt/LethalMimics) add-on, and it still answers
that add-on's endpoints. Models: [Audio8 TTS](https://github.com/Edge0-AI/Audio8_TTS) (Apache 2.0),
[Whisper](https://github.com/openai/whisper) (MIT), Orion by TheDrummer.
