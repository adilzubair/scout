param([switch]$Physical16Gb)
$ErrorActionPreference = 'Stop'
$scoutBundle = [IO.Path]::GetFullPath($PSScriptRoot)
function Get-ScoutOwnedPath([string]$Relative) {
    $resolved = [IO.Path]::GetFullPath((Join-Path $scoutBundle $Relative))
    if (-not $resolved.StartsWith($scoutBundle + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Bundle path escaped its root.' }
    $cursor = $resolved
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Extract the bundle into a plain local folder without links.' }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $resolved
}
$scoutInventory = Get-Content -LiteralPath (Get-ScoutOwnedPath 'inventory.json') -Raw | ConvertFrom-Json
foreach ($entry in $scoutInventory.PSObject.Properties) {
    $path = Get-ScoutOwnedPath $entry.Name
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw "Bundle integrity mismatch: $($entry.Name)" }
}
if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt 22000) { throw 'Windows 11 x64 is required.' }
$scoutResults = Get-ScoutOwnedPath ('r/' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $scoutResults -Force | Out-Null
@{ physical16GbPcDeclared=[bool]$Physical16Gb; capturedUtc=[DateTimeOffset]::UtcNow.ToString('O'); scope='Human declaration; RAM/CPU are separately measured. No release approval.' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scoutResults 'declaration.json')
Write-Output "Results: $scoutResults"
Write-Output 'CPU mode, Laya off. Downloads the pinned optional runtime/model into this run only. Keep the PC plugged in.'
$scoutPreviousErrorPreference = $ErrorActionPreference
try {
    # Windows PowerShell wraps native stderr as ErrorRecord. Retain those logs
    # and the native exit code instead of losing the final results location.
    $ErrorActionPreference = 'Continue'
    & (Get-ScoutOwnedPath 'package/Scout.Tests.exe') --hardware-run $scoutResults (Get-ScoutOwnedPath 'fixtures') 2>&1 | Tee-Object -FilePath (Join-Path $scoutResults 'console.log')
    $scoutValidationExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $scoutPreviousErrorPreference }
Write-Output "Keep this entire results folder, including failed attempts: $scoutResults"
& (Get-ScoutOwnedPath 'collect-results.ps1') -RunFolder $scoutResults.Substring($scoutBundle.Length + 1)
exit $scoutValidationExit
