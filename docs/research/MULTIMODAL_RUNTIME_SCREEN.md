# EmbeddingGemma 2 Windows runtime screen

> The LibreOffice renderer described here (`runtime/semantic_search/office.py`,
> `msi.py`, `office_renderer.json` and its fetch/probe scripts) has since been
> removed. Restore it from Git history to reproduce these results.

Date: 2026-10-14. Branch: `feature/multimodal-semantic-search`.

The full local model runs on Windows CPU and CUDA and produces valid embeddings
for all 14 authored smoke fixtures. This implementation supplies the model
manifest, separate dependency locks, verified downloads, an offline inference
adapter, fixture generation and benchmark tools. Scout's user-facing semantic
add-on now has an independent installation/host/settings preview described in
[the add-on status](MULTIMODAL_ADDON.md). The durable content index and retrieval
policy remain subsequent milestones.

The Office spike now verifies DOCX, ODT, PPTX, ODP, XLSX and ODS conversion from
an extracted official Windows package. The layout probe embeds rendered pages,
including a native PPTX chart, rather than relying on an embedded picture.
This resolves the renderer compatibility dependency for add-on host work.
Broader layout fidelity and production installation/hardening remain required.

## Implemented runtime

The model is pinned to `914f7f89142e33e77833254d9c9b90c3cef7303b` in
`runtime/embeddinggemma2_model.json`. All 13 inference artifacts have SHA-256
digests and expected lengths. Setup verifies every artifact before recording
readiness; loading independently verifies integrity and full audio/vision
configuration. Model files remain in ignored `.runtime/semantic-search/model`.

CPU and CUDA use independent, hash-locked dependency sets with Sentence
Transformers 6.1.0, Transformers 5.19.0 and PyTorch 2.8.0. CPU uses FP32 and
the tested GPU uses BF16. The adapter disables network model access and remote
code, checks the shared input budget before inference, rejects invalid vectors
and re-normalizes truncated output. Media must already be decoded in memory;
URLs, media paths, oversized frames and invalid audio arrays are rejected.
Real-model contracts verify loading and
inference with socket connections blocked, normalized 256-dimensional output,
and rejection of an oversized query before a forward pass.

The media probe decodes video explicitly through PyAV, samples frames and
preserves frame metadata for the processor. Passing video paths through the
default TorchVision decoder failed because its call used an argument removed
in PyAV 19. Explicit decoding resolves that compatibility failure. PDF page
images are rendered in the worker with pypdfium2/PDFium rather than requiring
a WPF preview window.

## Fixture coverage

The generated corpus contains two text PDFs, a DOCX meeting note, numeric XLSX
cells, two images, a PPTX chart image, a scanned PDF, two synthetic speech clips,
electronic beeps, two videos and a code function. Queries are matched against
actual content representations; filenames are not supplied as the embedding
text. Each file and the entire fixture manifest are hashed.

Both CPU and CUDA return the expected first item in 12/14 queries across the
unconstrained fixture pool. The two misses are the presentation chart competing
with its identical standalone image, and the scanned PDF competing with its
source screenshot. Format constraints have not been applied by this probe.
These correlated authored examples are a runtime screen, not a quality holdout,
an abstention assessment or final Scout search results. No threshold or query
was tuned to those misses.

## Measured resources

Measurements use the available Windows 11 development PC with 32 GB RAM,
24 reported CPU cores, four inference threads and an RTX 5060 Laptop GPU with
approximately 8 GB VRAM. CPU measurements below use an actual CPU-only package
environment. They exclude UI, vector retrieval and Office conversion.

| Measurement | CPU only | CUDA |
| --- | ---: | ---: |
| Process model readiness including imports and checksums | 7.96 s | 11.65 s |
| Model loading component | 3.05 s | 3.99 s |
| Query embedding median / p95, 14 queries | 37.9 / 49.0 ms | 31.0 / 45.9 ms |
| Image embedding median, 2 fixtures | 3,230 ms | 148 ms |
| Audio embedding median, 3 short clips | 394 ms | 67 ms |
| Video embedding median, 2 three-second clips | 4,176 ms | 132 ms |
| Process peak working set | 3.76 GiB | 2.44 GiB |
| Process private bytes at report time | 6.28 GiB | 5.91 GiB |
| Peak GPU tensor allocation reported by PyTorch | n/a | 1.51 GiB |

The first invocation in the fresh CPU environment took 26.45 seconds; later
process startup took 7.96 seconds with local model files and dependency caches.
Neither figure is a cold-boot or clean customer-install guarantee. The 14-query
p95 is the largest observed sample, and input embeddings include first-use
kernel costs. Image/video timings reflect these small fixtures and sampling
settings, not throughput over arbitrary media. Private bytes and working set
are different counters; GPU allocation excludes driver/context overhead.

Logical installed file sizes are 3.55 GiB for the CPU environment, 7.52 GiB for
the CUDA environment and 1.42 GiB for the model directory. Environment sizes
include development fixture packages; download caches and the isolated Python
interpreter are additional. These are directory totals, not compressed download
sizes or unique disk blocks when uv shares files through hard links.

Source reports stay in ignored `artifacts/diagnostics/multimodal`: `cpu-final.json`,
`cpu-validated.json`, `cuda-v2.json`, `cpu-clean-v1.json` and `office-v1.json`. The benchmark records
dependency versions, model/fixture identities, token counts, extraction and
embedding timings, FFmpeg build metadata and Windows memory counters. Newer
reports also record source hashes and hardware metadata.

The final input-validation pass again encoded all 14 fixtures and returned
12/14 expected first choices, with a 40.5 ms median CPU query embedding and
9.31-second process startup. Seven offline integrity/input/output contracts
pass; the real-model offline and oversized-input contracts pass on both devices.
Scout's Release build passes with zero warnings and its existing C# suite
passes 223/223 checks. Runtime packaging now excludes nested Python bytecode
directories as well as the top-level cache.

## Office renderer decision

LibreOffice was not already installed on this PC. The official portable
26.2.4 standard package downloaded successfully and matched the publisher's
SHA-256 `4bde93374aef4409243505b20d16561a4628ac7591457dd01fc6e1ccf571ba65`.
An attempted hidden `/S /D=<private destination>` extraction produced no renderer
and reached its 120-second timeout. Its process was stopped and no child helper
remained. That invocation is unsuitable for Scout's automatic setup.
[Publisher package details](https://portableapps.com/apps/office/libreoffice_portable).

The successful replacement is the official LibreOffice 26.8.1 Windows x64 MSI,
pinned in `runtime/office_renderer.json`. The package is 374,870,016 bytes
(357.5 MiB) with SHA-256
`c6298b10cfa1748bcbedad94c62ce771e240e5021bfd6d7a967cd571b04b5312`.
The metadata comes from the publisher's
[metalink](https://download.documentfoundation.org/libreoffice/stable/26.8.1/win/x86_64/LibreOffice_26.8.1_Win_x86-64.msi.metalink).

`scripts/fetch-office-renderer.py` opens the verified MSI in read-only mode,
exports its embedded cabinets, and uses Windows `expand.exe` to extract data.
It reconstructs the application directory through the MSI Directory, Component
and File tables, validates file lengths and creates a complete SHA-256 inventory.
It never invokes Windows Installer installation actions, registers components,
or runs the PortableApps installer. The tested extraction took 129.7 seconds
for 19,271 application files. The resulting directory is approximately 1.42 GiB,
additional to Python/model storage; the retained MSI and cabinet/payload staging
require additional temporary disk space. This is a development workspace setup,
not yet Scout's resumable installer or a clean standard-account deployment test.
[Microsoft read-only MSI API](https://learn.microsoft.com/en-us/windows/win32/api/msiquery/nf-msiquery-msiopendatabasew).

`runtime/semantic_search/office.py` verifies that inventory once at worker
startup, takes a bounded source snapshot under a fixed filename, and converts
with a private profile using `soffice.com`. Profiles disable macro execution and
the Python macro runtime. ZIP/XML preflight rejects excessive expansion,
entities, external image/data links, DDE fields and unsupported embedded objects.
Embedded XLSX chart data is checked recursively. Ordinary hyperlinks remain
allowed. Formula-bearing spreadsheets are explicitly deferred until a
cached-value-only render input is implemented; this helper must not recalculate
workbook formulas. These cases remain discoverable through ordinary search.
[LibreOffice command-line parameters](https://help.libreoffice.org/latest/en-US/text/shared/guide/start_parameters.html).

Conversion has a deadline and cancellation; failures terminate the owned process
tree. Successful rendering supplies bounded page rasters with PDF page locators
and explicit partial coverage. Context exit removes the owned snapshot/profile
directory, and a changed source is not published. Spreadsheet PDF pages are
layout locators; sheet/cell evidence will come from dedicated structured readers.
This helper is not an OS security sandbox. Network denial, Windows job limits,
crash/orphan cleanup and host resource accounting remain production work before
using it on an opted-in personal library.

The authored layout pack adds two-page DOCX/ODT notes, XLSX/ODS numeric tables,
a two-slide PPTX with a native chart, and an ODP with a chart image. All six files
pass expected page counts and 16 combined text assertions. Both chart pages
pass fixture-specific color/pixel checks and were inspected visually. All ten
rendered pages produce normalized 768-dimensional full-model embeddings on both
CPU and CUDA.
CPU conversion plus rasterization took 7.59–10.07 seconds per file; page
embedding took 3.20–3.67 seconds. All-file renderer verification took 10.02
seconds before conversion. CUDA page embedding took 111–150 ms after the first
875 ms invocation. Each conversion uses a fresh profile. These small
fixtures do not establish font parity or fidelity over arbitrary Office files.

The visual check initially caught an OLE placeholder in place of the native
chart, even though PDF text and embedding checks passed. LibreOffice's broad
`DisableActiveContent` setting caused it. The corrected path retains macro
blocking and package preflight while permitting validated chart data; the added
chart assertion prevents that failure from passing the screen again. ODF
embedded chart objects, formula caches and complex layouts need further fixtures.

Fourteen Office contracts cover path escapes, MSI layout/collisions, package
integrity, nested external data, formulas, expansion, deadlines, cancellation,
source changes, partial pages and temporary cleanup. The seven earlier
multimodal contracts still pass, and Scout's Release build again passes zero
warnings and all 223 C# checks. Reports and page previews stay in ignored
`artifacts/diagnostics/multimodal/office-layout-cpu-v2*` and
`office-layout-cuda-v2*`; the original portable
experiment remains as historical evidence.

The PyAV wheel exposes FFmpeg build configuration and reports LGPL version 3
or later for its libraries. Its configuration also names optional codec
libraries, so exact source/build and notice requirements must be checked before
choosing a redistributed product artifact. The installed dependency locks are
for development compatibility; they are not a completed distribution review.
[PyAV Windows packaging](https://pyav.basswood.io/docs/stable/overview/installation.html),
[FFmpeg licensing](https://www.ffmpeg.org/legal.html).

## Reproduction

These scripts require uv and a Python 3.12 interpreter. They create an isolated
environment and do not modify Laya. The current development setup uses
`.runtime/.venv/Scripts/python.exe` as an interpreter fallback; pass
`-PythonPath` when using another Python 3.12 installation. Omit `-Cuda` for
CPU-only installation. All outputs refuse to overwrite previous fixture packs
or reports; use a fresh output name for each run.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-semantic-probe.ps1 -Cuda -Fixtures
.runtime/semantic-search/.venv/Scripts/python.exe scripts/build-multimodal-fixtures.py --output artifacts/diagnostics/multimodal/new-fixtures
.runtime/semantic-search/.venv/Scripts/python.exe scripts/probe-multimodal.py --fixtures artifacts/diagnostics/multimodal/new-fixtures/fixtures.json --device cuda --output artifacts/diagnostics/multimodal/new-cuda.json
.runtime/semantic-search/.venv/Scripts/python.exe runtime/test_multimodal.py
.runtime/semantic-search/.venv/Scripts/python.exe runtime/test_multimodal_inference.py --model .runtime/semantic-search/model --device cuda
.runtime/semantic-search/.venv/Scripts/python.exe scripts/fetch-office-renderer.py
.runtime/semantic-search/.venv/Scripts/python.exe scripts/build-office-fixtures.py --output artifacts/diagnostics/multimodal/new-office-fixtures
.runtime/semantic-search/.venv/Scripts/python.exe scripts/probe-office-renderer.py --fixtures artifacts/diagnostics/multimodal/new-office-fixtures/fixtures.json --output artifacts/diagnostics/multimodal/new-office.json --model .runtime/semantic-search/model --device cuda
.runtime/semantic-search/.venv/Scripts/python.exe runtime/test_office_renderer.py
```

For a separate CPU environment, create another Python 3.12 venv, sync
`runtime/semantic_search/requirements.cpu.lock.txt` with uv's `--require-hashes`
and `--index-strategy unsafe-best-match`, then install the fixture lock if
running the content probe. The indexes in the locks are official PyPI and
PyTorch sources. Use the same verified model and fixture directory.

## Next implementation work

Implement the durable segment index, work queue and query integration after the
installation/host/settings preview. Include renderer process/network
limits and standard-account setup/update/removal checks. Verify codec/source
distribution details and measure the full pipeline on a 16 GB CPU laptop before
release. CPU visual indexing must stay in the background;
query embeddings are fast enough in this screen to justify testing an
interactive retrieval path, while the memory and first-load costs need further
optimization. Keep all modality support in the release scope.
