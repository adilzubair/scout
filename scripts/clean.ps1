# Never add broad artifacts/** or .runtime/** patterns: they contain fixtures
# and active models. Retired research data requires its separate explicit mode.
[CmdletBinding(SupportsShouldProcess)]
param([switch]$Apply, [switch]$RetiredResearch)
$ErrorActionPreference = 'Stop'
$scoutRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$prefix = $scoutRoot + [IO.Path]::DirectorySeparatorChar
function Test-RepositoryMarker([string]$Directory) {
    $marker = Join-Path $Directory '.git'
    # Empty .git files are uv cache markers, not repository metadata.
    return [IO.Directory]::Exists($marker) -or ([IO.File]::Exists($marker) -and ([IO.FileInfo]$marker).Length -gt 0)
}

$patterns = @(
    'src/Scout.App/bin', 'src/Scout.App/obj',
    'src/Scout.Core/bin', 'src/Scout.Core/obj',
    'tests/Scout.Tests/bin', 'tests/Scout.Tests/obj',
    'tests/Scout.Benchmark', 'tests/Scout.Lifecycle',
    'runtime/__pycache__', 'runtime/semantic_search/__pycache__', 'scripts/__pycache__',
    'torchinductor_*',
    '.runtime/cohort-smoke-20260926', '.runtime/feedback-*-20260926', '.runtime/tmp-build',
    'artifacts/bc', 'artifacts/bc-device', 'artifacts/bc-upgrade',
    'artifacts/build-temp', 'artifacts/bytecode-setup-previous',
    'artifacts/collector-check-*', 'artifacts/desktop-polish-*',
    'artifacts/devsw', 'artifacts/devsw2', 'artifacts/diagnose-*', 'artifacts/env-*',
    'artifacts/hw-*', 'artifacts/installer-diagnose', 'artifacts/installer-final-diagnose*',
    'artifacts/release-final', 'artifacts/release-source', 'artifacts/review-temp',
    'artifacts/Scout-e2e-fixed', 'artifacts/Scout-folder-picker-fix',
    'artifacts/Scout-hardware-validation*', 'artifacts/Scout-lightweight*',
    'artifacts/Scout-multimodal-preview*', 'artifacts/Scout-settings-persistence-fix',
    'artifacts/Scout-UI*', 'artifacts/site-image-*', 'artifacts/site-tmp',
    'artifacts/smoke*', 'artifacts/temp', 'artifacts/test-tmp', 'artifacts/tmp',
    'artifacts/ui-*', 'artifacts/verified-*',
    'artifacts/*.log', 'artifacts/debug-scout.py', 'artifacts/release-final.tar',
    'artifacts/release-source.tar', 'artifacts/release-notes.txt', 'artifacts/scout-site.tar.gz',
    'artifacts/diagnostics/continue-publish', 'artifacts/diagnostics/retry-publish',
    'artifacts/diagnostics/folder-search/build', 'artifacts/diagnostics/settings-ux/build',
    'artifacts/diagnostics/temp-exclusions/build',
    'artifacts/diagnostics/fsearch-redesign/baseline',
    'artifacts/diagnostics/fsearch-redesign/*-harness',
    'artifacts/diagnostics/fsearch-redesign/staged-cache',
    'artifacts/diagnostics/fsearch-redesign/staged-features',
    'artifacts/diagnostics/fsearch-redesign/staged-overlay',
    'artifacts/diagnostics/fsearch-redesign/staged-replay',
    'artifacts/diagnostics/fsearch-redesign/verified-build',
    'artifacts/diagnostics/multimodal/hardware-isolated-build',
    'artifacts/diagnostics/multimodal/sheet-context-build',
    'artifacts/diagnostics/multimodal/maintenance-previous-package-*',
    'artifacts/diagnostics/multimodal/upgrade-previous-package-*',
    'artifacts/diagnostics/multimodal/fresh-setup/*/probe*',
    'artifacts/diagnostics/multimodal/generation-upgrade/*/package-*',
    'artifacts/diagnostics/multimodal/device-coexistence-startup/*/probe',
    'artifacts/diagnostics/multimodal/startup-trials/*/probe',
    'artifacts/diagnostics/multimodal/resource-negative-*-bundle/package'
)
# Retain the latest preview and test installations containing runtime links.
# Standard release outputs, fixtures, reports and local runtimes are not selected.
$retained = @(
    'artifacts/Scout-multimodal-preview-e2e-fixed',
    'artifacts/Scout-multimodal-preview-e2e-fixed.zip',
    'artifacts/Scout-multimodal-preview-e2e-fixed.zip.sha256',
    'artifacts/env-fresh', 'artifacts/env-powershell-diagnostic-v1',
    'artifacts/env-uv-diagnostic-v1', 'artifacts/hw-rehearsal'
)
if ($RetiredResearch) {
    # This mode leaves current build output and all active AI installations alone.
    $patterns = @('.runtime/models/semantic-qwen3-1.7b', '.runtime/semantic-search/office')
}
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
if ($RetiredResearch) {
    $diagnostics = Join-Path $scoutRoot 'artifacts/diagnostics'
    $folders = [Collections.Generic.Stack[string]]::new()
    if ([IO.Directory]::Exists($diagnostics)) { $folders.Push($diagnostics) }
    while ($folders.Count) {
        $folder = $folders.Pop()
        if (([IO.File]::GetAttributes($folder) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Linked diagnostics directory refused: $folder"
        }
        foreach ($entry in Get-ChildItem -LiteralPath $folder -Force) {
            # Keep authored inputs, including archives and databases used as fixtures.
            if ($entry.Name -eq '.git' -or $entry.Name -match '(?i)(^|[-_])fixtures?($|[-_])') { continue }
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if ($entry.PSIsContainer) {
                if (Test-RepositoryMarker $entry.FullName) { continue }
                $folders.Push($entry.FullName)
                continue
            }
            if ($entry.Extension -in @('.db', '.zip') -or $entry.Name -match '\.db-(wal|shm|journal)$' -or
                ($entry.Name -like 'index-*.bin' -and $entry.Directory.Name -like '*.db.search')) {
                [void]$candidates.Add($entry.FullName)
            }
        }
    }
}
$plan = @()
foreach ($candidate in $candidates) {
    $path = [IO.Path]::GetFullPath($candidate)
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Cleanup path escaped repository: $path" }
    $relative = $path.Substring($prefix.Length).Replace('\', '/')
    if ($relative -in $retained) { continue }
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
    if ($RetiredResearch -and [IO.File]::Exists($path)) {
        # Refuse files held by an ongoing test or SQLite session before any deletion.
        $handle = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $handle.Dispose()
    }
}
# Finish all preflight checks before deleting anything.
$plan | Select-Object SizeMB,RelativePath | Format-Table -AutoSize -Wrap
$total = ($plan | Measure-Object Bytes -Sum).Sum
if (-not $Apply) {
    $mode = if ($RetiredResearch) { ' -RetiredResearch' } else { '' }
    Write-Output ("Preview: {0} targets, {1:N2} GiB. Run scripts/clean.ps1{2} -Apply to remove them." -f $plan.Count, ($total/1GB), $mode)
    return
}
$removed = 0L
$description = if ($RetiredResearch) { 'retired research data' } else { 'generated build/test output' }
foreach ($target in $plan) {
    if ($PSCmdlet.ShouldProcess($target.Path, "Remove $description")) {
        Remove-Item -LiteralPath $target.Path -Recurse -Force
        $removed += $target.Bytes
    }
}
Write-Output ("Removed {0:N2} GiB of {1}." -f ($removed/1GB), $description)
