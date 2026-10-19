param(
    [Parameter(Mandatory)][ValidateSet('native','packaging','cleanup','controls','pdf','pdf-worker','concurrency','application','scheduler','python','laya','cuda','laya-install')][string]$Phase,
    [string]$Executable = $env:SCOUT_E2E_EXECUTABLE,
    [string]$AddonRoot = $env:SCOUT_E2E_ADDON_ROOT,
    [string]$PythonHome = $env:SCOUT_E2E_PYTHON_HOME,
    [string]$Python = $env:SCOUT_E2E_PYTHON
)
$ErrorActionPreference = 'Stop'
$scoutRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Executable) { $Executable = Join-Path $scoutRoot 'artifacts/Scout/Scout.exe' }
if (!$AddonRoot) { $AddonRoot = Join-Path $scoutRoot 'artifacts/diagnostics/e2e/addon' }
if (!$PythonHome) { $PythonHome = Join-Path $scoutRoot '.runtime/python/cpython-3.12.13-windows-x86_64-none' }
if (!$Python) { $Python = Join-Path $scoutRoot '.runtime/semantic-search/cpu-venv/Scripts/python.exe' }
$scoutTestProject = if ($env:SCOUT_E2E_TEST_PROJECT) { $env:SCOUT_E2E_TEST_PROJECT } else { Join-Path $scoutRoot 'tests/Scout.Tests/Scout.Tests.csproj' }
if ($Phase -in @('application','scheduler','pdf','pdf-worker','concurrency')) {
    $scoutFixtures = @('scan-fixtures-v1/f003.pdf')
    if ($Phase -notin @('pdf','pdf-worker')) {
        $scoutFixtures += @('office-fixtures-v2/fixtures.json')
        $scoutFixtures += @('notes.docx','notes.odt','growth.pptx','growth.odp','sales.xlsx','sales.ods') | ForEach-Object { 'office-fixtures-v2/' + $_ }
        $scoutFixtures += @('tone.wav','tone.mp3','tone.m4a','tone.flac','scene.mp4','scene.mov','scene.mkv','scene.webm') | ForEach-Object { 'media-fixtures-v1/' + $_ }
    }
    foreach ($scoutFixture in $scoutFixtures) {
        if (!(Test-Path -LiteralPath (Join-Path $scoutRoot ('artifacts/diagnostics/multimodal/' + $scoutFixture)) -PathType Leaf)) { throw ('Required authored fixture is missing: ' + $scoutFixture) }
    }
}
if ($Phase -in @('application','scheduler','concurrency')) {
    $scoutMarkerPath = Join-Path $AddonRoot 'installed.json'
    if (!(Test-Path -LiteralPath $scoutMarkerPath -PathType Leaf)) { throw 'Prepare a verified E2E add-on and set SCOUT_E2E_ADDON_ROOT; no installation marker was found.' }
    $scoutMarker = Get-Content -LiteralPath $scoutMarkerPath -Raw | ConvertFrom-Json
    if ($scoutMarker.generation -notmatch '^(cpu|cuda)-[0-9a-f]{24}$') { throw 'The E2E add-on generation is invalid.' }
    $scoutInterpreter = Join-Path $AddonRoot ('generations/' + $scoutMarker.generation + '/venv/Scripts/python.exe')
    if (!(Test-Path -LiteralPath $scoutInterpreter -PathType Leaf)) { throw 'The E2E add-on marker refers to a removed runtime. Rebuild the test add-on before running UI/model checks.' }
    foreach ($scoutPin in @(@('modelManifestSha256','runtime/embeddinggemma2_model.json'),@('setupManifestSha256','runtime/multimodal_setup.json'),@('lockSha256',('runtime/semantic_search/requirements.' + $scoutMarker.device + '.lock.txt')))) {
        if ($scoutMarker.($scoutPin[0]) -ne (Get-FileHash -LiteralPath (Join-Path $scoutRoot $scoutPin[1]) -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'The E2E add-on pins do not match this checkout. Re-verify the installation.' }
    }
}
Push-Location $scoutRoot
try {
    switch ($Phase) {
        native {
            & dotnet run --project $scoutTestProject -c Release --no-build --no-restore
            if ($LASTEXITCODE -ne 0) { throw 'Native regressions failed.' }
        }
        packaging { & "$PSScriptRoot/test-package.ps1" }
        cleanup { & "$PSScriptRoot/test-clean.ps1" }
        controls { & "$PSScriptRoot/test-app-e2e.ps1" -Executable $Executable -ControlsOnly -OutputDirectory artifacts/diagnostics/e2e }
        pdf { & "$PSScriptRoot/test-app-e2e.ps1" -Executable $Executable -PdfOnly -OutputDirectory artifacts/diagnostics/e2e }
        pdf-worker {
            $scoutPdfOutput=Join-Path $scoutRoot ('artifacts/diagnostics/e2e/pdf-workers-'+[guid]::NewGuid().ToString('N'))
            & dotnet run --project $scoutTestProject -c Release --no-build --no-restore -- --pdf-worker-checks ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Executable))) (Join-Path $scoutRoot 'artifacts/diagnostics/multimodal/scan-fixtures-v1/f003.pdf') $scoutPdfOutput 3
            if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $scoutPdfOutput 'profile/pdf-recovery.txt'))){throw 'Hidden PDF worker/recovery checks failed; rebuild the Release test driver if the new mode is unavailable.'}
        }
        concurrency {
            $scoutConcurrencyOutput=Join-Path $scoutRoot ('artifacts/diagnostics/e2e/concurrent-search-'+[guid]::NewGuid().ToString('N'))
            $scoutLayaPython=Join-Path $scoutRoot '.runtime/.venv/Scripts/python.exe'
            & dotnet run --project $scoutTestProject -c Release --no-build --no-restore -- --concurrent-search ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Executable))) $AddonRoot $PythonHome $scoutLayaPython $scoutConcurrencyOutput
            if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $scoutConcurrencyOutput 'report.json'))){throw 'Concurrent search workload failed; retain its private profiles and logs.'}
        }
        application {
            & "$PSScriptRoot/test-app-e2e.ps1" -Executable $Executable -IncludeUi -UiAddonRoot $AddonRoot -UiPythonHome $PythonHome -OutputDirectory artifacts/diagnostics/e2e
        }
        scheduler {
            & dotnet run --project $scoutTestProject -c Release --no-build --no-restore -- --semantic-scheduler $AddonRoot $PythonHome
            if ($LASTEXITCODE -ne 0) { throw 'Real-model scheduler failed.' }
        }
        python {
            # Excludes inference scripts requiring separate CUDA/large fixture runs.
            $scoutContracts = @('test_multimodal','test_multimodal_setup','test_model_reuse','test_multimodal_worker','test_notices','test_content_readers','test_media_readers','test_ocr','test_playback','test_streaming_ods','test_xlsx_context')
            Push-Location runtime
            try { & $Python -m unittest @scoutContracts; if ($LASTEXITCODE -ne 0) { throw 'Python contracts failed.' } }
            finally { Pop-Location }
        }
        laya {
            $scoutLayaPython=Join-Path $scoutRoot '.runtime/.venv/Scripts/python.exe'
            & $scoutLayaPython runtime/test_runtime.py
            if($LASTEXITCODE -ne 0){throw 'Laya adapter contracts failed.'}
            & $scoutLayaPython runtime/test_http.py
            if($LASTEXITCODE -ne 0){throw 'Laya authenticated HTTP checks failed.'}
        }
        cuda {
            $scoutCudaPython=Join-Path $scoutRoot '.runtime/semantic-search/.venv/Scripts/python.exe'
            $scoutModel=Join-Path $scoutRoot '.runtime/semantic-search/model'
            & $scoutCudaPython runtime/test_multimodal_worker_inference.py --model $scoutModel --device cuda
            if($LASTEXITCODE -ne 0){throw 'CUDA worker contracts failed.'}
            $scoutJointOutput=Join-Path $scoutRoot ('artifacts/diagnostics/e2e/joint-av-cuda-'+[guid]::NewGuid().ToString('N')+'.json')
            & $scoutCudaPython runtime/test_joint_av_inference.py --model $scoutModel --device cuda --output $scoutJointOutput
            if($LASTEXITCODE -ne 0){throw 'CUDA joint audiovisual compatibility failed.'}
        }
        laya-install {
            $scoutPackage=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Executable))
            $scoutArtifacts=[IO.Path]::GetFullPath((Join-Path $scoutRoot 'artifacts')).TrimEnd('\')+'\'
            if(!$scoutPackage.StartsWith($scoutArtifacts,[StringComparison]::OrdinalIgnoreCase)){throw 'Installer tests require a disposable package inside this checkout artifacts directory.'}
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scoutPackage 'scripts/setup-laya.ps1') -Model english
            if($LASTEXITCODE -ne 0){throw 'Laya package setup failed.'}
            $scoutPrivatePython=Join-Path $scoutPackage '.runtime/.venv/Scripts/python.exe'
            $scoutPrivateModels=Join-Path $scoutPackage '.runtime/models'
            $scoutOriginalModels=$env:SCOUT_TEST_MODELS
            try{
                $env:SCOUT_TEST_MODELS=$scoutPrivateModels
                & $scoutPrivatePython runtime/test_runtime.py
                if($LASTEXITCODE -ne 0){throw 'Newly installed Laya runtime contracts failed.'}
                & $scoutPrivatePython runtime/test_http.py --models-dir $scoutPrivateModels
                if($LASTEXITCODE -ne 0){throw 'Newly installed Laya HTTP checks failed.'}
            }finally{$env:SCOUT_TEST_MODELS=$scoutOriginalModels}
        }
    }
} finally { Pop-Location }
