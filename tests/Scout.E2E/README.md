# Scout native end-to-end tests

This project pins [TesterArmy e2e](https://github.com/tester-army/e2e) 0.21.2.
Its runner orchestrates Scout's real WPF integration driver, native regressions,
Python contracts and actual local-model checks. The upstream browser/mobile
engines do not control WPF; the target deliberately declares `platform: windows`
without substituting a web UI. No external model account or AI calls are needed.
The wrapper disables e2e telemetry and preserves each run's JSON, JUnit and
Markdown reports under ignored `artifacts/diagnostics/e2e/runs/`.

Use Windows 11 x64 with the repository's .NET SDK and Node 22.22.3+ or 24.8+.
Run Windows-native checks in a normal desktop session; restricted file/process
permissions can prevent native identity, hardlink and AppContainer tests.

```powershell
dotnet build Scout.slnx -c Release -m:1
Set-Location tests/Scout.E2E
npm ci --ignore-scripts --no-audit --no-fund
$env:SCOUT_E2E_EXECUTABLE = 'C:/absolute/path/to/published/Scout.exe'
$env:SCOUT_E2E_ADDON_ROOT = 'C:/absolute/path/to/verified/test-addon'
$env:SCOUT_E2E_PYTHON_HOME = 'C:/absolute/path/to/matching/base-python'
npm test
```

The app target must be a self-contained package with its normal runtime/scripts.
The hidden PDF/concurrency probes require a clean base package without an installed
`.runtime` directory; use a separate disposable package for Laya installer tests.
`scripts/build.ps1 -Publish` creates that package and runs packaging checks; do
not bypass antivirus blocks or disable security tooling if a script is blocked.
The test add-on must have matching model/setup/lock hashes and an existing private
interpreter. A marker left over from a removed generation is rejected up front.
The all-family checks also require the authored Office, scanned-PDF and media
fixture sets under `artifacts/diagnostics/multimodal/`. The runner rejects missing
required fixtures before starting these phases; retain and inspect the reports.
No tests should point at personal document folders or the normal Scout profile.

On a fresh checkout, author the fixture packs before running the UI/model phases.
Use a development interpreter with the CPU reader dependencies plus the pinned
`runtime/semantic_search/requirements.fixtures.lock.txt` authoring dependencies:

```powershell
$scoutFixturePython = 'C:/absolute/path/to/development-fixture/python.exe'
& $scoutFixturePython scripts/build-office-fixtures.py --output artifacts/diagnostics/multimodal/office-fixtures-v2
& $scoutFixturePython runtime/test_media_readers.py --author-pack
& $scoutFixturePython runtime/test_ocr.py --author
```

Run these from the repository root. The Office author refuses an existing output
directory; preserve an existing verified pack instead of overwriting it.

Existing development assets can prepare a new add-on with the real setup path:

```powershell
& .runtime/python/cpython-3.12.13-windows-x86_64-none/python.exe -I -B -X utf8 runtime/semantic_search/setup.py --root artifacts/diagnostics/e2e/addon --uv C:/absolute/path/to/uv.exe --device cpu --existing-model .runtime/semantic-search/model --cache-dir .runtime/semantic-search/uv-cache
```

This reuses verified model bytes, installs pinned dependencies and checks all
encoders. It is a development installation, not clean-account install evidence.
The default Python contract interpreter is `.runtime/semantic-search/cpu-venv`;
override it with `SCOUT_E2E_PYTHON`. Laya checks use `.runtime/.venv` and the
separately downloaded pinned checkpoints.
`SCOUT_E2E_TEST_PROJECT` can select an already built Release regression project
from a clean source export; the default is this checkout's `tests/Scout.Tests`.

The hidden PDF test uses the exact published Windows PDF worker and real WPF
Previous/Next handlers in a never-shown window, including injected render failure
and retry. It does not start Scout's launcher. Set `SCOUT_E2E_CONCURRENCY=1` for
an additional workload comparing 100,000 authored metadata entries alone, during
17-file/seven-family indexing and PDF renders, and with Laya enabled as well.
It records ordinary-search timings, unchanged result signatures, Laya outcomes,
observed indexing completion and whole-job peak commit. These are engineering
measurements; visible WPF/debounce timing, battery and minimum hardware remain
separate. Other desktop workloads may affect the measurements.
The same workload verifies real Laya reset and watchdog cleanup after abrupt
owner termination, before closing its enclosing test job. Laya fallback counts
are retained separately from ordinary-search success; a passing isolation check
does not establish timely or accurate AI ranking.

Set `SCOUT_E2E_CUDA=1` to run the optional actual CUDA worker and joint audiovisual
checks with `.runtime/semantic-search/.venv` on a supported NVIDIA GPU. Otherwise
that test is explicitly skipped; CPU checks still run.

Set `SCOUT_E2E_INSTALL=1` to exercise the shipping Laya installer and validate
the resulting interpreter and newly downloaded model with adapter and HTTP tests.
This writes `.runtime` inside the selected disposable package, so that package
must live under this checkout's `artifacts/`. It can download several GB and
retains partial downloads for retry. Do not distribute that post-install test
directory as the release ZIP; build a clean package for distribution.

Run narrower groups with `npm test -- --grep 'Expanded launcher'`,
`--grep 'Repeated PDF'`, `--grep 'Real WPF'`, or `--grep 'Real-model'`.
The app also supports isolated `--controls-smoke-test` and
`--pdf-preview-smoke-test`; both require a fresh `SCOUT_DATA_DIR`.
`scripts/test-app-e2e.ps1 -ControlsOnly` and `-PdfOnly` create those profiles.

Interaction reports distinguish fixture callbacks from real native operations.
Settings toggle/choice checks use detached dialogs; durable Save/reopen/restart,
native folder selection, actual readers, model inference and previews have
separate tests. Control counts are inventories, not proof every possible state
or hardware configuration was exercised. System lock/sleep/radio/deletion actions
are covered by selection/dispatch contracts without applying those changes to the
host machine.

See [validation](../../docs/research/VALIDATION.md) for measured results and
remaining release checks.
