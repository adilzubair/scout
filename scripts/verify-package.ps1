param([string]$PublishedDirectory, [string]$Archive, [string]$Inventory)
$ErrorActionPreference = 'Stop'

function Test-ScoutPackage {
    param([Parameter(Mandatory)][string]$Directory, [string]$Zip)
    $packageRoot = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    if (-not [IO.Directory]::Exists($packageRoot)) { throw 'Package directory missing.' }
    $sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $runtime = @(Get-Content -LiteralPath (Join-Path $sourceRoot 'packaging/runtime-files.txt') | Where-Object { $_.Trim() })
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $runtime) {
        if ($name -notmatch '^runtime/[a-zA-Z0-9_./-]+$' -or $name.Contains('..') -or -not $expected.Add($name)) { throw 'Invalid runtime allowlist.' }
    }
    $scripts = @('scripts/setup-laya.ps1', 'scripts/setup-multimodal.ps1', 'scripts/fetch-embeddinggemma2.py')
    $required = @('Scout.exe', 'Scout.dll', 'Scout.Core.dll', 'Scout.deps.json', 'Scout.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'README.md', 'docs/THIRD_PARTY.md') + $runtime + $scripts
    $files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($packageRoot)
    $total = 0L
    while ($pending.Count) {
        $folder = $pending.Pop()
        if (([IO.File]::GetAttributes($folder) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked package directory.' }
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($folder)) {
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked package entry.' }
            $relative = $path.Substring($packageRoot.Length + 1).Replace('\', '/')
            Test-ScoutPackagePath $relative
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Push($path); continue }
            if ($files.Count -ge 5000) { throw 'Package file count limit.' }
            $size = ([IO.FileInfo]$path).Length; $total += $size
            if ($size -gt 512MB -or $total -gt 1GB) { throw 'Package size limit.' }
            if ($relative.StartsWith('runtime/', [StringComparison]::OrdinalIgnoreCase) -and -not $expected.Contains($relative)) { throw "Unexpected runtime content: $relative" }
            $files.Add($relative, [pscustomobject]@{ file=$relative; bytes=$size; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() })
        }
    }
    foreach ($name in $required) { if (-not $files.ContainsKey($name)) { throw "Required package file missing: $name" } }
    foreach ($name in ($runtime + $scripts)) {
        if ($files[$name].sha256 -ne (Get-FileHash -LiteralPath (Join-Path $sourceRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Package source mismatch: $name" }
    }
    $configuration = Get-Content -LiteralPath (Join-Path $packageRoot 'Scout.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworks = @($configuration.runtimeOptions.includedFrameworks | ForEach-Object name)
    if ('Microsoft.NETCore.App' -notin $frameworks -or 'Microsoft.WindowsDesktop.App' -notin $frameworks) { throw 'Package is not a self-contained desktop build.' }
    if ($Zip) {
        Add-Type -AssemblyName System.IO.Compression
        $stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($Zip))
        try {
            $archiveReader = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
            try {
                $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($entry in $archiveReader.Entries) {
                    $name = $entry.FullName; Test-ScoutPackagePath $name
                    if ($name.EndsWith('/')) { continue }
                    if (-not $seen.Add($name)) { throw 'Duplicate ZIP entry.' }
                    if (-not $files.ContainsKey($name) -or $entry.Length -ne $files[$name].bytes) { throw 'ZIP inventory mismatch.' }
                    $data = $entry.Open(); $hash = [Security.Cryptography.SHA256]::Create()
                    try { $digest = [BitConverter]::ToString($hash.ComputeHash($data)).Replace('-', '').ToLowerInvariant() }
                    finally { $hash.Dispose(); $data.Dispose() }
                    if ($digest -ne $files[$name].sha256) { throw 'ZIP content mismatch.' }
                }
                if ($seen.Count -ne $files.Count) { throw 'ZIP omits published files.' }
            } finally { $archiveReader.Dispose() }
        } finally { $stream.Dispose() }
    }
    return [pscustomobject]@{ schemaVersion=1; scope='Base payload inventory and integrity only; no release approval'; files=@($files.Values | Sort-Object file); bytes=$total; zipVerified=[bool]$Zip; runtimeFiles=$runtime.Count; optionalDistributionApproved=$false }
}
function Test-ScoutPackagePath {
    param([string]$Name)
    if (-not $Name -or $Name.Contains('\') -or $Name.StartsWith('/') -or $Name.Contains(':')) { throw 'Unsafe package path.' }
    $parts = $Name.TrimEnd('/').Split('/')
    foreach ($part in $parts) {
        if (-not $part -or $part -in @('.', '..') -or $part.TrimEnd(' ', '.') -ne $part -or
            $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { throw 'Unsafe package path.' }
        if ($part -in @('.runtime','.venv','venv','models','addons','diagnostics','evaluation','tests','__pycache__','site-packages','.git') -or $part -like '.env*') { throw 'Private package directory or file.' }
    }
    $leaf = $parts[-1]; $extension = [IO.Path]::GetExtension($leaf)
    if ($extension -in @('.safetensors','.gguf','.bin','.pt','.pth','.onnx','.sqlite','.sqlite3','.db','.whl','.tar','.gz','.zip','.pyd','.wav','.mp3','.mp4','.mkv','.webm','.ogg','.flac') -or
        $leaf -in @('python.exe','pythonw.exe','uv.exe','uvx.exe','ffmpeg.exe','llama-server.exe','installed.json','token.txt') -or
        $leaf -match '^(torch|c10|cudart|cublas|avcodec|avformat|avutil|libx264|libx265).*\.dll$') { throw 'Optional runtime or private data in base package.' }
}
if ($MyInvocation.InvocationName -ne '.') {
    if (-not $PublishedDirectory) { throw 'PublishedDirectory is required.' }
    $result = Test-ScoutPackage -Directory $PublishedDirectory -Zip $Archive
    if ($Inventory) { $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Inventory -Encoding UTF8 }
    Write-Output "Base package integrity verified: $($result.files.Count) files, $($result.runtimeFiles) allowlisted runtime files."
}
