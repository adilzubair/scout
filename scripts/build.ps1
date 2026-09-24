param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$scoutRoot = Split-Path -Parent $PSScriptRoot
Push-Location $scoutRoot
try {
    & dotnet build Scout.slnx -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet tests/Scout.Tests/bin/Release/net10.0/Scout.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($Publish) {
        & dotnet publish src/Scout.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/Scout
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath README.md -Destination artifacts/Scout/README.md
        New-Item -ItemType Directory -Path artifacts/Scout/docs -Force | Out-Null
        Copy-Item -LiteralPath docs/VALIDATION.md -Destination artifacts/Scout/docs/VALIDATION.md
        Copy-Item -LiteralPath docs/THIRD_PARTY.md -Destination artifacts/Scout/docs/THIRD_PARTY.md
        Copy-Item -LiteralPath docs/PARITY.md -Destination artifacts/Scout/docs/PARITY.md
        Copy-Item -LiteralPath docs/UI.md -Destination artifacts/Scout/docs/UI.md
        Copy-Item -LiteralPath docs/SEARCH_EVALUATION.md -Destination artifacts/Scout/docs/SEARCH_EVALUATION.md
        Copy-Item -LiteralPath docs/SEMANTIC_SEARCH.md -Destination artifacts/Scout/docs/SEMANTIC_SEARCH.md
        Copy-Item -LiteralPath docs/LOCAL_SEMANTIC_HELPER.md -Destination artifacts/Scout/docs/LOCAL_SEMANTIC_HELPER.md
        New-Item -ItemType Directory -Path artifacts/Scout/scripts -Force | Out-Null
        Copy-Item -LiteralPath scripts/setup-laya.ps1 -Destination artifacts/Scout/scripts/setup-laya.ps1
        Compress-Archive -Path artifacts/Scout/* -DestinationPath artifacts/Scout-win-x64.zip -Force
        $checksum = (Get-FileHash artifacts/Scout-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
        "$checksum  Scout-win-x64.zip" | Set-Content -LiteralPath artifacts/Scout-win-x64.zip.sha256
        Write-Output "Packaged artifacts/Scout-win-x64.zip ($checksum)"
    }
} finally { Pop-Location }
