param([ValidateSet('english','multilingual','typed-decisions','all')][string]$Model = 'english', [switch]$Cuda, [switch]$SkipModels, [switch]$SemanticHelper)
$ErrorActionPreference = 'Stop'
$scoutRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $scoutRoot '.runtime'
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
$uvCommand = Get-Command uv -ErrorAction SilentlyContinue
$uvExe = if ($uvCommand) { $uvCommand.Source } else { Join-Path $env:USERPROFILE '.local/bin/uv.exe' }
if (-not (Test-Path -LiteralPath $uvExe)) {
    $uvRoot = Join-Path $runtimeRoot 'uv'
    New-Item -ItemType Directory -Path $uvRoot -Force | Out-Null
    $uvArchive = Join-Path $uvRoot 'uv.zip'
    Invoke-WebRequest -Uri 'https://github.com/astral-sh/uv/releases/download/0.8.22/uv-x86_64-pc-windows-msvc.zip' -OutFile $uvArchive
    Expand-Archive -LiteralPath $uvArchive -DestinationPath $uvRoot -Force
    $uvExe = Join-Path $uvRoot 'uv.exe'
    if (-not (Test-Path -LiteralPath $uvExe)) { throw 'Could not install the local Python setup tool.' }
}
$env:UV_PYTHON_INSTALL_DIR = Join-Path $runtimeRoot 'python'
$env:UV_CACHE_DIR = Join-Path $runtimeRoot 'uv-cache'
& $uvExe python install 3.12
if ($LASTEXITCODE -ne 0) { throw 'Python 3.12 installation failed.' }
$venv = Join-Path $runtimeRoot '.venv'
if (-not (Test-Path -LiteralPath (Join-Path $venv 'Scripts/python.exe'))) {
    & $uvExe venv --python 3.12 $venv
    if ($LASTEXITCODE -ne 0) { throw 'Virtual environment creation failed.' }
}
$python = Join-Path $venv 'Scripts/python.exe'
$torchIndex = if ($Cuda) { 'https://download.pytorch.org/whl/cu128' } else { 'https://download.pytorch.org/whl/cpu' }
$torchVersion = if ($Cuda) { 'torch==2.8.0+cu128' } else { 'torch==2.8.0+cpu' }
& $uvExe pip install --python $python --index-url $torchIndex $torchVersion
if ($LASTEXITCODE -ne 0) { throw 'PyTorch installation failed.' }
& $uvExe pip install --python $python -r (Join-Path $scoutRoot 'runtime/requirements.lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'Laya installation failed.' }
& $uvExe pip freeze --python $python | Set-Content -LiteralPath (Join-Path $runtimeRoot 'installed-requirements.txt')
if (-not $SkipModels) {
    & $python (Join-Path $scoutRoot 'runtime/download_models.py') --output (Join-Path $runtimeRoot 'models') --model $Model
    if ($LASTEXITCODE -ne 0) { throw 'Model download failed. Re-run setup to resume.' }
}
Write-Output "Laya Python executable for Scout Settings: $python"
if ($SemanticHelper) {
    & $python (Join-Path $scoutRoot 'runtime/download_semantic.py') --output (Join-Path $runtimeRoot 'models')
    if ($LASTEXITCODE -ne 0) { throw 'Semantic helper setup failed. Re-run setup to resume.' }
}
