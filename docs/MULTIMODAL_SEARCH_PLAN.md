# Scout multimodal semantic search implementation plan

Date: 2026-10-14. Branch: `feature/multimodal-semantic-search`.
Status: implementation started. The full-model runtime and content smoke probe
are implemented and verified on CPU and CUDA. The Office compatibility spike
now passes DOCX/ODT, PPTX/ODP and XLSX/ODS conversion from a privately extracted
official Windows package, including native presentation chart rendering.
The independent add-on setup, host and settings preview is implemented and
verified. The separate SQLite segment index and durable queue now pass crash,
source-change, revocation, atomic replacement and quota contracts. They are not
yet connected to the application; source acquisition and isolated readers are
the next integration slice. Clean
standard-account installation, renderer hardening, distribution review and
release evaluations remain required. See
[the runtime screen](MULTIMODAL_RUNTIME_SCREEN.md) for results and reproduction.
See [the add-on preview](MULTIMODAL_ADDON.md) for lifecycle, installation,
prepared-media boundaries and verification limits.

Build an optional local content-search add-on using the full EmbeddingGemma 2
model. The first release must search document, spreadsheet, code, image, audio,
and video contents. Internal milestones can land separately, but a text-only
implementation does not satisfy this release scope.

Users install the add-on, select folders, and enable content indexing explicitly.
Scout continues to provide ordinary search while setup, indexing, or inference
is unavailable. Semantic retrieval must work with Laya disabled; Laya remains
an independently enabled refinement feature.

## Product scope

| Content | Required first-release coverage | Search evidence |
| --- | --- | --- |
| Documents | PDF, DOCX, ODT, TXT and Markdown; PDF text and rendered pages; Office text and rendered document pages | Page or section, text excerpt or visual thumbnail |
| Presentations | PPTX and ODP, including slide text, diagrams and charts | Slide number and thumbnail |
| Spreadsheets | XLSX, ODS and CSV; cell values, headers and table relationships; rendered charts and relevant sheet regions | Sheet name and cell range |
| Code | Existing supported source/configuration formats, preserving functions or logical sections where practical | Line range and excerpt |
| Images | PNG, JPEG, WebP, BMP and TIFF; image content and available OCR text | Image thumbnail and extracted text where present |
| Audio | WAV, MP3, M4A and FLAC; speech and environmental sounds | Start/end timestamps and in-app playback |
| Video | MP4, MOV, MKV and WebM; sampled visual content and audio tracks | Timestamp, frame thumbnail and in-app playback |

Validate actual codec/format combinations, not extensions alone. Legacy binary
Office formats, encrypted documents, archives and unsupported codecs get an
explicit unsupported status and remain discoverable by filename. Multi-page
images and long documents/media are segmented. Budget limits produce a visible
partial-index status rather than implying complete content coverage.

Examples for acceptance include finding an invoice by its contents despite an
unhelpful filename, a chart in a presentation, a value in a spreadsheet, a
payment error screenshot, a spoken discussion in an audio recording, and a
specific visual event in a video. Related descriptions with no corresponding
content must return no semantic match.

## Current Scout integration points

- `src/Scout.Core/ContentIndex.cs` stores local FTS5 text. Keep this keyword path
  and its existing clear behavior; introduce a separate semantic index.
- `src/Scout.Core/PreviewText.cs` provides bounded preview extraction. It flattens
  Office text and reads XLSX shared strings, so it cannot provide complete cell
  values, sheet relationships or visual layout for this feature.
- `src/Scout.Core/ContentIndexer.cs` limits files to 16 MiB and delegates to
  preview extraction. Introduce dedicated content readers with per-format
  budgets instead of simply removing those limits.
- `src/Scout.App/IndexService.cs` already manages roots, exclusions, file events
  and reconciliation. Feed semantic indexing through a separate durable queue;
  never run long media extraction inside its metadata refresh gate. Its current
  settings reconfiguration clears the keyword index; semantic configuration
  should reconcile changed scope without rebuilding unaffected content.
- `src/Scout.Core/SearchEngine.cs`, `Shortlist.cs` and `RankingPolicy.cs` supply
  deterministic search and a maximum 32-item model shortlist. Add content
  retrieval before candidate selection, with a separate admission policy.
- `src/Scout.App/LocalSetup.cs`, `ModelHost.cs` and `Dialogs.cs` provide patterns
  for installation, authenticated loopback inference, settings and cancellation.
  Add a separate host; current Laya startup and its two-second ranking deadline
  are unsuitable for background multimodal indexing.
- `src/Scout.App/PreviewWindow.cs` renders PDFs and plays media. Extend it to
  navigate to matched pages, sections and timestamps. External Open continues
  to open the original file; page/time navigation is guaranteed in Scout's own
  supported preview, not every third-party application.

## Model and dependency decisions

Use `google/embeddinggemma-2` with all modality encoders available. It has 740M
parameters, an 8,192-token shared input budget and a native 768-dimensional
embedding. Use the published query/document prefixes, normalize embeddings,
and reject non-finite outputs. Run FP32 on CPU or BF16 on supported GPUs; do
not use FP16. Start with 768 dimensions for the quality reference and compare
512 and 256 before selecting a shipping format. These are publisher-supported
configurations, not Scout performance measurements.
[Google model card](https://ai.google.dev/gemma/docs/embeddinggemma/model_card_2).

Use a dedicated Python environment initially. Google's integration guide
specifies Sentence Transformers 6.1.0 or later; verify a compatible Transformers,
PyTorch and multimedia dependency set, then pin exact versions and hashes in
a separate lock file. Do not upgrade Laya's environment in place.
[Google developer guide](https://developers.googleblog.com/embeddinggemma-2-the-developer-guide/).

Proposed extraction stack to validate in milestone 1:

- PDF text through pypdf, with page rendering initially implemented through
  pypdfium2 in the isolated worker; evaluate reuse of Windows rendering later.
  pypdf extracts text but does not supply OCR or page rendering.
  [pypdf documentation](https://pypdf.readthedocs.io/en/stable/).
- Dedicated Open XML/OpenDocument readers for paragraphs, slides, formulas,
  cached cell values and table structure. Never execute workbook formulas,
  macros or external links while extracting.
- A headless Office rendering worker, initially evaluated with LibreOffice.
  Validate unattended DOCX/PPTX/XLSX and ODF rendering, chart fidelity, font
  substitutions, timeouts and a per-user installation without admin rights.
  The standard LibreOffice installer requires administrator rights, so it
  cannot be assumed to fit Scout's installation flow. Resolve an appropriate
  distribution or alternative renderer in the initial spike.
  [LibreOffice system requirements](https://www.libreoffice.org/system-requirements/).
  The spike now extracts the pinned 26.8.1 MSI through read-only data APIs and
  embedded cabinets, without executing installer actions. It validates all
  extracted application files before conversion. This resolves development
  runtime viability; it is not a completed standard-account installation test.
  Formula-bearing spreadsheets and embedded ODF objects currently receive an
  unsupported status. Cached-value render inputs and additional chart/layout
  fixtures remain part of the document-reader milestone.
- Windows image decoding and local OCR where available; define missing OCR
  language handling. Vision embeddings remain usable without OCR.
- A pinned FFmpeg/ffprobe build for bounded audio/video decoding. Record the
  actual build configuration and corresponding source/notices; optional
  components can change its license. Select the build before packaging.
  [FFmpeg licensing](https://www.ffmpeg.org/legal.html).

Model setup must pin the repository revision, artifact hashes, processor,
tokenizer, pooling and normalization configuration. Record distribution
requirements for every bundled component in `docs/THIRD_PARTY.md` before release.
Investigate quantized full-modality runtimes as an optimization only after a
working reference; any alternative must pass the same all-modality tests.

## Architecture

### Installation and lifecycle

Add settings for semantic installation, enablement, selected folders, modality
selection, processing device, indexing pause, index quota and resource profile.
Keep them separate from `AiEnabled`, `SemanticHelper`, and the existing keyword
content setting. All modalities are supported by the installed add-on; users
may disable particular types in their own libraries.

Use a per-user add-on directory under Scout's data directory. Installation
downloads dependencies with progress, cancellation, resumability, checksums and
an atomic ready marker. Failure leaves the existing Scout install usable.
Normal inference uses cached local files and performs no network downloads.

The host owns a hidden worker process with authenticated random loopback ports
and a Windows job lifetime. Support explicit states: not installed, installing,
disabled, loading, indexing, ready, paused, partially indexed and failed.
Prioritize interactive requests between bounded indexing jobs. Idle unload
depends on whether indexing is active; do not copy Laya's one-minute shutdown
rule while a background job is running. Quit terminates workers and preserves
resumable queue state.

### Content indexing and persistence

Introduce `SemanticIndex`, `SemanticIndexer`, `ContentSegment` and extractor
interfaces in Core, with Windows rendering/decoding adapters in App or isolated
workers. `SemanticHost` in App manages the embedding worker under a new
`runtime/semantic_search/` package. File names are proposed implementation
targets, not existing APIs.

Each segment stores its candidate ID, source revision, modality, content
locator, extractor version and model configuration identity. Locators preserve
page/slide numbers, sheet/cell ranges, line ranges or media start/end times.
Store vectors and bounded evidence in a private SQLite database, separate from
`scout.db` and the existing keyword index. Temporary rendered pages and decoded
media use per-job directories and are cleaned after use and on crash recovery.

Persist indexing jobs with retry counts and configuration generations. Recheck
file identity, modification time and length before publishing a result; use
content hashes to validate/reuse completed extraction when required. Replace
all segments for a source revision atomically. Reconcile deletes, renames,
excluded folders, changed roots and watcher overflow. Path-derived candidate IDs
can change on rename; stale IDs must never remain launchable.

Use token-aware text chunks with overlap; preserve document headings and table
headers. Render PDFs and Office pages/slides separately so diagrams and scans
are indexed visually. Spreadsheet extraction must include numeric/boolean
cells, cached formula results, shared and inline strings, merged headings and
sheet context; missing cached formulas are labelled, not recalculated.

Decode audio in bounded overlapping windows to mono 16 kHz. Index video in
timestamped windows with bounded frame sampling, plus audio when present. Start
with separate visual/audio segments; test interleaved representations for
events requiring both. Measure processor token budgets for each window rather
than submitting an entire long file. Do not silently truncate input. Record
sampling and partial coverage so short events missed by sampling can be
measured and explained.

Set bounded concurrency, pixel counts, decoded duration, archive expansion,
pages, tokens, evidence length and per-file processing time. A timeout or bad
file must not stall the queue or UI. Pause cleanly on index quota, low disk or
memory pressure. Start with one extraction/embedding job at a time and increase
concurrency only after measurement.

### Retrieval and result presentation

Maintain a reusable vector matrix per active index generation in the worker.
Begin with batched exact cosine search for a bounded corpus; benchmark at
25,000, 100,000 and 500,000 segments. Adopt an approximate index only if exact
search misses the latency/memory targets. SQLite remains the authoritative
store, and any secondary index must recover after interrupted updates.

Parse requests using Scout's existing intent parser. Apply folder, type, scope
and time constraints to the eligible retrieval set before top-k selection.
Revalidate scope and source revision before displaying or opening a hit.
Return only known candidate IDs and locators; the worker cannot generate launch
targets or system commands.

Fuse semantic and lexical ranked lists, initially using reciprocal rank fusion.
Group segment matches by file, cap contributions from repeated segments, and
show the strongest evidence. A long document must not win merely by having more
chunks. Develop admission rules using positive, ambiguous and unsupported
content queries, with separate analysis by modality. Cosine scores and fusion
scores are not Laya probabilities and must not enter its probability fields.

Preserve literal-name priority, explicit chronological ordering, group limits,
manual selection and explicit execution. Semantic hits work without Laya. If
Laya is enabled, keep document bodies/media out of its requests and retain
semantic evidence through its refinement rather than requiring filename-only
endorsement for every content hit.

Render ordinary results immediately, debounce embedding queries, cache short
query results by index/configuration generation, and suppress superseded
responses. Evidence labels identify content matches and open the matching
page/section/time inside Scout. Show progress and partial coverage in Settings,
without dumping implementation details into normal search results.

### Privacy and removal

Content is processed only within explicitly selected local folders. Enforce
existing root/exclusion policies and skip reparse points and offline cloud
placeholders. Decoders/converters must not fetch external document resources.
Run extraction with bounded resources, macros disabled and no shell-built
commands; pass executable arguments structurally.

Revoking a folder or disabling semantic search immediately excludes its results,
cancels pending work and purges indexed content, vectors and derived caches.
Removing the add-on additionally removes its dedicated runtime/model files.
Preserve the ordinary file library and unrelated Laya/keyword-search state.
Purge must account for SQLite journals and secondary indexes, without promising
physical erasure on every storage device.

Update the current README privacy wording: opted-in content will be supplied to
the local embedding worker, while Laya/Qwen continue to receive no document
bodies. Keep private fixtures, paths, extracted content, weights, runtimes and
diagnostic reports under ignored local directories.

## Delivery milestones

| Milestone | Work and dependencies | Completion check |
| --- | --- | --- |
| 1 Runtime and format spike | Pin the full model; verify text, chart/image, speech, sound and video embeddings on Windows CPU and available CUDA hardware; test Office rendering/distribution and media decoders | Reproducible all-modality probe, dependency manifest, licensing notes and measured resource/package report; resolve renderer compatibility before dependent work |
| 2 Add-on host and settings | Build independent setup, host, install states, offline operation, cancel/retry, pause and removal | Interrupted install/restart/offline tests; Scout starts and searches with add-on absent; Laya disabled path works |
| 3 Index and queue | Add segment schema, durable jobs, scope generations, atomic replacement, quotas and crash recovery | Delete/rename/change/revocation tests; no stale or excluded results; resumed jobs are idempotent |
| 4 Document and image readers | Dedicated structured text/table readers, PDF and Office rendering, images/OCR and local evidence | Fixture coverage for scans, charts, numeric/formula cells and poor filenames; no preview truncation treated as complete indexing |
| 5 Audio and video readers | Bounded decoding, temporal segmentation, frame/audio embeddings and coverage reporting | Long-file, silent/video-only, speech/sound and timestamp tests; cancellation cleans processes and temporary files |
| 6 Retrieval and previews | Constrained vector retrieval, lexical fusion, abstention, grouped evidence and locator navigation | Final content hits with Laya off/on; exact/date/action/group behavior preserved; no stale responses |
| 7 Resource and quality evaluation | Fresh content-grounded holdout, modality ablations, dimension/quantization comparisons and Windows laptop measurements | Meets release targets below with every required content family enabled |
| 8 Packaging and release preparation | Ship setup entry points and worker scripts; update user/privacy/format/hardware docs, notices and installer flows | Clean per-user installation, update/removal and packaged WPF smoke tests; model weights remain outside base installer |

Land each verified milestone as a focused local commit on this branch. A milestone
does not enable the unfinished feature by default. Do not merge or publish a
release as part of writing this plan.

## Hardware and release targets

Target Windows 11 x64 with 16 GB RAM for the first full-modality CPU validation;
also measure a 32 GB machine and the available NVIDIA GPU. CPU-only support is
a release goal. Advertise minimum RAM/VRAM, supported CPU instructions and disk
requirements only after measuring the exact shipped artifacts. Include decoding,
Office conversion, tokenizer, runtime and download-cache peaks in those figures.

The full model's raw FP32 weights are approximately 2.96 GB by parameter-count
arithmetic; this excludes process overhead and temporary content. Index storage
depends on segments, not files: 100,000 float32 vectors at 768 dimensions require
about 293 MiB before evidence and database/index overhead. A long video can
generate many segments. Set an initial 2 GiB semantic-index quota with explicit
pause/expansion controls, then validate it against representative libraries.

Proposed engineering gates, to be fixed before opening the holdout:

- At least 95% Recall@20 and 90% correct first-file results on unambiguous
  positive tasks in each required content family; no more than 5% false
  admissions on unsupported tasks in each family. Report counts and task
  families, not just an aggregate percentage.
- No failures in exact-name, folder/type/time constraints, revocation, explicit
  execution, retained selection or stale-response tests.
- Ordinary search p95 regression no greater than 20 ms versus the saved baseline;
  warm semantic results p95 at most 1 second including query embedding and
  retrieval at 100,000 segments on the target CPU laptop. Separate debounce,
  cold activation and UI timings in the report.
- Cold ready time at most 20 seconds on that laptop; embedding worker and its
  extractor children together peak below 6 GiB with Laya disabled. Measure the
  combined optional stack separately so concurrent Laya does not hide costs.
- Idle unloaded state has no model/decoder processes; paused indexing starts no
  new extraction; active query traffic can preempt between bounded jobs.
- Publish measured initial indexing throughput by format, battery impact,
  installed/download/temporary disk peaks, and index growth. No fixed indexing
  completion promise for arbitrary personal libraries.

If a gate fails, optimize or retain experimental status. A CPU footprint or
quality failure does not justify silently dropping audio/video or substituting
a text-only release.

## Verification strategy

Extend the existing task benchmark for content segments, page/cell/time locators,
scope-specific queries and final UI outcomes. Freeze independent development
and holdout task families before model/policy tuning. Use at least 400 holdout
tasks across documents/code, spreadsheets/presentations, images, audio and video,
including close distractors, unsupported queries and ambiguous descriptions.
Keep related source files, paraphrases and derived media in one split. Ground
labels in actual fixture contents, not filename guesses; use authored/licensed
fixtures and avoid reading a personal library for test construction.

Compare current keyword content search, semantic retrieval alone and combined
retrieval, then optional Laya refinement. Score final file selection, locator
accuracy, coverage, abstention and latency separately. Test multilingual tasks
as an additional slice; the initial quality claim should name the languages
actually validated.

Add C# index/retrieval/lifecycle contracts, Python embedding/decoder contracts,
authenticated HTTP tests, concurrent update/revocation tests and packaged WPF
checks. Exercise corrupt files, zip expansion, missing codecs/OCR languages,
unsupported Office rendering, GPU OOM, worker crashes and offline startup.
Test clean setup/update/removal under a standard Windows account.

Use `scripts/build.ps1` for the existing Release build and C# checks. Extend
packaging to include the new setup script and source runtime files, and add
dedicated semantic smoke/evaluation commands once their interfaces exist.
Keep all large or private evaluation outputs under ignored
`artifacts/diagnostics/multimodal/` and model/runtime downloads outside Git.

## First implementation task

Start milestone 1 with a pinned full-model probe and a small public/authored
fixture pack spanning every modality. Produce the compatibility and footprint
report, choose the Office renderer distribution, and finalize the runtime lock
before building permanent settings or installing the add-on into a normal Scout
user profile. This resolves the two largest dependencies: a viable Windows
multimodal runtime and faithful Office content rendering.
