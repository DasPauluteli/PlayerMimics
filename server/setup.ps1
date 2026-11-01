<#
PlayerMimics AI server - one-time setup on Windows (host only; nobody else needs this).

Makes a Python environment next to this script (.venv), installs PyTorch (CUDA when there's an NVIDIA GPU, else the
CPU build), the speech and voice libraries, downloads the voice and speech models (~4 GB) and pulls the LLM into
Ollama (~17 GB for the default).

Run: double-click setup.bat, or from a terminal:
    powershell -ExecutionPolicy Bypass -File setup.ps1

Options:
    -Cuda cu128    PyTorch CUDA build (cu128: RTX 20xx-50xx; cu126 for older drivers). -Cpu forces the CPU build.
    -LlmModel      Ollama model to pull (default: the server's default). Smaller GPUs: e.g. gemma3:12b, and start
                   the server with  start.bat --llm-model gemma3:12b
    -SkipModels / -SkipOllama  Skip those steps.
#>
param(
    [string]$Cuda = "cu128",
    [switch]$Cpu,
    [string]$LlmModel = "hf.co/bartowski/TheDrummer_Orion-26B-A4B-v1.1-GGUF:Q4_K_M",
    [switch]$SkipModels,
    [switch]$SkipOllama,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$venv = Join-Path $here ".venv"
$py = Join-Path $venv "Scripts\python.exe"

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Done { if (-not $NoPause) { Read-Host "Press Enter to close" | Out-Null } }
function Fail($msg) { Write-Host "`nERROR: $msg" -ForegroundColor Red; Done; exit 1 }
function Ask($msg) { return $NoPause -or (Read-Host "$msg (y/N)") -eq "y" }

Step "Looking for an NVIDIA GPU"
$gpu = $null
try { $gpu = & nvidia-smi --query-gpu=name,memory.total --format=csv,noheader 2>$null } catch {}
if ($gpu -and -not $Cpu) {
    Write-Host "Found: $gpu"
    $index = "https://download.pytorch.org/whl/$Cuda"
} else {
    if (-not $Cpu) {
        Write-Host "No NVIDIA GPU found. The voice and speech models will run on the CPU (slower: lines take longer to start)." -ForegroundColor Yellow
        Write-Host "Ollama still uses an AMD or Intel GPU for the LLM if it supports it."
    }
    $index = "https://download.pytorch.org/whl/cpu"
}

Step "Checking uv (installs Python and the packages)"
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Fail "Install uv from https://docs.astral.sh/uv/ and run this again." }
    if (-not (Ask "uv isn't installed. Install it now with winget?")) { Fail "uv is needed. Install it from https://docs.astral.sh/uv/ and run this again." }
    & winget install --id astral-sh.uv -e --accept-source-agreements --accept-package-agreements
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "User") + ";" + [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:Path"
    if (-not (Get-Command uv -ErrorAction SilentlyContinue)) { Fail "uv is installed but not on PATH yet. Open a new terminal and run this again." }
}

Step "Making the Python 3.11 environment in $venv"
& uv venv $venv --python 3.11 --allow-existing
if ($LASTEXITCODE -ne 0) { Fail "Could not make the environment." }

Step "Installing PyTorch from $index (large download)"
& uv pip install --python $py --index-url $index torch
if ($LASTEXITCODE -ne 0) { Fail "PyTorch install failed." }

Step "Installing the speech and voice libraries"
& uv pip install --python $py -r (Join-Path $here "requirements.txt")
if ($LASTEXITCODE -ne 0) { Fail "Package install failed." }
& $py -c "import torch; print('PyTorch', torch.__version__, '| GPU:', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'none (CPU)')"

if (-not $SkipModels) {
    Step "Downloading the voice model (Edge0/Audio8-TTS-Realistic-0.6b) and Whisper (openai/whisper-large-v3-turbo)"
    $env:HF_HUB_DISABLE_IMPLICIT_TOKEN = "1"
    $env:HF_HUB_DISABLE_SYMLINKS_WARNING = "1"
    $models = Join-Path $here "models"
    # Whisper's PyTorch copy is only used on the GPU; on the CPU the server fetches a smaller int8 copy itself.
    $whisper = if ($index -like "*cpu") { "" } else { "openai/whisper-large-v3-turbo" }
    & $py -c "import sys; from huggingface_hub import snapshot_download as d; d('Edge0/Audio8-TTS-Realistic-0.6b', local_dir=sys.argv[1] + '/realistic-0.6b'); sys.argv[2:] and sys.argv[2] and d(sys.argv[2], local_dir=sys.argv[1] + '/whisper-large-v3-turbo')" $models $whisper
    if ($LASTEXITCODE -ne 0) { Fail "Model download failed." }
}

if (-not $SkipOllama) {
    Step "Ollama (runs the LLM)"
    $ollama = Get-Command ollama -ErrorAction SilentlyContinue
    if (-not $ollama -and (Get-Command winget -ErrorAction SilentlyContinue) -and (Ask "Ollama isn't installed. Install it now with winget?")) {
        & winget install --id Ollama.Ollama -e --accept-source-agreements --accept-package-agreements
        $env:Path = [Environment]::GetEnvironmentVariable("Path", "User") + ";" + [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:Path"
        $ollama = Get-Command ollama -ErrorAction SilentlyContinue
    }
    if ($ollama) {
        Write-Host "Pulling $LlmModel"
        & ollama pull $LlmModel
        if ($LASTEXITCODE -ne 0) { Write-Host "ollama pull failed. Is Ollama running? Start it and run:  ollama pull $LlmModel" -ForegroundColor Yellow }
    } else {
        Write-Host "Install Ollama from https://ollama.com and run:  ollama pull $LlmModel" -ForegroundColor Yellow
    }
}

Step "Done"
Write-Host "Before hosting, double-click start.bat and keep its window open while you play."
Done
