# App settings and multimodal end-to-end checks

Date: 2026-10-15. Branch: `feature/multimodal-semantic-search`.

The reported immediate-reopen folder loss is reproduced before the fix: Settings
closes while keyword indexing is deliberately held pending, and the folder lists
have not reached the settings database. Saving was incorrectly dependent on a
successful full index reconfiguration. Settings now commits the validated choices
before closing. Indexing follows independently. A storage-write error retains the
open dialog, edits and retry guidance; an indexing error reports that preferences
were saved and indexing needs attention.

## Tested flows

- Real modal Settings Save followed by immediate reopen while indexing is pending.
  Additional, keyword-content, excluded, automation and multimodal folders persist.
- A second Save while the first index update is pending retains the latest choices
  after both updates finish. An actual second Scout process reopens the same private
  profile without reseeding it and verifies all folders and seven selected families.
- Injected index failure preserves saved preferences. Invalid folder input keeps
  Settings open; Cancel discards draft edits. Injected storage failure retains edits
  and shows retry guidance. Saved folders reach ordinary and keyword content search.
- Actual native folder selection, duplicate prevention, cancellation and recoverable
  browser errors continue to pass the folder-picker regression.
- The broader WPF suite checks launcher rendering, scopes, selection, groups,
  calculator, web fallback, rapid typing, all settings pages and validation focus,
  workspace naming, content coverage/revocation and evidence previews. Actual
  sandboxed DOCX/ODT/PPTX/ODP/XLSX/ODS reads retain their evidence locators. PDF
  page navigation and WAV/MP3/M4A/FLAC/MP4/MOV/MKV/WebM playback pass.
- The separate real-model production scheduler smoke indexes all seven families,
  queries code/image contents, validates current evidence, handles watched arrival/
  rename, pauses/resumes and revokes removed scope. Office screenshot checks use
  a deterministic embedding boundary; this scheduler check uses the actual model.

The Release build has zero warnings/errors. All 351 native regression checks and
24 packaging contracts pass. The original failing app test and the first test-
harness page-reference failure remain under ignored diagnostics beside the passing
results. The Windows desktop-control helper failed to initialize; these tests use
the app's real WPF controls, modal dialogs and production save path through its
isolated integration harness. Representative generated screenshots were inspected.
No personal folders/settings were changed, and the user's running Scout was kept.

## Reproduce

Build the solution, then run from the repository:

```powershell
scripts/test-app-e2e.ps1 -Executable <path-to-Scout.exe>
```

This creates a fresh private profile for Save/reopen, reuses that exact database in
a second process, and tests the real native picker in another private profile.
The script retains result files, application hashes, phase exit codes and timings.
Diagnostic windows use their own activation event and test mutex.

For the broader UI and preview suite, supply an existing verified test add-on and
its matching base Python home. It uses the authored fixtures already in this
workspace; it does not install/download a new runtime:

```powershell
scripts/test-app-e2e.ps1 -Executable <path-to-Scout.exe> -IncludeUi -UiAddonRoot <verified-addon> -UiPythonHome <matching-base-python-home>
```

The all-family real-model scheduler check is separate:

```powershell
dotnet run --project tests/Scout.Tests -c Release --no-build -- --semantic-scheduler <verified-addon> <matching-base-python-home>
```

These are functional engineering checks with authored fixtures, not exhaustive
application coverage or retrieval-quality approval. The independent benchmark,
physical 16 GB test, broader Laya/concurrency/resource cases and distribution review
remain deferred. See [the internal preview](MULTIMODAL_INTERNAL_PREVIEW.md).
