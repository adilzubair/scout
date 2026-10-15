# Scout multimodal semantic search implementation plan

Date: 2026-10-15. Branch: `feature/multimodal-semantic-search`.
Status: implementation active; resumed on 2026-10-15 from checkpoint `909bb60`.
Retrieval, previews, OCR and format coverage are committed. The frozen evaluation
foundation passes 323 Release checks; independent quality and release gates remain open.
This plan is the implementation checklist; the add-on remains a
preview until the release gates below pass. The full-model runtime and content smoke probe
are implemented and verified on CPU and CUDA. On 2026-10-14 the user directed
implementation to proceed without LibreOffice. The earlier renderer spike is
historical evidence, not a shipping dependency. Office content will use dedicated
Open XML/OpenDocument readers for text, tables, chart data and embedded images.
Exact Office layout reproduction is outside this path; PDF page rendering,
standalone images, audio and video remain required.
The independent add-on setup, host and settings preview is implemented and
verified. The separate SQLite segment index and durable queue now pass crash,
source-change, revocation, atomic replacement and quota contracts. The app now
connects selected-folder reconciliation and background indexing to that queue.
Native source acquisition and renderer-free
Office/PDF/image/CSV reader foundations are implemented; all six Office formats
pass actual no-network AppContainer extraction and CPU embedding. Bounded audio/video
readers now pass real-codec sandbox extraction and full-model CPU/CUDA embedding.
A trusted extractor bridge validates receipts/payloads and source identity before
durable publication; all seven families pass its real CPU pipeline smoke.
Sandbox permission journals recover after owner crashes, and shared runtime
leases interlock with setup/removal. Scheduler/progress integration passes real
all-family CPU indexing and watcher/maintenance/revocation checks. Constrained
retrieval, lexical fusion and locator previews now pass engineering contracts,
real CPU code/image queries and authored WPF playback checks.
Bounded Windows OCR now adds scanned text while preserving vision and page locators;
its authored CPU pipeline smoke passes. Calibrated quality,
clean standard-account installation, further parser lifecycle hardening,
distribution review and release
evaluations remain required. See
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
| Documents | PDF, DOCX, ODT, TXT and Markdown; PDF text and rendered pages; Office paragraphs, tables and embedded images | PDF page or document section, excerpt or embedded-image thumbnail |
| Presentations | PPTX and ODP; slide text, shape labels, chart data and embedded images | Slide number with text/data or image evidence |
| Spreadsheets | XLSX, ODS and CSV; cell values, headers, table relationships and chart data | Sheet name and cell range or chart data |
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
- No Office suite, automation or headless converter is installed or invoked.
  Read Open XML/OpenDocument containers directly, with bounded ZIP/XML processing.
  Preserve cached numeric/boolean/formula values, merges, sheet/slide context,
  chart series and embedded images. Never calculate formulas or fetch links.
  Label uncached formulas and unsupported objects explicitly. Office evidence
  previews show extracted text/data/images; they do not promise exact pagination,
  shape geometry, font layout or visual chart styling. Visual queries requiring
  an unsupported Office layout must abstain. PDF and image vision remain enabled.
- Windows image decoding and local OCR where available; define missing OCR
  language handling. Vision embeddings remain usable without OCR.
- Bounded audio/video decoding through the already pinned PyAV 19 wheel and its
  bundled FFmpeg libraries, without installing a separate FFmpeg executable.
  Validate the actual codecs, timestamp handling and Windows sandbox behavior.
  Record the wheel's actual build configuration and corresponding source/notices;
  optional components can change its license. Complete that review before packaging.
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
headers. Render PDF pages and decode embedded Office images for visual indexing.
Read Office chart series/labels and diagram text directly; label visual layout
that cannot be represented by these readers. Spreadsheet extraction must include numeric/boolean
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
| 1 Runtime and format spike | Pin the full model; verify text, chart/image, speech, sound and video embeddings on Windows CPU and available CUDA hardware; test media decoders | Reproducible all-modality probe, dependency manifest, licensing notes and measured resource/package report |
| 2 Add-on host and settings | Build independent setup, host, install states, offline operation, cancel/retry, pause and removal | Interrupted install/restart/offline tests; Scout starts and searches with add-on absent; Laya disabled path works |
| 3 Index and queue | Add segment schema, durable jobs, scope generations, atomic replacement, quotas and crash recovery | Delete/rename/change/revocation tests; no stale or excluded results; resumed jobs are idempotent |
| 4 Document and image readers | Structured Office text/table/chart/image readers, PDF rendering, images/OCR and local evidence | Fixture coverage for scans, chart data, embedded images, numeric/formula cells and poor filenames; explicit unsupported layout and partial coverage |
| 5 Audio and video readers | Bounded decoding, temporal segmentation, frame/audio embeddings and coverage reporting | Long-file, silent/video-only, speech/sound and timestamp tests; cancellation cleans processes and temporary files |
| 6 Retrieval and previews | Constrained vector retrieval, lexical fusion, abstention, grouped evidence and locator navigation | Final content hits with Laya off/on; exact/date/action/group behavior preserved; no stale responses |
| 7 Resource and quality evaluation | Fresh content-grounded holdout, modality ablations, dimension/quantization comparisons and Windows laptop measurements | Meets release targets below with every required content family enabled |
| 8 Packaging and release preparation | Ship setup entry points and worker scripts; update user/privacy/format/hardware docs, notices and installer flows | Clean per-user installation, update/removal and packaged WPF smoke tests; model weights remain outside base installer |

Land each verified milestone as a focused local commit on this branch. A milestone
does not enable the unfinished feature by default. Do not merge or publish a
release as part of writing this plan.

### Restart checkpoint and execution order

The existing foundations are committed and verified; their integration and
release checks remain open. Do not restart completed spikes or treat their smoke
results as end-to-end release evidence.

| Area | Verified checkpoint | Remaining work |
| --- | --- | --- |
| Runtime | Full-model CPU/CUDA and all-encoder prepared-input probes; pinned artifacts (`c9febee`) | Exact shipped-resource, codec distribution and minimum-hardware validation |
| Add-on lifecycle | Atomic process ownership; seeded cancellation/retry/removal; real upstream install/removal; published-package offline environment-revision failure/retry/rollback/reactivation with private query/parser and prior-file hash preservation | Changed-dependency/device upgrades, clean-account setup, real volume exhaustion and obsolete-generation reclamation |
| Durable index | SQLite queue and scheduler; published-library CPU owner-death/resume across 16 files, seven families and 27 segments (`909bb60`) | Broader source distributions, UI/ordinary-search timing and resource/quality gates |
| Source acquisition | Native identity checks and hashed owned snapshots (`f87fb8b`) | Revalidate source and scope across extraction, embedding, display and launch |
| Extractor bridge | Receipt/hash/payload validation, source rechecks, resource guards and progress; parser job assigned at creation and live native decoder owner-death/recovery (`909bb60`) | Upgrade/failure scenarios and full resource/quality evaluation |
| Document/image readers | Six Office formats with sandbox-backed WPF locator previews; bounded OCR; explicit unsupported/encrypted/corrupt statuses and scoped coverage review | Independent content/language quality and additional fidelity cases exposed by evaluation |
| Audio/video readers | Bounded PyAV readers; separate and synchronized AV windows; CPU/CUDA joint-input compatibility; live decoder owner-death/recovery | Independent natural audiovisual quality, broader cancellation/upgrade scenarios and resource gates |
| Retrieval/UI | Constrained retrieval and preview engineering contracts, real CPU code/image queries, retained selection/revoked actions, and all eight required media extensions in WPF | Independent quality/admission evaluation, richer locator/format fixtures and packaged lifecycle validation |
| Release validation | Frozen quality protocol/development pilot and production-adapter replay; grouped-source 25K/100K/500K scale baseline with uncached CPU queries | Independent 400-task holdout, abstention calibration, Laya on/off, broader source distributions/UI timing, throughput/resources, physical minimum hardware and clean packaging/install |

Proceed in this order, committing each verified unit:

1. Reader foundation completed after restart: audio/video file readers use the
   pinned PyAV runtime. Real codec fixtures, overlapping audio windows, timestamps,
   video-only/silent input, frame/pixel/duration/output limits and no-network
   AppContainer execution pass. All 22 segments from 16 authored smoke files pass
   full-model CPU and CUDA embedding. This is compatibility evidence, not a
   retrieval-quality or minimum-hardware result.
2. Extractor/scheduler foundation completed: selected-folder reconciliation,
   snapshot receipts/hashes, cancellation, immediate scope revocation,
   pause/quota/resource behavior, interactive priority and Settings progress pass
   without holding the metadata refresh gate. Packaged forced-exit/upgrade cases
   remain in the release checklist.
3. Retrieval/preview foundation completed: constraints precede ranking, native
   identity is revalidated, and lexical fusion retains dates/actions, Laya evidence,
   grouping and manual selection. Authored WPF media playback advances for
   WAV/MP3/M4A/FLAC/MP4/MOV/MKV/WebM; WebM uses a bounded sandboxed clip.
   Real process-death tests recover abandoned clips and preserve active owners.
   These checks do not calibrate admission or establish final quality.
4. Document/image coverage foundation completed: PDF scans, bounded Windows OCR,
   unsupported/encrypted/corrupt statuses and scoped coverage review pass. Keep
   independent fidelity/language evaluation and packaged parser crash cleanup open.
5. Empty-root upstream CPU setup recovery now passes using
   the published application and its bundled CLR. Verify the private interpreter,
   all five prepared-input paths (text, image, audio, video and joint AV), an actual
   query, notices and preservation of ordinary storage. Record inherited cache/
   interpreter overrides and retain downloads on failure. The real download case
   exposed uv's embedded mutable interpreter alias; the standard CPython venv
   launcher now pins the patch interpreter, preserving unpublished retry caches.
   Its actual query and sandboxed parser/embedding checks pass. This current-account
   test does not discharge clean-account installation or minimum hardware gates.
6. Storage-maintenance native validation passes for cache/unfinished-generation
   reclamation, prepared preview preservation, live-worker exclusion and retained
   offline-package rollback/reactivation. Continue changed-dependency/device
   upgrades and real disk-full recovery. Cleanup now
   retains the active and recent completed generations, rejects links, excludes
   live runtime leases and preserves sandbox recovery journals. An optional
   settings action also clears private download caches; uv package installs use
   explicit hardlinks. Environment revision 2 now pins the
   standard venv backend; a real offline upgrade between the archived prior and
   current published packages passes native-audit rejection, prior-package query,
   retry, exact-marker rollback and current-generation reactivation. Every prior
   venv/model file hash is preserved. The separate first-attempt empty-root install/removal
   run now passes with the actual generated cache, all encoder readiness paths,
   CPU query and sandboxed parsing. Preserve a usable prior
   generation and ordinary search; reject links escaping the owned add-on tree.
   Model and parser process ownership at creation, actual native-decoder owner
   death/recovery and seeded offline setup cancellation/retry/removal already pass
   (`9831a31`, `909bb60`); repeat them only when relevant code changes.
7. Collect a diverse, licensed natural-media candidate pool with
   original checksums, provenance, bounded downloads and interruption recovery.
   Review actual content, license obligations and related-source groups before
   assigning splits or labels. Provider metadata alone never approves a holdout.
   For audio, assess [FSD50K's published human annotations](https://zenodo.org/records/4060432)
   as independently grounded event labels. Retain both dataset and per-clip
   attribution; exclude restrictive clip licenses from the candidate subset.
   Clip-level labels support positive event presence, not an assumption that
   unlisted events are absent or that precise time boundaries are known.
8. Freeze at least 400 independent content-grounded holdout tasks across all seven
   families, including natural sound and joint audiovisual queries, before tuning
   admission. Preserve the opened development pilot as regression evidence.
   Run file/locator/abstention and dimension comparisons, then fix measured failures
   and validate with a fresh unopened holdout when necessary. Joint AV inputs now
   consume both encoders on CPU/CUDA and change when either channel changes;
   independently grounded sound-plus-visual retrieval still needs evaluation.
9. Extend the completed development-machine 25K/100K/500K baseline with CPU/GPU, ordinary-search regression,
   throughput, memory, battery, disk and lifecycle benchmarks. Validate the physical
   16 GB CPU laptop separately from the available 32 GB development machine.
   The user confirmed a physical Windows 11 16 GB PC is available and will run
   validation later. The portable validation bundle described
   in `MULTIMODAL_HARDWARE_VALIDATION.md` has passed its authored all-family,
   preview and 100K engineering workload on the 32 GB development machine. Retain the hardware gate until actual
   results are returned and reviewed. No further availability confirmation is needed.
   The development baseline's process-cold first query is 17.57 s and 100K
   semantic p95 is 545.98 ms. Windows whole-job peak commit is 7.37 GiB for
   all-family validation and 6.54 GiB for scale, including the test app and nested
   model/parser processes. These conservative totals do not establish the <6 GiB
   worker/extractor gate. Profile process contributions and reduce memory before
   declaring the resource budget met. Keep raw measurements and the known failed
   long-path/disk-reserve rehearsals; an engineering exit code is not release approval.
   Native-pool profiling has since reduced the same packaged workloads to 5.68 GiB
   (all-family) and 5.28 GiB (100K), including the test app, with CPU FP32 and all
   encoders retained. Set BLAS/OpenMP limits before imports and use one native
   thread in parsers. Six authored embedding outputs stayed byte-identical in the
   paired probe. The updated replay's p95 is 928.34 ms, but its process-cold first
   query is 20.0023 s, above the 20 s target. Preserve that miss; investigate cold
   startup and representative maximum-window resource cases before closing these
   gates. The physical 16 GB result and broader Laya/battery/UI work remain open.
   Full-window testing then exposed a 6.27 GiB combined peak with the earlier
   policy. The updated policy uses one preprocessing BLAS thread and immediate
   decommit of unused Windows CPU allocator pages. The actual 21-file delivery
   replay, including 2048-token text, 1 MP imagery, 12 s audio and 8-frame joint AV,
   passes at 5.83 GiB (validation) and 5.06 GiB (100K); p95 is 634.88 ms. All six
   paired full-window embeddings stayed byte-identical. Three independent
   published-CLR cold-start trials pass, and the integrated portable workflow's
   trials are 10.72/10.46/10.05 s. Keep historical misses and the prior p95 sample
   taken with concurrent profiling; do not use that sample as isolated latency
   evidence. The physical 16 GB, concurrent UI/index/preview, Laya-on, battery,
   larger source distributions and independent quality/distribution gates remain.
10. Finish distribution obligations and clean standard-account install/update/removal
   checks, rebuild the package and run packaged WPF checks. Physical 16 GB CPU
   hardware validation is still outstanding; a memory cap on the available 32 GB
   machine is not a substitute. Retained package notices are implemented, while
   exact native codec/source correspondence and distribution approval remain open.
   Report any failed or unavailable gate explicitly.

The user-authorized restart does not change the all-content scope or relax the
release targets. No LibreOffice installation, renderer troubleshooting or Office
automation is part of the remaining work.

## Hardware and release targets

Target Windows 11 x64 with 16 GB RAM for the first full-modality CPU validation;
also measure a 32 GB machine and the available NVIDIA GPU. CPU-only support is
a release goal. Advertise minimum RAM/VRAM, supported CPU instructions and disk
requirements only after measuring the exact shipped artifacts. Include decoding,
structured readers, tokenizer, runtime and download-cache peaks in those figures.

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
unsupported Office objects/layout, GPU OOM, worker crashes and offline startup.
Test clean setup/update/removal under a standard Windows account.

Use `scripts/build.ps1` for the existing Release build and C# checks. Extend
packaging to include the new setup script and source runtime files, and add
dedicated semantic smoke/evaluation commands once their interfaces exist.
Keep all large or private evaluation outputs under ignored
`artifacts/diagnostics/multimodal/` and model/runtime downloads outside Git.

## Next implementation task

Restart milestone completed: the base has an explicit 33-file runtime allowlist,
24 package contracts, a fresh self-contained ZIP and streamed verification of all
532 published file hashes. The published executable passes isolated WPF checks;
its source runtime passes offline CPU readiness for text, image, audio, video and
joint audiovisual input using the existing development generation. This does not
establish clean-account installation or independent retrieval quality.

Worker session ownership and recovery are now implemented. Four contracts cover
active owners, forced death, marker initialization and unknown/legacy content.
A private copy of the published libraries and self-contained CLR passed an actual
CPU forced-death/resume run: 16 authored files, seven families, 27 segments. The
worker died with its owner, an unpublished running job survived in SQLite, and
restart restored the index. The abandoned readiness/source work was removed while
an active session survived; four runtime-root ACLs matched their pre-run rules.
Semantic index removal preserved ordinary database bytes and original sources.
The final published WPF smoke checks also pass. These checks reuse the existing
development add-on and explicit private interpreter override, so they do not
establish clean-account setup or minimum hardware support.

Installer process ownership is now assigned by Windows during creation, before
its thread runs. Seven native launch contracts cover inherited handles, immediate
descendants, parent death, cancellation, Unicode/large output, failed-start
cleanup and Windows PowerShell module resolution. Two readiness-reader contracts
cover atomic-publication sharing, cancellation and size bounds. Three removal
contracts exercise real managed-Python junctions, dangling aliases and rejection
of an external target before deletion.

The published-library seeded CPU flow passes with a non-admin token and its own
private interpreter, without a development home override: interrupt actual model
verification, observe four setup processes exit, preserve both ready markers,
query the previous generation, retry verification/notices offline and remove the
private add-on while preserving ordinary database bytes. The initial failures
exposed inherited PowerShell 7 module paths and uv's internal minor-version
junction. Both production paths are corrected. The base runtime remains unchanged
by setup; Python bytecode writes into the application directory are disabled.
This does not establish a fresh internet install or a new-generation update.

Job-at-creation ownership now also covers the model launcher and AppContainer
parser. The parser's zero-capability token and existing 1 GiB process/2 GiB job/
16-process limits are installed before its thread runs; suspension only allows
the managed wrapper to acquire a stable process handle. The model host reuses
the owned launcher and discards output in bounded buffers.

A published-library live-parser test confirmed parent/child native PyAV decoding
with advancing frame counters, zero capabilities, private-venv activation,
source-read/runtime-write denial, network denial and a clean environment. Killing only its
owner terminated all three observed parser/launcher processes. The same profile
then executed a fresh isolated parser, restored runtime-root access rules and
removed its owned job while preserving an outside canary. The updated model
launcher also passed actual CPU owner-death/resume across 16 files, seven families
and 27 segments. The published WPF checks pass. These are process-lifetime and
engineering results, not quality or minimum-hardware approval.

Next, continue fresh-download/new-generation update and rollback,
interruption/disk-full and generation/cache cleanup checks, including generated
cache links, followed by the independent
content-grounded evaluation corpus. The first
full run timed out waiting for quota pause/resume (306/307); targeted scheduler
checks (16/16) and the complete rerun (307/307) passed. Preserve this unresolved
intermittent result and the improved timeout diagnostics for lifecycle work.
Keep the physical 16 GB machine and native distribution obligations as explicit
release gates; neither has been discharged by development-machine smoke tests.

The empty-root upstream-download case has since passed recovery with the published
application, bundled CLR, non-admin token and no cache/interpreter overrides.
It exposed uv's embedded minor-version launcher alias. Setup now creates or
refreshes an unpublished environment using the resolved patch interpreter's
standard venv launcher and verifies its bounded, unlinked home configuration.
Ready generations remain unchanged. All five readiness paths, notice integrity,
actual CPU query and sandboxed parsing/embedding pass; ordinary storage and source
bytes are preserved. A separate first-attempt empty-root install and complete
runtime/cache removal also passes under a non-admin token, without seeded artifacts
or inherited cache/interpreter overrides. Eight setup and eight notice contracts
pass. Continue the remaining new-generation/failure and obsolete-generation checks;
this result does not discharge clean-account, hardware, quality or distribution gates.

The actual environment-revision upgrade now passes with both published packages
and their bundled CLR. The deliberately rejected private audit policy leaves the
prior marker intact; the prior package remains query/parser capable. Offline retry
reuses only the fully verified pinned model, activates a distinct generation, then
supports exact-marker rollback and current reactivation. Every prior venv/model
file hash and ordinary/source bytes remain unchanged. Nine setup and seven reuse
contracts pass, including nested Windows process diagnostics and injected disk-full
copy/publication failures. Those injected errors are not real-volume exhaustion.
Explicit Git line endings preserve all four runtime identity files with autocrlf
on and off. Continue obsolete-generation/cache reclamation and changed-dependency/
device upgrades; retain the clean-account, physical hardware, quality and native
distribution gates.

Distribution audit now retains 71 installed package notice inventories, interpreter
terms and pinned supplemental OCR/tokenizer/model license texts. Eight notice and
five setup contracts cover path/integrity limits, Windows RECORD separators and
publication rollback. The exact PyAV wheel has 23 DLL references including x264
and x265; a publisher patch changes FFmpeg's configure license classification.
Do not accept the runtime's reported LGPL string as distribution approval. Exact
source correspondence and the remaining codec/toolchain obligations are still
open. Continue base-package validation while preserving this release gate.

The first grouped-source scale run is complete on the available 32 GB development
machine: warm semantic p95 including uncached CPU query embedding is 393/471/893 ms
at 25K/100K/500K segments. The workload uses 250 synthetic segments per real
generated local source, so file counts are 100/400/2,000. Initial worker/matrix/query
activation is 15.5/18.0/33.5 seconds. Raw samples, source/binary identity and hardware
metadata are retained; generated indexes were removed. Do not generalize these
numbers to the physical 16 GB CPU target, different file distributions, WPF UI or
ordinary-search regression. Keep the remaining hardware/resource gates open.

Joint audiovisual prepared windows now preserve separate sound/frame vectors and
add synchronized representations only where audio covers the whole frame window.
CPU/CUDA encoder hooks and channel-change probes pass; protocol and locator
duration checks reject mismatched combined payloads. This is engineering evidence,
not proof of audiovisual retrieval quality. Keep natural joint-event tasks in the
independent holdout and measure the added indexing/resource cost.

Intake and content-review export foundations are complete. Fourteen offline intake
contracts pass; the production sandbox exports verified media without inference or
label approval. Direct private-interpreter startup passed parent/child isolation,
venv/package checks and the eight-source export after two launcher-path timeouts.
The first natural audio exposed 147 tiny windows in an 88-second recording.
Reported timing alignment up to 20 ms now yields nine overlapping windows with
exactly preserved reconstructed PCM16 samples. Seventeen media contracts and the
same real sandboxed source pass; larger gaps and accumulated drift remain visible.
Seven photos have visual observations linked to source/asset hashes; auditory,
license and related-group review are still pending. Continue these reviews and
the remaining natural-media intake without running retrieval predictions.
The current discovery has 92 public candidates; 71 pass initial license-label,
container, size and original-checksum filters. These are unreviewed candidates,
not approved licenses, source groups or content labels. The first transfer stopped
on HTTP 429 after two originals. A bounded resume verified seven photos and one
audio file before another provider limit; the verified subset is now preserved in
an eight-source intake manifest using offline-only verification. Do not retry the
remaining transfers in a tight loop. Reuse only files whose original bytes/hash
still match. Keep sources and attribution manifests under ignored diagnostics.

Then freeze the independent content-grounded evaluation corpus and at least 400
holdout tasks before changing admission/ranking policy. The evaluator foundation
now freezes hashes, checks source/group split integrity, scores file/locator and
abstention results, compares dimensions and verifies production-adapter replay.
Keep related sources, derivatives and paraphrases in one split and exclude the
engineering smoke fixtures from quality claims. Exercise every required family,
close distractors, negatives and ambiguous descriptions with Laya off/on.

The fourteen-source, seven-group, twenty-one-task development pilot is frozen
separately from the future holdout. Its actual CPU pipeline finds seven positive
first files/locators and admits all seven negative queries at the provisional
0.35 threshold across 768/512/256 dimensions. This is a concrete abstention failure,
not a quality pass. Keep the threshold unchanged until independent holdout tasks
are frozen. Natural photos, environmental audio, real video, joint AV, multilingual
and larger-source slices need independent content/label review; authored toy
media and synthetic speech alone cannot discharge these gates. Laya on/off,
large-library latency, physical 16 GB CPU and packaged lifecycle checks remain open.

The format audit now has 296 Release checks, nine actual sandbox format-status
fixtures and all six Office evidence previews in WPF. Unsupported legacy formats
and archive names are accounted for without reading their bodies; encrypted and
malformed content has explicit terminal reasons. Sparse spreadsheet locators and
transparent-image preparation were corrected. These are engineering checks,
not independent quality proof. English (US) OCR is smoke-tested; Arabic is not
installed and has no recognition-quality evidence. Packaged forced-death recovery,
resource/hardware measurements and distribution/install checks remain open.
The add-on is not release ready.
