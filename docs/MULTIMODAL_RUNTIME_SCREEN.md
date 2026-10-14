# EmbeddingGemma 2 Windows runtime screen

Date: 2026-10-14. Branch: `feature/multimodal-semantic-search`.

The full local model runs on Windows CPU and CUDA and produces valid embeddings
for all 14 authored smoke fixtures. This implementation supplies the model
manifest, separate dependency locks, verified downloads, an offline inference
adapter, fixture generation and benchmark tools. Scout's user-facing semantic
add-on, durable content index and retrieval policy are subsequent milestones.

Milestone 1 remains open for complete Office layout rendering and automatic
per-user renderer distribution. The probe can read DOCX paragraphs, XLSX cell
values and an embedded PPTX picture; those readers do not establish faithful
document, chart or spreadsheet layout coverage.

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

`scripts/fetch-office-probe.ps1` therefore downloads and verifies only; it does
not execute the portable installer. `scripts/probe-office-renderer.py` can test
an explicitly supplied renderer on the authored DOCX, PPTX and XLSX fixtures.
It reports unavailability with exit code 2 when no renderer exists. The next
renderer experiment should validate an upstream package extraction method or
an alternative isolated renderer without a system installation. Full Office
layout support remains a release requirement.

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
.runtime/semantic-search/.venv/Scripts/python.exe scripts/probe-office-renderer.py --fixtures artifacts/diagnostics/multimodal/new-fixtures/fixtures.json --soffice C:/Path/To/soffice.exe --output artifacts/diagnostics/multimodal/new-office.json
```

For a separate CPU environment, create another Python 3.12 venv, sync
`runtime/semantic_search/requirements.cpu.lock.txt` with uv's `--require-hashes`
and `--index-strategy unsafe-best-match`, then install the fixture lock if
running the content probe. The indexes in the locks are official PyPI and
PyTorch sources. Use the same verified model and fixture directory.

## Next implementation work

Resolve Office conversion and its per-user deployment, verify codec/source
distribution details, and measure the full pipeline on a 16 GB CPU laptop.
Then implement the independent add-on host, consent/settings, durable segment
index and query integration. CPU visual indexing must stay in the background;
query embeddings are fast enough in this screen to justify testing an
interactive retrieval path, while the memory and first-load costs need further
optimization. Keep all modality support in the release scope.
