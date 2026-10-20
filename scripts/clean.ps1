# Removes generated build output and Python bytecode caches. It never selects
# artifacts/ or .runtime/: those hold release packages, fixtures and AI runtimes.
[CmdletBinding(SupportsShouldProcess)]
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$scoutRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$prefix = $scoutRoot + [IO.Path]::DirectorySeparatorChar
function Test-RepositoryMarker([string]$Directory) {
    $marker = Join-Path $Directory '.git'
    # Empty .git files are uv cache markers, not repository metadata.
    return [IO.Directory]::Exists($marker) -or ([IO.File]::Exists($marker) -and ([IO.FileInfo]$marker).Length -gt 0)
}

$patterns = @(
    'src/*/bin', 'src/*/obj', 'tests/*/bin', 'tests/*/obj',
    'runtime/__pycache__', 'runtime/semantic_search/__pycache__', 'scripts/__pycache__',
    'torchinductor_*'
)
$gitRoot = & git -C $scoutRoot rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($gitRoot).TrimEnd('\', '/') -ne $scoutRoot) {
    throw 'Cleanup must run from its own Git checkout.'
}
$tracked = @(& git -c core.quotepath=false -C $scoutRoot ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect tracked files; cleanup refused.' }
$running = @(Get-Process | ForEach-Object { try { if ($_.Path) { $_.Path } } catch { } })
$candidates = [Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($pattern in $patterns) {
    foreach ($item in @(Get-Item -Path (Join-Path $scoutRoot $pattern) -Force -ErrorAction SilentlyContinue)) {
        [void]$candidates.Add($item.FullName)
    }
}
$plan = @()
foreach ($candidate in $candidates) {
    $path = [IO.Path]::GetFullPath($candidate)
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Cleanup path escaped repository: $path" }
    $relative = $path.Substring($prefix.Length).Replace('\', '/')
    # Revalidate every ancestor, including wildcard-expanded directories.
    $ancestor = $path
    while ($ancestor.Length -ge $scoutRoot.Length) {
        if (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Linked cleanup path refused: $ancestor"
        }
        if ($ancestor -eq $scoutRoot) { break }
        if ([IO.Directory]::Exists($ancestor) -and (Test-RepositoryMarker $ancestor)) {
            throw "Nested repository in cleanup target: $ancestor"
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if (@($tracked | Where-Object { $_ -eq $relative -or $_.StartsWith($relative + '/', [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw "Tracked content in cleanup target: $relative"
    }
    if (@($running | Where-Object { $_ -eq $path -or $_.StartsWith($path + '\', [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw "Running program in cleanup target: $relative"
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($path)
    $bytes = 0L
    while ($pending.Count) {
        $entry = $pending.Pop()
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked cleanup entry refused: $entry" }
        # uv uses an empty .git file to stop Git discovery inside its cache.
        if ([IO.Path]::GetFileName($entry) -eq '.git' -and
            (($attributes -band [IO.FileAttributes]::Directory) -ne 0 -or ([IO.FileInfo]$entry).Length -gt 0)) {
            throw "Nested repository in cleanup target: $entry"
        }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($entry)) { $pending.Push($child) }
        } else { $bytes += ([IO.FileInfo]$entry).Length }
    }
    $plan += [pscustomobject]@{ RelativePath=$relative; SizeMB=[math]::Round($bytes/1MB, 2); Path=$path; Bytes=$bytes }
}
# Finish all preflight checks before deleting anything.
$plan | Select-Object SizeMB,RelativePath | Format-Table -AutoSize -Wrap
$total = ($plan | Measure-Object Bytes -Sum).Sum
if (-not $Apply) {
    Write-Output ("Preview: {0} targets, {1:N2} GiB. Run scripts/clean.ps1 -Apply to remove them." -f $plan.Count, ($total/1GB))
    return
}
$removed = 0L
$description = 'generated build/test output'
foreach ($target in $plan) {
    if ($PSCmdlet.ShouldProcess($target.Path, "Remove $description")) {
        Remove-Item -LiteralPath $target.Path -Recurse -Force
        $removed += $target.Bytes
    }
}
Write-Output ("Removed {0:N2} GiB of {1}." -f ($removed/1GB), $description)
