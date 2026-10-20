param([switch]$Publish, [switch]$Installer)
$ErrorActionPreference = 'Stop'
$scoutRoot = Split-Path -Parent $PSScriptRoot
if ($Installer) { $Publish = $true }
Push-Location $scoutRoot
try {
    & dotnet build Scout.slnx -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet run --project tests/Scout.Tests/Scout.Tests.csproj --configuration Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & (Join-Path $PSScriptRoot 'test-package.ps1')
    if ($Publish) {
        $artifactsDir = [System.IO.Path]::GetFullPath((Join-Path $scoutRoot 'artifacts'))
        $publishDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsDir 'Scout'))
        if (-not $publishDir.StartsWith($artifactsDir + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Publish directory escaped artifacts.'
        }
        if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
        & dotnet publish src/Scout.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $publishDir
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath README.md -Destination (Join-Path $publishDir 'README.md')
        Copy-Item -LiteralPath LICENSE -Destination (Join-Path $publishDir 'LICENSE')
        $docsDir = Join-Path $publishDir 'docs'
        New-Item -ItemType Directory -Path $docsDir -Force | Out-Null
        foreach ($doc in @('RELEASE_NOTES', 'INSTALLER', 'THIRD_PARTY', 'PARITY', 'UI')) {
            Copy-Item -LiteralPath "docs/$doc.md" -Destination (Join-Path $docsDir "$doc.md")
        }
        $scriptsDir = Join-Path $publishDir 'scripts'
        New-Item -ItemType Directory -Path $scriptsDir -Force | Out-Null
        Copy-Item -LiteralPath scripts/setup-laya.ps1 -Destination (Join-Path $scriptsDir 'setup-laya.ps1')
        & (Join-Path $PSScriptRoot 'verify-package.ps1') -PublishedDirectory $publishDir
        & (Join-Path $PSScriptRoot 'package-archive.ps1') -Directory $publishDir -Destination artifacts/Scout-win-x64.zip
        & (Join-Path $PSScriptRoot 'verify-package.ps1') -PublishedDirectory $publishDir -Archive artifacts/Scout-win-x64.zip -Inventory artifacts/Scout-win-x64.inventory.json
        $checksum = (Get-FileHash artifacts/Scout-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
        "$checksum  Scout-win-x64.zip" | Set-Content -LiteralPath artifacts/Scout-win-x64.zip.sha256
        Write-Output "Packaged artifacts/Scout-win-x64.zip ($checksum)"
        if ($Installer) {
            $compilerPaths = @(@(
                (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
                (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 7/ISCC.exe'),
                (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
            ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) })
            if (-not $compilerPaths) { throw 'Inno Setup 7 compiler (ISCC.exe) is required for -Installer.' }
            $versionProperties = ([xml](Get-Content -LiteralPath src/Scout.App/Scout.App.csproj -Raw)).Project.PropertyGroup
            & $compilerPaths[0] "/DScoutVersion=$($versionProperties.Version)" "/DScoutFileVersion=$($versionProperties.FileVersion)" installer/Scout.iss
            if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
            $setup = Join-Path $artifactsDir 'Scout-Setup.exe'
            if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer output is missing.' }
            $setupChecksum = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
            "$setupChecksum  Scout-Setup.exe" | Set-Content -LiteralPath (Join-Path $artifactsDir 'Scout-Setup.exe.sha256')
            Write-Output "Packaged artifacts/Scout-Setup.exe ($setupChecksum)"
        }
    }
} finally { Pop-Location }
