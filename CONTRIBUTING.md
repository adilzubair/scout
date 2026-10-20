# Contributing to Scout

Thanks for your interest in Scout. Bug reports, ideas and pull requests are all
welcome.

## Before you start

- For anything beyond a small fix, open an issue first so we can agree on the
  approach.
- Scout targets Windows 11 x64. You need the .NET SDK pinned in `global.json`.
  Python is only needed for the optional AI runtime, and the setup scripts
  install their own.
- Never commit personal data. Indexes, diagnostic reports, model weights, local
  runtimes and build output belong in the ignored `artifacts/` and `.runtime/`
  directories.

## Build and test

```powershell
dotnet run --project src/Scout.App                                         # run from source
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1      # build + regression checks
```

`build.ps1` builds `Scout.slnx` in Release mode, runs the native regression
suite (`tests/Scout.Tests`, a console runner) and the packaging contract tests.
Warnings are treated as errors.

If you change the Python runtime, also run its checks with the runtime from
`scripts/setup-laya.ps1`:

```powershell
.runtime/.venv/Scripts/python.exe runtime/test_runtime.py
.runtime/.venv/Scripts/python.exe runtime/test_http.py
```

UI and integration changes can be exercised with the app's isolated diagnostic
modes. Set `SCOUT_DATA_DIR` to a fresh, throwaway directory first, so your real
settings in `%LOCALAPPDATA%\Scout` aren't touched:

```powershell
$env:SCOUT_DATA_DIR = "$PWD/artifacts/diag-data"
dotnet run --project src/Scout.App -- --smoke-test      # rendering, groups, previews
dotnet run --project src/Scout.App -- --ui-smoke-test   # screenshots, settings navigation
```

See [`tests/Scout.E2E/README.md`](tests/Scout.E2E/README.md) for the full
Windows end-to-end runner and [`scripts/README.md`](scripts/README.md) for the
rest of the tooling.

## Pull requests

- Keep each pull request focused on one change, and describe what you verified.
- Match the style of the surrounding code. Nullable reference types are enabled.
- Add or update regression checks in `tests/Scout.Tests` for behavior changes.
- Update the user-facing docs (`README.md`, `docs/USER_GUIDE.md`) when behavior
  changes.
- Some files are byte-pinned. `.gitattributes` fixes line endings for evaluation
  protocols, runtime manifests and lock files. CI also checks the SHA-256 of
  `scripts/test-package.ps1` and `scripts/verify-package.ps1`. If you change a
  pinned file, update its pin in the same pull request.

## Design principles

A change should preserve these properties:

- Nothing launches without Enter or an explicit Open action.
- Models only choose among known candidate IDs. They never produce commands.
- Document contents never reach the ranking model.
- Model services bind only to `127.0.0.1` with per-session tokens.
- Optional features (local AI, browser history, content search) stay opt-in.

## License

By contributing, you agree that your contributions are licensed under the
[MIT License](LICENSE).
