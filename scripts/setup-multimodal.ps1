param([Parameter(Mandatory)][string]$AddonRoot, [ValidateSet('cpu','cuda')][string]$Device = 'cpu')
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
$scoutRoot = Split-Path -Parent $PSScriptRoot
$spec = Get-Content -LiteralPath (Join-Path $scoutRoot 'runtime/multimodal_setup.json') -Raw | ConvertFrom-Json
$addonPath = [System.IO.Path]::GetFullPath($AddonRoot)
New-Item -ItemType Directory -Path $addonPath -Force | Out-Null
$bootstrapLock = [System.IO.File]::Open((Join-Path $addonPath 'bootstrap.lock'), [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
try {
    $bootstrapLock.Lock(0, 1)
    $toolRoot = Join-Path $addonPath "bootstrap/uv-$($spec.uv.version)"
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    $archive = Join-Path $toolRoot 'uv.zip'
    if (-not (Test-Path -LiteralPath $archive)) {
        Write-Output 'Downloading the pinned setup tool...'
        $partial = Join-Path $toolRoot 'uv.partial'
        Invoke-WebRequest -Uri $spec.uv.url -OutFile $partial -UseBasicParsing
        if ((Get-Item -LiteralPath $partial).Length -ne $spec.uv.bytes -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $spec.uv.sha256) { throw 'Setup tool checksum mismatch.' }
        Move-Item -LiteralPath $partial -Destination $archive -Force
    }
    if ((Get-Item -LiteralPath $archive).Length -ne $spec.uv.bytes -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $spec.uv.sha256) { throw 'Setup tool checksum mismatch.' }
    # Always restore the tool from its verified archive; an old executable is not trusted.
    Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot -Force
    $uv = Join-Path $toolRoot 'uv.exe'
    $env:UV_PYTHON_INSTALL_DIR = Join-Path $addonPath 'python'
    $env:UV_CACHE_DIR = Join-Path $addonPath 'cache'
    Write-Output 'Preparing the dedicated Python runtime...'
    & $uv --no-config python install --no-bin --no-registry $spec.pythonVersion
    if ($LASTEXITCODE -ne 0) { throw 'Python setup failed. Completed downloads are kept for retry.' }
    $python = & $uv --no-config python find --managed-python --system $spec.pythonVersion
    if ($LASTEXITCODE -ne 0) { throw 'Dedicated Python runtime was not found.' }
    $privatePythonRoot = [System.IO.Path]::GetFullPath($env:UV_PYTHON_INSTALL_DIR).TrimEnd('\') + '\'
    if (-not [System.IO.Path]::GetFullPath($python).StartsWith($privatePythonRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Setup selected a Python interpreter outside the private add-on.' }
    & $python -I -B -X utf8 (Join-Path $scoutRoot 'runtime/semantic_search/setup.py') --root $addonPath --uv $uv --device $Device
    if ($LASTEXITCODE -ne 0) { throw 'Multimodal setup failed. Retry to reuse completed downloads.' }
} finally { $bootstrapLock.Dispose() }
