param([switch]$Cuda, [switch]$Fixtures, [switch]$SkipModel, [string]$PythonPath = '')
$ErrorActionPreference = 'Stop'
$scoutRoot = Split-Path -Parent $PSScriptRoot
$probeRoot = Join-Path $scoutRoot '.runtime/semantic-search'
$env:UV_CACHE_DIR = Join-Path $scoutRoot '.runtime/uv-cache'
$env:HF_HOME = Join-Path $probeRoot 'hf-cache'
$uvExe = (Get-Command uv -ErrorAction Stop).Source
if (-not $PythonPath) {
    $PythonPath = Join-Path $scoutRoot '.runtime/.venv/Scripts/python.exe'
    if (-not (Test-Path -LiteralPath $PythonPath)) {
        throw 'Provide -PythonPath for a Python 3.12 interpreter. This development setup requires uv on PATH.'
    }
}
$python = Join-Path $probeRoot '.venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $python)) {
    & $uvExe venv --python $PythonPath (Join-Path $probeRoot '.venv')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated environment creation failed.' }
}
$device = if ($Cuda) { 'cuda' } else { 'cpu' }
$lock = Join-Path $scoutRoot "runtime/semantic_search/requirements.$device.lock.txt"
# Both indexes are official; consider PyPI for dependencies absent from PyTorch.
& $uvExe pip sync --python $python --require-hashes --index-strategy unsafe-best-match $lock
if ($LASTEXITCODE -ne 0) { throw 'Multimodal dependency installation failed.' }
if ($Fixtures) {
    & $uvExe pip install --python $python --require-hashes -r (Join-Path $scoutRoot 'runtime/semantic_search/requirements.fixtures.lock.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture dependency installation failed.' }
}
if (-not $SkipModel) {
    & $python (Join-Path $scoutRoot 'scripts/fetch-embeddinggemma2.py') --output (Join-Path $probeRoot 'model')
    if ($LASTEXITCODE -ne 0) { throw 'Full model download or verification failed. Re-run to resume.' }
}
Write-Output "Multimodal probe runtime ready: $python ($device)"
