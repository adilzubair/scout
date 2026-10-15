param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$OutputDirectory = 'artifacts/diagnostics/multimodal',
    [switch]$IncludeUi,
    [string]$UiAddonRoot,
    [string]$UiPythonHome
)
$ErrorActionPreference = 'Stop'
$scoutExecutable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $scoutExecutable -PathType Leaf)) { throw 'Scout executable missing.' }
if ($IncludeUi -and (-not $UiAddonRoot -or -not $UiPythonHome)) { throw 'The full UI run requires a verified test add-on and Python home.' }
$scoutEvidence = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('app-e2e-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $scoutEvidence | Out-Null
$scoutRuns = [Collections.Generic.List[object]]::new()
Write-Output "App E2E evidence: $scoutEvidence"
function Invoke-ScoutCheck([string]$Phase,[string]$Arguments,[string]$Profile,[string]$Result,[int]$TimeoutSeconds=60) {
    $scoutProfile = Join-Path $scoutEvidence $Profile
    New-Item -ItemType Directory -Path $scoutProfile -Force | Out-Null
    $scoutStart = [Diagnostics.ProcessStartInfo]::new()
    $scoutStart.FileName = $scoutExecutable; $scoutStart.Arguments = $Arguments
    $scoutStart.WorkingDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $scoutStart.UseShellExecute = $false; $scoutStart.CreateNoWindow = $true
    $scoutStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $scoutStart.EnvironmentVariables['SCOUT_DATA_DIR'] = $scoutProfile
    $scoutStart.EnvironmentVariables.Remove('SCOUT_TEST_PYTHON')
    $scoutStart.EnvironmentVariables.Remove('SCOUT_TEST_DEVICE')
    if ($IncludeUi) {
        $scoutStart.EnvironmentVariables['SCOUT_UI_TEST_ADDON_ROOT'] = [IO.Path]::GetFullPath($UiAddonRoot)
        $scoutStart.EnvironmentVariables['SCOUT_UI_TEST_PYTHON_HOME'] = [IO.Path]::GetFullPath($UiPythonHome)
    }
    $scoutClock = [Diagnostics.Stopwatch]::StartNew()
    $scoutChild = [Diagnostics.Process]::Start($scoutStart)
    try {
        while (-not $scoutChild.WaitForExit(1000)) {
            if ($scoutClock.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $scoutChild.Kill()
                throw "App E2E $Phase exceeded $TimeoutSeconds seconds. Keep $scoutProfile."
            }
        }
        $scoutResult = Join-Path $scoutProfile $Result
        $scoutRuns.Add([pscustomobject]@{ phase=$Phase; exitCode=$scoutChild.ExitCode; elapsedMs=$scoutClock.Elapsed.TotalMilliseconds; result=$scoutResult })
        if (Test-Path -LiteralPath $scoutResult) { Get-Content -LiteralPath $scoutResult | Write-Output }
        if ($scoutChild.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $scoutResult)) { throw "App E2E $Phase failed. Keep $scoutProfile." }
    } finally { $scoutChild.Dispose() }
}
try {
    Invoke-ScoutCheck 'save-and-reopen' '--settings-e2e-test write' 'settings' 'settings-e2e-write.txt'
    Invoke-ScoutCheck 'restart-and-read' '--settings-e2e-test read' 'settings' 'settings-e2e-read.txt'
    Invoke-ScoutCheck 'native-folder-picker' '--folder-picker-smoke-test' 'picker' 'smoke-result.txt'
    if ($IncludeUi) { Invoke-ScoutCheck 'launcher-and-previews' '--ui-smoke-test' 'ui' 'smoke-result.txt' 240 }
    $scoutAppDll = Join-Path ([IO.Path]::GetDirectoryName($scoutExecutable)) 'Scout.dll'
    [pscustomobject]@{ schemaVersion=1; scope='Isolated app integration checks; not production quality or hardware approval'; executable=$scoutExecutable; appSha256=(Get-FileHash -LiteralPath $scoutAppDll -Algorithm SHA256).Hash.ToLowerInvariant(); passed=$true; fullUiIncluded=[bool]$IncludeUi; phases=$scoutRuns.ToArray() } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scoutEvidence 'report.json') -Encoding UTF8
    Write-Output 'PASS application E2E checks; profiles and screenshots retained.'
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $scoutEvidence 'failure.txt') -Encoding UTF8
    throw
}
