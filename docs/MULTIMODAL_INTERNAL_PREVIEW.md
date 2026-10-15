# Scout multimodal internal preview

Handoff: 2026-10-15. Branch: `feature/multimodal-semantic-search`.
Status: ready for internal testing. Production release approval is deferred.

The user approved finishing the existing internal preview within a 60-minute
limit, deferring the extensive quality benchmark, physical 16 GB validation and
distribution review. The optional add-on retains documents, presentations,
spreadsheets, code, images, audio and video. It uses the full EmbeddingGemma 2
model locally, works with Laya off and requires no LibreOffice.

## Delivery

Use `artifacts/Scout-multimodal-preview.zip`. It contains the
tested application, bundled .NET runtime, validation driver and authored fixtures.
Python, dependencies and model weights download when the add-on is installed;
they are not inside this ZIP. No .NET SDK, Office or LibreOffice is required.

The adjacent `artifacts/Scout-multimodal-preview.zip.sha256` identifies this ZIP.
The earlier hardware-validation ZIP is retained as historical evidence; use the
new preview for interactive testing because it fixes the settings folder picker.

| Identity | Commit |
| --- | --- |
| Packaged product, including folder-picker crash fix | `f5f0b9b` |
| Prior full-model CPU/CUDA resource package | `eacfe164dd75d0c3e51b052ba9d6a788573a9be2` |
| Shipping setup bytecode preparation | `4996824bff5ee8fdfcdfcfb7ec1e604a3c60f650` |
| Recorded CPU/CUDA validation evidence | `5efdc8c` |
| Development-only natural-code intake tooling; not packaged | `79faae2` |

## Try the application

1. On Windows 11 x64, fully extract the ZIP into a short, plain local folder,
   outside OneDrive/network shares. Close any existing Scout instance, then open
   `package/Scout.exe`. This application uses the normal Scout user profile.
2. Open **Settings → Multimodal**. Start with **cpu** and four CPU threads. Choose
   **Install or verify add-on**, wait for completion, then **Test add-on**. Setup
   needs internet and several GB of downloads plus temporary space. The validation
   procedure reserves 12 GiB free space; this is a test reserve, not a measured
   product minimum. Plug the PC into power; indexing pauses on battery by default.
3. Choose a small local test folder containing supported examples from all seven
   families. Keep all content types selected, check **Enable the multimodal add-on**
   and **Save settings**. Leave Laya off for the initial test.
4. Wait for indexing, search by content and inspect the matched evidence in Scout's
   preview: document/page or slide, sheet/cells, code lines, image, or media time.
   Use **Review content coverage** for partial, unsupported and failed files.

Enabling selected folders also adds their ordinary file metadata to Scout's
library. Disabling the add-on stops semantic content search; ordinary search
continues. **Remove add-on** removes its runtime/model while preserving the
ordinary library and Laya. See [the detailed add-on notes](MULTIMODAL_ADDON.md)
for storage maintenance, scope, formats and evidence behavior.

## Run the isolated validation later

The separate runner uses only bundled/generated fixtures and a private add-on;
it does not inspect personal folders or use the normal Scout database. On the
physical 16 GB PC, open Windows PowerShell in the extracted root and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -Physical16Gb
```

Keep the results folder and the `diagnostics-....zip` printed at the end,
including failed attempts. Nothing is sent automatically. Follow the included
README or [hardware instructions](MULTIMODAL_HARDWARE_VALIDATION.md). The user
will run this later; physical minimum-hardware support is not yet established.

## Verification and known limitations

The folder-picker fix passes a clean Release build, all 351 native regression
checks and 24 packaging contracts. An isolated WPF regression opens the actual
Windows folder dialog three times: select, repeat select and cancel. It verifies
the selected path, duplicate prevention, unchanged values on cancellation and
usable settings/manual entry after simulated COM, Win32 and dialog-state failures.
This also passes against the replacement self-contained application.

Prior evidence includes 17 Python setup/model-reuse checks. The `eacfe16` package
passes all 21 authored
files across seven families, video preview, CPU/CUDA setup/fallback/offline retry/
rollback and revision-3 generation preparation. On the 32 GB development PC,
three CPU process-cold queries take 13.19/10.77/10.87 seconds; warm semantic p95 at
100,000 synthetic segments is 331.59 ms. Whole-job peak commit is 5.82 GiB for
content/preview and 5.06 GiB for scale, with Laya off and OS file cache unflushed.
These are engineering checks, not representative retrieval-quality results. The
folder-picker fix does not change readers, model/runtime pins, index or retrieval
policy; the expensive full-model resource workload was not repeated for this UI fix.

- Semantic false positives remain a known issue: the small authored pilot admitted
  every negative query at the unchanged 0.35 similarity floor. Natural-audio
  development results also missed the intended first file in three of ten tasks.
  Review evidence; absence of a result does not prove absence of content.
- Office evidence exposes extracted text, cells, charts and embedded images;
  exact Office layout is unavailable. Budget-limited content is partial. OCR
  depends on installed Windows language packs; Arabic OCR was not validated here.
- The independent 400-task quality benchmark, physical 16 GB run, ordinary WPF/
  Laya/concurrent workloads, remaining resource/lifecycle/account/disk checks and
  same-device dependency-version upgrades are deferred production follow-ups.
  Earlier intermittent extraction, cleanup and cold-start failures remain retained
  and unexplained; later successful runs do not erase them.
- Native runtime/codec distribution obligations remain under review. This handoff
  does not approve public distribution or a production release.

The partially reviewed natural-code corpus and other private development work
are preserved. They are not bundled and are not prerequisites for trying this
preview. No additional corpus collection or benchmark work is scheduled by this
handoff. The internal-preview objective is complete; autonomous work stops here.
