<#
PlayerMimics AI server (Windows). Start it before hosting (start.bat) and keep the window open while you play.
The mod finds it at http://127.0.0.1:5279. It starts Ollama itself when it isn't running.

Extra arguments go to server.py, e.g.
    start.bat --llm-model gemma3:12b          another Ollama model
    start.bat --cache-dir voices              keep voices and speech style between games
    start.bat --language German               the language players speak (default: detected per clip)
#>
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$py = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
if (-not (Test-Path $py)) {
    Write-Host "The Python environment is missing. Run setup.bat first." -ForegroundColor Red
    Read-Host "Press Enter to close" | Out-Null
    exit 1
}
$env:PYTHONUTF8 = "1"
& $py server.py --log-file (Join-Path $PSScriptRoot "server.log") @args
if ($LASTEXITCODE -ne 0) { Read-Host "The server stopped with an error (see above). Press Enter to close" | Out-Null }
