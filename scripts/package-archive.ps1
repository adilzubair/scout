param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'verify-package.ps1')
$package = Test-ScoutPackage -Directory $Directory
$outputPath = [IO.Path]::GetFullPath($Destination)
$packagePath = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
if ($outputPath.StartsWith($packagePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive must be outside its payload.' }
$temporary = $outputPath + '.' + [guid]::NewGuid().ToString('N') + '.partial'
Add-Type -AssemblyName System.IO.Compression
try {
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $package.files) {
                # Windows PowerShell Compress-Archive emits backslash paths. Use
                # canonical ZIP names so validation rejects ambiguous input paths.
                $entry = $zip.CreateEntry($file.file, [IO.Compression.CompressionLevel]::Optimal)
                $inputFile = [IO.File]::OpenRead((Join-Path $packagePath $file.file))
                try { $entryStream = $entry.Open(); try { $inputFile.CopyTo($entryStream) } finally { $entryStream.Dispose() } }
                finally { $inputFile.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    $verified = Test-ScoutPackage -Directory $packagePath -Zip $temporary
    Move-Item -LiteralPath $temporary -Destination $outputPath -Force
    Write-Output "Archived and verified $($verified.files.Count) base package files."
} finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
