param([Parameter(Mandatory=$true)][string]$RunFolder)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$scoutBundleRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$scoutRunRoot = [IO.Path]::GetFullPath((Join-Path $scoutBundleRoot $RunFolder))
$scoutRunsRoot = [IO.Path]::GetFullPath((Join-Path $scoutBundleRoot 'r'))
if ([IO.Path]::GetDirectoryName($scoutRunRoot) -ne $scoutRunsRoot -or [IO.Path]::GetFileName($scoutRunRoot) -notmatch '^[0-9a-f]{8}$') { throw 'Choose one owned r/<run> folder.' }
function Assert-ScoutPlainPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    if (-not $cursor.Equals($scoutRunRoot,[StringComparison]::OrdinalIgnoreCase) -and -not $cursor.StartsWith($scoutRunRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Report path escaped its run.' }
    while ($cursor) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked report input.' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
Assert-ScoutPlainPath $scoutRunRoot
$scoutReportFiles = [Collections.Generic.List[string]]::new()
function Add-ScoutReportFiles([string]$Directory) {
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Force) {
        if ($file.Extension -notin @('.json','.txt','.log')) { continue }
        Assert-ScoutPlainPath $file.FullName
        if ($file.Length -gt 8MB) { throw 'Report file exceeds the diagnostic size bound.' }
        $scoutReportFiles.Add($file.FullName)
    }
}
Add-ScoutReportFiles $scoutRunRoot
foreach ($work in Get-ChildItem -LiteralPath $scoutRunRoot -Directory -Force) {
    if ($work.Name -notmatch '^h-[0-9a-f]{8}$') { continue }
    Assert-ScoutPlainPath $work.FullName
    Add-ScoutReportFiles $work.FullName
    $scale = Join-Path $work.FullName 'scale'
    if (Test-Path -LiteralPath $scale) {
        Assert-ScoutPlainPath $scale
        foreach ($trial in Get-ChildItem -LiteralPath $scale -Directory -Force) {
            if ($trial.Name -notmatch '^scale-[0-9a-f]{32}$') { continue }
            Assert-ScoutPlainPath $trial.FullName
            Add-ScoutReportFiles $trial.FullName
        }
    }
}
$scoutReportZip = Join-Path $scoutRunRoot ('diagnostics-' + [guid]::NewGuid().ToString('N').Substring(0,8) + '.zip')
$scoutZip = [IO.Compression.ZipFile]::Open($scoutReportZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $scoutReportFiles) {
        $relative = $file.Substring($scoutRunRoot.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($scoutZip, $file, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $scoutZip.Dispose() }
Write-Output "Share this diagnostics ZIP after reviewing its logs: $scoutReportZip"
