param([ValidateSet('cpu','cuda')][string]$Device = 'cuda')
$ErrorActionPreference = 'Stop'
$scoutRoot = Split-Path -Parent $PSScriptRoot
Push-Location $scoutRoot
try {
    & dotnet build tests/Scout.Tests -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet tests/Scout.Tests/bin/Release/net10.0/Scout.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Regression suite failed.' }
    foreach ($suite in @('quality','heldout')) {
        $fixtures = "artifacts/evaluation/$suite-cases.json"
        $answers = "artifacts/evaluation/$suite-$Device-answers.json"
        & dotnet tests/Scout.Tests/bin/Release/net10.0/Scout.Tests.dll "--$suite-export" $fixtures
        if ($LASTEXITCODE -ne 0) { throw 'Fixture export failed.' }
        & .runtime/.venv/Scripts/python.exe runtime/quality.py --fixtures $fixtures --models .runtime/models --device $Device --checkpoints english --output $answers
        if ($LASTEXITCODE -ne 0) { throw 'Inference evaluation failed.' }
        & dotnet tests/Scout.Tests/bin/Release/net10.0/Scout.Tests.dll --quality-assess $fixtures $answers "artifacts/evaluation/$suite-$Device-report.json"
        if ($LASTEXITCODE -ne 0) { throw 'Ranking evaluation failed.' }
    }
} finally { Pop-Location }
