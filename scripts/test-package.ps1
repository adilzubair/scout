param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'verify-package.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testParent = [IO.Path]::GetFullPath((Join-Path $sourceRoot 'artifacts/diagnostics/package-contracts'))
$testRoot = Join-Path $testParent ([guid]::NewGuid().ToString('N'))
$payload = Join-Path $testRoot 'payload'
$checks = 0
function Expect-Rejection {
    param([scriptblock]$Action, [string]$Message)
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    if (-not $caught -or $caught -notlike "*$Message*") { throw "Expected '$Message', received '$caught'." }
    $script:checks++
}
function Write-Zip {
    param([string]$Path, [string]$ExtraName, [switch]$Alter, [switch]$Omit)
    $stream = [IO.File]::Create($Path)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
            $name = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
            if ($Omit -and $name -eq 'README.md') { continue }
            $entry = $zip.CreateEntry($name); $output = $entry.Open()
            try {
                $bytes = [IO.File]::ReadAllBytes($file.FullName)
                if ($Alter -and $name -eq 'README.md') { $bytes[0] = $bytes[0] -bxor 1 }
                $output.Write($bytes, 0, $bytes.Length)
            } finally { $output.Dispose() }
        }
        if ($ExtraName) { $entry = $zip.CreateEntry($ExtraName); $output = $entry.Open(); $output.Dispose() }
    } finally { if ($zip) { $zip.Dispose() }; $stream.Dispose() }
}
try {
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    $runtime = @(Get-Content -LiteralPath (Join-Path $sourceRoot 'packaging/runtime-files.txt') | Where-Object { $_.Trim() })
    foreach ($name in ($runtime + @('scripts/setup-laya.ps1','scripts/setup-multimodal.ps1','scripts/fetch-embeddinggemma2.py'))) {
        $target = Join-Path $payload $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $target
    }
    foreach ($name in @('Scout.exe','Scout.dll','Scout.Core.dll','Scout.deps.json','coreclr.dll','hostfxr.dll','hostpolicy.dll','README.md','docs/THIRD_PARTY.md')) {
        $target = Join-Path $payload $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        [IO.File]::WriteAllText($target, 'fixture')
    }
    $config = Join-Path $payload 'Scout.runtimeconfig.json'
    [IO.File]::WriteAllText($config, '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App"},{"name":"Microsoft.WindowsDesktop.App"}]}}')
    $valid = Test-ScoutPackage -Directory $payload
    if ($valid.runtimeFiles -ne 33 -or $valid.optionalDistributionApproved -or $valid.zipVerified) { throw 'Wrong package report.' }
    $checks++
    $archive = Join-Path $testRoot 'valid.zip'
    & (Join-Path $PSScriptRoot 'package-archive.ps1') -Directory $payload -Destination $archive
    $valid = Test-ScoutPackage -Directory $payload -Zip $archive
    if (-not $valid.zipVerified) { throw 'ZIP not verified.' }; $checks++
    foreach ($name in @('runtime/private.env','model.safetensors','.env.local','avcodec-62.dll','notes.sqlite')) {
        $path = Join-Path $payload $name; [IO.File]::WriteAllText($path, 'private')
        try { Expect-Rejection { Test-ScoutPackage -Directory $payload } $(if ($name -eq 'runtime/private.env') { 'Unexpected runtime' } elseif ($name -eq '.env.local') { 'Private package' } else { 'Optional runtime or private data' }) }
        finally { Remove-Item -LiteralPath $path -Force }
    }
    $runtimeFile = Join-Path $payload $runtime[0]; $original = [IO.File]::ReadAllBytes($runtimeFile)
    try { [IO.File]::AppendAllText($runtimeFile, '#changed'); Expect-Rejection { Test-ScoutPackage -Directory $payload } 'source mismatch' }
    finally { [IO.File]::WriteAllBytes($runtimeFile, $original) }
    foreach ($path in @('../escape','a//b','a/./b','/absolute','C:/absolute','a\b','dir/name.','dir/name ','NUL.txt','dir/COM1')) {
        Expect-Rejection { Test-ScoutPackagePath $path } 'Unsafe package path'
    }
    foreach ($name in @('../escape','README.md','readme.MD')) {
        $zipPath = Join-Path $testRoot 'bad.zip'; Write-Zip $zipPath -ExtraName $name
        Expect-Rejection { Test-ScoutPackage -Directory $payload -Zip $zipPath } $(if ($name -eq '../escape') { 'Unsafe package path' } else { 'Duplicate ZIP' })
    }
    $zipPath = Join-Path $testRoot 'bad.zip'; Write-Zip $zipPath -Alter
    Expect-Rejection { Test-ScoutPackage -Directory $payload -Zip $zipPath } 'ZIP content mismatch'
    Write-Zip $zipPath -Omit
    Expect-Rejection { Test-ScoutPackage -Directory $payload -Zip $zipPath } 'ZIP omits'
    [IO.File]::WriteAllText($config, '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App"}}}')
    Expect-Rejection { Test-ScoutPackage -Directory $payload } 'not a self-contained desktop'
    Write-Output "$checks package contracts passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($testParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup escaped owned directory.' }
    if (Test-Path -LiteralPath $resolved) {
        try { Remove-Item -LiteralPath $resolved -Recurse -Force }
        catch { Write-Warning "Fixture cleanup failed: $($_.Exception.Message)" }
    }
}
