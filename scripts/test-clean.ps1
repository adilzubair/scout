$ErrorActionPreference = 'Stop'
$scoutRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$parent = Join-Path $scoutRoot 'artifacts/diagnostics/cleanup-contracts'
$testRoot = Join-Path $parent ([guid]::NewGuid().ToString('N'))
$checks = 0
function Write-Fixture([string]$Name) {
    $path = Join-Path $testRoot $Name
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, 'fixture')
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Expect-Rejection([string]$Message) {
    $caught = $null
    try { & $clean -Apply | Out-Null } catch { $caught = $_.Exception.Message }
    Assert ($caught -like "*$Message*") "Expected '$Message', received '$caught'."
    Assert (Test-Path -LiteralPath (Join-Path $testRoot 'artifacts/Scout-UI-v2/Scout.exe')) 'Preflight deleted an earlier target.'
}
try {
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'scripts') -Force | Out-Null
    $clean = Join-Path $testRoot 'scripts/clean.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'clean.ps1') -Destination $clean
    & git -C $testRoot init -q
    if ($LASTEXITCODE -ne 0) { throw 'Test repository initialization failed.' }
    $obsolete = @('artifacts/Scout-UI-v2/Scout.exe', 'src/Scout.App/obj/cache', 'tests/Scout.Benchmark/obj/cache', 'runtime/__pycache__/old.pyc')
    $research = @(
        '.runtime/models/semantic-qwen3-1.7b/weights.safetensors',
        '.runtime/semantic-search/office/retired.msi',
        'artifacts/diagnostics/old/scout.db', 'artifacts/diagnostics/old/scout.db-wal',
        'artifacts/diagnostics/old/scout.db.search/index-old.bin',
        'artifacts/diagnostics/old/package.zip'
    )
    $retained = @(
        'artifacts/Scout/Scout.exe', 'artifacts/Scout-win-x64.zip', 'artifacts/Scout-Setup.exe',
        'artifacts/Scout-multimodal-preview-e2e-fixed/Scout.exe',
        'artifacts/Scout-multimodal-preview-e2e-fixed.zip',
        'artifacts/Scout-multimodal-preview-e2e-fixed.zip.sha256',
        'artifacts/diagnostics/multimodal/office-fixtures-v2/fixtures.json',
        'artifacts/diagnostics/multimodal/app-e2e-current/report.json',
        'artifacts/evaluation/frozen.json', '.runtime/models/weights.bin',
        '.runtime/semantic-search/.venv/Scripts/python.exe',
        '.runtime/semantic-search/cpu-venv/Scripts/python.exe',
        '.runtime/semantic-search/model/model.safetensors', '.runtime/.venv/Scripts/python.exe',
        'artifacts/diagnostics/multimodal/format-fixtures-v1/bundle.zip',
        'artifacts/diagnostics/ui/coverage-fixtures/semantic.db',
        'artifacts/diagnostics/old/report.json', 'artifacts/diagnostics/old/other.bin',
        'artifacts/current-profile/scout.db', 'website/dist/index.html'
    )
    foreach ($name in ($obsolete + $research + $retained)) { Write-Fixture $name }
    [IO.File]::WriteAllText((Join-Path $testRoot 'src/Scout.App/obj/.git'), '')
    # A source checkout nested in diagnostics is not disposable research output.
    Write-Fixture 'artifacts/diagnostics/checkout/.git'
    Write-Fixture 'artifacts/diagnostics/checkout/archive.zip'
    & $clean | Out-Null
    foreach ($name in $obsolete) { Assert (Test-Path -LiteralPath (Join-Path $testRoot $name)) "Preview removed $name" }
    & $clean -Apply -WhatIf | Out-Null
    foreach ($name in $obsolete) { Assert (Test-Path -LiteralPath (Join-Path $testRoot $name)) "WhatIf removed $name" }
    & $clean -Apply -Confirm:$false | Out-Null
    foreach ($name in $obsolete) { Assert (-not (Test-Path -LiteralPath (Join-Path $testRoot $name))) "Cleanup missed $name" }
    foreach ($name in $research) { Assert (Test-Path -LiteralPath (Join-Path $testRoot $name)) "Default cleanup removed research data: $name" }

    & $clean -RetiredResearch | Out-Null
    foreach ($name in $research) { Assert (Test-Path -LiteralPath (Join-Path $testRoot $name)) "Research preview removed $name" }
    & $clean -RetiredResearch -Apply -WhatIf | Out-Null
    foreach ($name in $research) { Assert (Test-Path -LiteralPath (Join-Path $testRoot $name)) "Research WhatIf removed $name" }
    Write-Fixture 'src/Scout.App/obj/cache'
    $locked = [IO.File]::Open((Join-Path $testRoot 'artifacts/diagnostics/old/scout.db'), 'Open', 'Read', 'None')
    try {
        $caught = $false
        try { & $clean -RetiredResearch -Apply | Out-Null } catch { $caught = $true }
        Assert $caught 'Cleanup accepted an open database.'
        foreach ($name in $research) { Assert (Test-Path -LiteralPath (Join-Path $testRoot $name)) "Locked-file preflight deleted $name" }
    } finally { $locked.Dispose() }
    & $clean -RetiredResearch -Apply -Confirm:$false | Out-Null
    foreach ($name in $research) { Assert (-not (Test-Path -LiteralPath (Join-Path $testRoot $name))) "Research cleanup missed $name" }
    Assert (Test-Path -LiteralPath (Join-Path $testRoot 'src/Scout.App/obj/cache')) 'Research cleanup removed the current build.'
    Assert (Test-Path -LiteralPath (Join-Path $testRoot 'artifacts/diagnostics/checkout/archive.zip')) 'Research cleanup entered a nested checkout.'
    foreach ($name in $retained) { Assert ([IO.File]::ReadAllText((Join-Path $testRoot $name)) -eq 'fixture') "Cleanup changed $name" }

    Write-Fixture 'artifacts/Scout-UI-v2/Scout.exe'
    Write-Fixture 'src/Scout.App/obj/tracked.txt'
    & git -C $testRoot add -f -- src/Scout.App/obj/tracked.txt
    if ($LASTEXITCODE -ne 0) { throw 'Test staging failed.' }
    Expect-Rejection 'Tracked content'
    & git -C $testRoot rm --cached -q -- src/Scout.App/obj/tracked.txt
    if ($LASTEXITCODE -ne 0) { throw 'Test index cleanup failed.' }
    Write-Fixture 'src/Scout.App/obj/.git'
    Expect-Rejection 'Nested repository'
    Write-Output "$checks cleanup contracts passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup escaped owned directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
