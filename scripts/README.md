# Scripts

Run PowerShell scripts from the repository root with
`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/<name>.ps1`.
Python tools expect the runtime created by the setup scripts under `.runtime/`.

## Build, test and package

| Script | Purpose |
|---|---|
| `build.ps1` | Build, run regression checks; `-Publish` adds the portable ZIP, `-Installer` adds `Scout-Setup.exe` |
| `package-archive.ps1` | Create the release ZIP and checksums (called by `build.ps1`) |
| `verify-package.ps1` | Verify a published app directory (called by `build.ps1`) |
| `test-package.ps1` | Contract tests for the packaging scripts |
| `test-app-e2e.ps1`, `test-e2e-phase.ps1` | Drive the packaged app through isolated end-to-end phases |
| `clean.ps1`, `test-clean.ps1` | Preview/remove build output and caches; tests for the cleaner |

CI pins the SHA-256 of `test-package.ps1` and `verify-package.ps1`. If you change
either one, update the pins in `.github/workflows/windows-package-verification.yml`
in the same pull request.

## Optional AI runtime

| Script | Purpose |
|---|---|
| `setup-laya.ps1` | Install the isolated Python runtime and Laya model (`-Cuda` for NVIDIA GPUs) |
| `setup-multimodal.ps1`, `fetch-embeddinggemma2.py` | Install the multimodal add-on runtime and model (shipped with the app) |
| `evaluate.ps1` | Run the regression suite plus the authored Laya quality evaluation (`-Device cpu` or `cuda`) |
| `configure-local-ai.py` | Enable an installed local runtime in a Scout settings database |

## Multimodal validation

| Script | Purpose |
|---|---|
| `run-multimodal-hardware.ps1`, `collect-multimodal-hardware.ps1` | Physical-PC validation bundle (see [the procedure](../docs/research/MULTIMODAL_HARDWARE_VALIDATION.md)) |
| `probe-multimodal.py`, `profile-multimodal-memory.py` | Embedding and memory probes on authored inputs |
| `build-multimodal-fixtures.py`, `build-multimodal-resource-fixtures.py`, `build-office-fixtures.py` | Generate authored fixture corpora into ignored directories |

## Research and evaluation tooling

These reproduce results in [`docs/research`](../docs/research/README.md). They
aren't needed to build or run Scout. Outputs may contain metadata from your own
files, so they're written to ignored directories. Don't commit them.

- Search evaluation: `build-search-evaluation.py`, `build-semantic-evaluation.py`,
  `build-helper-evaluation.py`, `build-prospective-tasks.py`, `freeze-task-benchmark.py`
- Compact reranker screens: `compact-probe.py`, `compact-admission.py`,
  `ettin-onnx-probe.py`, `fetch-ettin-model.py`, `setup-semantic-probe.ps1`
- Office renderer experiment: `fetch-office-renderer.py`, `probe-office-renderer.py`
- Multimodal dataset intake: `build-multimodal-evaluation-pilot.py`,
  `multimodal-media-intake.py`, `intake-*.py`, `prepare-esc10-evaluation.py`,
  `capture-commons-attribution.py`, and their `test_*.py` contract tests
