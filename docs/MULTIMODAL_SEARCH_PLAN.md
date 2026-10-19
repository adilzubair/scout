# Scout multimodal semantic search implementation plan

Date: 2026-10-15. Branch: `feature/multimodal-semantic-search`.
Status: internal-preview handoff complete; production release work deferred.

2026-10-19 scheduling override: physical 16 GB testing is post-release work at
the user's direction. It no longer blocks the beta merge/release. Keep minimum
hardware claims provisional, and retain the other quality, lifecycle and
distribution checks. Historical pre-release hardware gates below describe the
earlier plan and are superseded only for this scheduling decision.

2026-10-19 expanded native E2E results and the fixed fresh Laya installation are
recorded in [the current E2E report](E2E_TESTING_2026_10_19.md). Current packaging
validation is blocked by local antivirus; the historical package passes below
do not supersede that block or close the remaining release gates.

The immediate-reopen folder-persistence bug is reproduced and fixed in `2f5173a`.
Saving no longer waits for a full index reconfiguration: Settings writes validated
preferences before closing and retains edits if writing fails. Real WPF tests cover
immediate reopen with indexing held pending, overlapping saves, a second-process
restart, index failure, validation, Cancel and storage failure. The wider UI/
preview suite and the actual seven-family scheduler/query smoke pass; all 351
native regressions and 24 package contracts pass. See
[app end-to-end testing](APP_END_TO_END_TESTING.md). The new delivery is
`artifacts/Scout-multimodal-preview-e2e-fixed.zip`; earlier previews remain kept.
The full UI run exposed a native PDF/graphics process-detach crash even after its
assertions completed. `f53e3d6` isolates PDF rendering in a bounded owned worker.
Navigation, cancellation, malformed inputs and temporary-file cleanup pass, and
the runner now checks actual process exit codes. The failed intermediate
settings-only package is retained as evidence and is superseded by this delivery.

The subsequent interactive folder-selection crash is fixed in `f5f0b9b`.
Windows events identify unregistered COM activation in the hand-written picker.
WPF's built-in folder dialog replaces that interop, and a picker failure now
leaves settings open with manual-path guidance. The targeted real-dialog WPF
regression verifies select/repeat/cancel and failure recovery; all 351 native
checks and 24 package contracts pass. The replacement self-contained preview is
`artifacts/Scout-multimodal-preview.zip`; the prior hardware ZIP remains retained.

The user approved changing the finish line to ready for internal testing, with
at most 60 minutes of remaining work from the 2026-10-15 scope change. The revised
objective is to hand over the existing optional, LibreOffice-free, seven-family
preview with its validated ZIP, usage instructions, commits and known issues.
Only demonstrated installation/indexing/search/evidence blockers justify fixes
in this finalization. The later reported folder-picker blocker is corrected
above. Package inventory, runtime sources and checksum verification are retained;
see [the internal-preview handoff](MULTIMODAL_INTERNAL_PREVIEW.md).

The internal-preview objective is complete. Stop autonomous goal work after
handoff. The extensive quality benchmark, physical 16 GB run, distribution review,
remaining lifecycle/UI/resource checks and the original production acceptance
criteria below are deferred follow-ups. They do not block this internal preview
and are not waived or represented as passed. Preserve unfinished corpus work;
do not expand it as a prerequisite to delivery.

Retrieval, previews, OCR and format coverage are committed. The frozen evaluation
foundation passes 351 Release checks; independent quality and release gates remain open.
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

The next quality slice adds a pinned ESC-10 natural-audio development diagnostic;
see `docs/MULTIMODAL_QUALITY_EVALUATION.md`. Twenty unique original clips from ten
classes have human-authored upstream presence labels and retained per-clip
attribution. Ten ambiguous queries accept both known class matches. The inputs
were frozen before inference; all sources and metadata reproduce byte-for-byte
offline, with the Git tree and blob membership verified. Nine offline provenance
contracts pass. These are development-only sources, not the independent holdout.
There are no negative or precise-event-timing labels. Keep their original source
identities and derivatives out of the future holdout. Physical 16 GB validation
is pending the user's later run; do not ask for that hardware again.
The first actual CPU run indexed all twenty sources but returned no files for
all ten queries at every dimension. There were no adapter disagreements. The
parser treated generic "recording" as video and excluded all audio candidates
before scoring. That scope bug is corrected: generic recordings include audio
and video, while explicit types retain their constraints. The unchanged frozen
development rerun returns an acceptable first file for 7/10 class queries at all
three dimensions, with no production-adapter disagreement. All twenty files are
admitted for every query, so recall at twenty does not establish selectivity.
Keep both reports, the unchanged 0.35 floor and the remaining quality failures.
This is not an opened release holdout. All 336 regression checks and 24 package
contracts pass after the correction.

Forty COCO validation images and 200 upstream human captions are now preserved as
unopened holdout candidates, with pinned archive/terms, per-image ETag and SHA256,
and current Flickr author/license evidence. All forty passed the production
no-network review export without model inference. The prepared images and all
captions were reviewed, yielding forty content-grounded draft queries. Corrections
and contradictory captions are retained; none is an approved holdout task yet.
Current metadata matches CC BY for 31 originals; two have license conflicts and
seven have unresolved canonical originals. Exclude those nine pending review.
The 31 remaining provisional candidates still need rights and full-corpus related
source review. Same-author/photo grouping yields 38 provisional groups for the
whole batch. Do not count 200 captions as 200 independent tasks or derive negatives
from omitted captions/object classes. Actual intake/attribution replay passed
offline, alongside thirteen Python contracts and 337 regression checks. Continue
the all-family corpus, resolve source/group review, author supported negatives,
then freeze at least 400 tasks before changing admission or opening holdout
predictions. The 0.35 floor and all release gates remain unchanged.

The document/Office candidate intake now pins original public record metadata,
file checksums, attribution and concept/version relationships. Eleven originals
from four related-source groups (PPTX/ODP, PDF/DOCX and XLSX/CSV) reproduce offline.
Ten source-intake contracts pass. The all-family no-model review exporter now
records unavailable sources without approving them or dropping later files.
The real corpus exposed lost sheet context at token boundaries, a Windows
snapshot-cleanup sharing race and a PDF import stall through CPython's WMI-based
OS-name detection. Bounded context/cell-aware chunks, exact emitted-token checks,
owned deferred cleanup and a scoped Windows PDF import workaround correct these
cases without changing sandbox capabilities, parser deadlines or model artifacts.
The final actual review prepares all eleven sources into 1,288 verified segments;
all 1,014 text segments stay within 2,048 raw tokens. Failed and intermediate runs
remain preserved. Both PDFs complete without a partial flag; Office/OCR/large-sheet
limits remain explicit. Twenty-eight source-part-grounded draft questions remain
unapproved, with no model predictions or splits. Source rights, whole-corpus
ambiguity/group review, merged/multirow header fidelity, supported negatives and
the frozen 400-task holdout remain next. All 340 regression, 24 package and 21
reader checks pass. The full-model CPU/CUDA, packaged resource, physical 16 GB,
remaining lifecycle and distribution gates retain their existing scope.

The exact packaged reader-fix rehearsal completed all 21 authored fixtures across
seven families, bounded video preview and the 100,000-segment workload. Whole-job
peak commit was 6,248,288,256 bytes for extraction and 5,424,603,136 bytes for scale,
both below 6 GiB. Performance targets did not pass: three new-process cold queries
took 24,214.2027, 22,668.7916 and 20,039.3698 ms (target 20,000 ms), and uncached
semantic search p95 was 1,099.034 ms, maximum 1,289.1309 ms (p95 target 1,000 ms).
Only 2,805,518,336 bytes of physical RAM were available at the first cold result;
memory pressure may contribute, but does not waive a failed target. Preserve this
run alongside earlier passing and failing measurements. The harness's workload
exit code is not performance approval. No physical 16 GB result has been received;
the user has that PC and will run the validation bundle later.
The separate offline CUDA worker smoke check also passes text, image, audio,
video and joint audiovisual inference with finite normalized 768-dimensional
vectors and outgoing Python sockets blocked. This uses authored engineering
inputs; no predictions have been opened on the new natural-content candidates.

The ODS reader now traverses standard header-row, grouped-row and row-list
containers, preserves real cell addresses across repeated/empty rows and repeats
declared multirow/merged header context. Nested groups inherit context without
leaking local headings to siblings. Leading rows without a declaration are
labelled as context. Header storage and traversal depth have explicit bounds;
oversized context is marked partial while original header cells are still emitted.
Four baseline failures now pass within 25 reader contracts. Four actual sandbox
contracts also pass, with twelve accepted text segments (maximum 1,515 raw tokens)
and explicit excessive-nesting rejection. These authored checks do not establish
natural retrieval quality. Reader-byte hashing invalidates old index generations.

The unopened ODS candidate pool adds 36 originals from nine concept/version groups
and reproduces offline. The production no-model review prepares 31 sources into
5,067 validated text segments (maximum 1,019 raw tokens), preserving all originals.
Five legitimate large `content.xml` parts exceed the current 8 MiB budget and are
explicitly unavailable; thirteen sources also reach the segment limit. These
failures remain recorded. No inspected in-budget original uses the row containers
covered by the authored tests. Large XML handling and XLSX table/merged-header
relationships remain reader-fidelity work, alongside the full seven-family corpus,
source rights, supported negatives, ambiguity/group review and 400-task holdout.
Physical 16 GB, performance, lifecycle and distribution gates remain open.

Large ODS XML now uses a complete, no-tree structural/DTD/CRC preflight followed
by bounded row streaming. The streamed `content.xml` ceiling is 64 MiB; other XML
parts remain at 8 MiB and archive/sandbox/output limits are unchanged. Depth,
global element count and live-row windows are explicit. Completed nodes are
discarded, malformed tails fail before any prefix can publish, and embedded
images keep their real containing-cell locators. All 35 reader contracts and
eight native sandbox cases pass. The actual C2 image bytes and 269 text segments
(maximum 1,515 raw tokens) are verified; this authored evidence is not quality or
minimum-hardware approval.

All 36 unchanged ODS/readme originals now prepare into 6,347 verified text
segments (maximum 1,568 raw tokens). Prior content/locator/asset multisets remain
identical for the earlier 31 successes; cached charts reorder in three files.
The original five rejections remain preserved. Eighteen sources still reach
the segment cap, and visual/embedded-object limits remain explicit. These
candidates remain unapproved and unsplit, with no embedding predictions opened.

The 341-check native suite includes a deterministic readiness sharing/cancellation
contract after a test-helper race caused an initial 339/340 result. A full-model
run stopped on a generic media parsing error after fourteen publications; the
sixteen-file private parser diagnostic and a later isolated seventeen-file CPU
integration passed. Preserve both outcomes and investigate the earlier failure
before release. Targeted source-race tests now assert callback entry and pass
without running the model workload again. XLSX table/merged-header fidelity,
whole-corpus rights/group/ambiguity review, supported negatives and the 400-task
holdout remain next, alongside the existing physical 16 GB, performance, lifecycle
and distribution gates. No threshold, model or dependency pins changed.

The XLSX reader now resolves declared table parts and keeps their names, ranges,
column schemas and actual declared header rows with later cell evidence. Separate
tables retain separate context; leading merged titles preserve their ranges.
Undeclared leading rows remain labelled context, with no inference of arbitrary
later headers or visual layout. Eight-column groups and explicit table/merge/header
bounds keep context controlled. Invalid metadata fails before inference, external
relationships remain unavailable and formulas remain inert. Plain sheets retain
the earlier whole-row batching. Reader-byte hashing invalidates old extraction
and index generations.

All 43 combined reader, eight actual-tokenizer, 341 native and 24 package checks
pass. Six authored XLSX sandbox cases include four accepted workbooks and two
pre-inference rejections. The accepted workbooks also pass the full CPU model
(15 text segments, maximum 1,289 raw tokens); the final adjustment affected only
plain-sheet batching, which those scoped inputs do not use. A final no-model
review of four existing original XLSX files prepares 545 segments, maximum 2,048
raw tokens. Independent checks locate every cached cell in two originals (744 and
1,359 cells), including a data dictionary with three declared tables. Two dense
workbooks remain segment-limited, representing 16,896/246,438 and 17,280/59,705
cells. Original hashes and owned cleanup pass. This is bounded extraction evidence;
the four sources remain unapproved, unsplit and unopened to model predictions.

Continue with cross-corpus rights/group/ambiguity review, independent seven-family
tasks, supported negatives and the frozen 400-task holdout. Arbitrary spreadsheet
layouts and large-sheet coverage remain limitations. The unexplained earlier
media failure, cold/p95 misses, remaining lifecycle checks and distribution
clearance remain open. The user will run the refreshed validation bundle on the
physical 16 GB PC later; no hardware approval is implied by this milestone.

Bundle finalization exposed two regenerated local media containers that differed
from the recorded resource baseline. The failed build is retained and the prior
delivered ZIP remains preserved. Expanded bundles now take all 21 originals from
the verified resource pack, checking sizes/hashes before copying and hashes again
afterwards. The real bundle succeeds with unchanged pinned inputs while both
different developer files remain untouched. Four new baseline/corruption/missing-
file/window-budget contracts pass; the native suite now passes 345 checks. This
harness correction does not resolve the separate earlier media extraction failure
or change any resource/quality target.

Retrieval now reuses structural scope decisions for repeated segments of the same
file/family within a single request. Ineligible catalog files return immediately;
the cache never crosses a query or scope generation. Native source-revision and
current database-evidence checks still run before each admitted file. The lookup
contract now uses twelve segments per file, checks the three-evidence bound and
rejects an edited source on a later cached query. All 345 native checks pass.
Fresh 100,000-segment/400-file CPU runs measured 623.331 ms p95 for the unchanged
package and 547.4044 ms for the initial scope-cache probe, with 27 unique model
queries and zero cache hits each. Host conditions vary; the older 1,099.034 ms
miss remains recorded, and these runs do not establish physical 16 GB or UI
approval. Per-evidence database-open overhead remains the next profiling target.

Each admitted file now loads its ranked evidence in one bounded SQLite operation
instead of opening a connection for each of up to three segments. The operation
retains ready-status/scope filtering, locator validation, revision/path checks,
partial coverage, thumbnails and input ranking order. Removed IDs are omitted;
no evidence cache survives a source generation. A batch cannot exceed three IDs.
Contracts cover ordered/missing/duplicate IDs, parameter binding, partial payloads,
changed/excluded/disabled sources and malformed stored locators. The final native
suite passes 348 checks, including the stronger repeated-segment lookup contract.

The batching probe's unchanged 100,000-segment/400-file workload measured
253.0177 ms semantic p95 (maximum 362.5127), 80.973 ms query-embedding p95 and
184.4877 ms adapter/fusion p95. All 27 model calls were unique, with no query-cache
hits. The unchanged-package and initial scope-cache probes measured 623.331 and
547.4044 ms respectively. These are current-host engineering observations with
varying load, not physical 16 GB or independent retrieval-quality approval. The
older cold/p95 misses remain retained; exact committed packaged resource replay
is the next verification step. No admission threshold or dependency pin changed.

The exact self-contained package from `bcfec8d` now completes the full pinned
21-file/seven-family CPU rehearsal, including production-window text/image/audio/
joint-AV inputs, bounded video preview and the 100,000-segment workload. Three
process-cold queries take 9,671.4878, 9,675.5928 and 9,593.861 ms. Warm semantic
p95 is 256.636 ms (maximum 260.8398), embedding p95 79.932 ms and adapter/fusion
p95 175.8071 ms, with 27 unique model calls and zero query-cache hits. Whole-job
peak commit is 6,243,721,216 bytes for extraction and 5,437,243,392 bytes for scale;
all five measured phases remain below 6 GiB. These current-host engineering
targets pass. Startup code was unchanged; earlier slower cold/p95 and media-failure
runs remain retained, and the earlier media error is still unexplained.

This is a 32 GB development-machine run using the already installed CPU add-on,
with Laya off. It proves neither fresh installation nor physical 16 GB behaviour,
ordinary WPF latency, Laya/concurrent UI workloads or independent retrieval quality.
The refreshed hardware ZIP contains the exact tested `bcfec8d` product/driver,
533 base files, 33 source-matched runtime files and 563 verified archive entries.
Its SHA-256 is `772f55dae67449c88204b4f82f5403e0a7132226f22b6b0f99864e610d2c752b`.
The user will run it on the physical PC later. Quality/abstention, broader resource
and lifecycle evidence, and distribution clearance remain release requirements.

The natural-video quality intake adds seven unchanged originals (126,327,527
bytes) from the prior Commons discovery. Transfer stopped at another provider
limit; the verified subset and a fresh offline replay preserve source/provenance
bytes, and the remaining three transfers were not retried. Production no-model
review yields 82 segments, including 27 joint AV and 22 separate audio segments.
All 505 prepared assets and source hashes/window bounds/cleanup verify. Eleven
contact sheets expose 223 sampled frames, now inspected. Nine visual draft queries
cover seven originals/provisional creator groups; multiple train queries are
related tasks. Titles alone were insufficient for boat and nighttime-traffic
content. No auditory labels, absence claims, exact event boundaries or splits
have been approved.

A current Commons attribution capture retains full publisher metadata/page
revisions and creator links, uses bounded/resumable requests and verifies offline
without rewriting evidence. Ten new contracts and fourteen intake contracts pass;
seven current original-checksum/artist/license-label comparisons match. These
matches do not clear original-source rights or aliases/event/derivative grouping.
No model predictions were opened on this pool. Next complete auditory content and
cross-corpus rights/group review, supported negatives and the independent 400-task
holdout; keep the physical 16 GB, WPF/Laya/concurrency, lifecycle and distribution
gates open. Production code and the tested `bcfec8d` hardware ZIP are unchanged.

The inherited-environment gate now has a reproduced baseline: the prior published
model worker executes an injected `PYTHONPATH` startup module before readiness.
Setup and the worker now construct private child environments, use isolated Python
arguments and disable discovered uv configuration. Explicit setup authentication,
proxy/certificate settings and offline requests remain supported; worker inference
stays offline. The separate parser environment is unchanged. Two environment
contracts and a real PowerShell native-command/exit-status contract bring the
native suite to 351; seventeen Python setup/model-reuse contracts pass.

An actual empty-root upstream CPU installation under seventeen controlled parent
overrides now passes all encoder readiness, real query, sandbox parsing/embedding,
notice integrity and ordinary-database preservation. Its foreign startup/cache
directory remains byte-identical. The successful case resumed two unpublished
attempts: a missing trusted `PATHEXT` in the first guard implementation prevented
PowerShell native dispatch, then concurrent diagnostic appends failed the test
logger. Both failures are retained; trusted native extensions and serialized
diagnostic writes address those observed causes. A later build hit the active
driver's executable lock; it was deferred until the validation exited. This is
current-account installation evidence, not a clean-account or physical 16 GB pass.
Changed-dependency/device upgrade, broader lifecycle, independent quality and
distribution requirements remain open. No dependency pins or quality thresholds
changed.

The contaminated existing-generation lifecycle also passes real verification
interruption, observed-process exit, ready-marker/configuration preservation,
fallback CPU query and sandbox parsing/embedding, offline retry, notice checks and
private add-on removal while preserving the ordinary database. All seventeen
foreign overrides remain contained. The first lifecycle attempt failed an
immediate bootstrap-lock acquisition after the observed processes exited; later
inspection found both locks available and no surviving installer. The harness
now measures lock acquisition with a ten-second bound, retrying only Windows
lock-violation 33. The successful separate run reacquired both locks in 0.6576 ms
with zero retries; it does not establish the cause of that earlier transient.
All 24 package contracts pass. Exact committed-package resource replay and
validation-bundle refresh follow this implementation milestone.

The exact self-contained `317b288` package now passes the full pinned 21-file,
seven-family CPU replay, production-window fixtures, video preview and 100,000
segments across 400 files. Three process-cold queries take 12,876.8949, 9,448.6961
and 9,889.9265 ms. Semantic p95 is 297.3272 ms (maximum 318.2054), with 27 unique
model calls and zero cache hits. Whole-job peak commit is 6,228,635,648 bytes for
extraction/preview and 5,473,267,712 bytes for scale; all five measured phases
stay below 6 GiB. These current-host engineering targets pass. The run uses the
existing CPU add-on on the 32 GB development machine with Laya off; it does not
measure physical 16 GB behaviour, ordinary WPF latency or concurrent workloads.
Earlier failures remain retained, including the unexplained media extraction and
immediate post-cancellation lock failures.

The refreshed validation ZIP preserves the tested committed app/driver, 533 base
files, 33 source-matched runtime files and 563 individually verified ZIP entries.
Its SHA-256 is `20358424f4bbe20048bf69bf2bc0dd0f45a174d3cfa435f1ad34e07de33553a0`;
the previous `772f55d` bundle is preserved. The source-bound private receipt links
the fresh/lifecycle precommit build and the exact committed resource assembly
identities separately. The user will run physical-PC validation later. Next
complete actual changed-dependency/device upgrade, remaining account/volume and
parser lifecycle checks, independent content-grounded quality/abstention and
WPF/Laya/concurrency validation. Distribution clearance remains open; this package
is a validation preview, and release readiness is still false.

Actual CPU/CUDA dependency switching now passes using the exact published
`317b288` product and a dedicated validation driver. The retained fresh CPU
installation upgrades Torch/torchaudio from `2.8.0+cpu` to `2.8.0+cu128` and
torchvision from `0.23.0+cpu` to `0.23.0+cu128`. The remaining package versions
stay pinned. All encoders verify on the RTX 5060 Laptop GPU before an intentionally
incorrect native-audit hash rejects publication in the private probe. The CPU
marker survives, and actual CPU query/parsing still works. Restoring the original
audit allows offline CUDA retry, query/parsing and model reuse. Both devices then
pass the same 21 pinned files, seven families and video preview. Offline CPU
rollback, CUDA reactivation and final restoration of the exact original CPU
marker pass. Every original CPU environment/model file, ordinary database,
selected source and fixture original remains unchanged. The private audit policy
is restored byte-for-byte; no fault is shipped.

CUDA content validation records a 19,499.5471 ms cold query and 5,542,924,288-byte
whole-job peak commit. The CPU content replay after rollback records 30,862.9321
ms and 6,239,522,816 bytes. Both memory peaks stay below 6 GiB; this CPU cold query
misses the 20-second target. Preserve it alongside the earlier passing CPU trials
and profile startup under coexistence/load before claiming that resource gate.
These are current 32 GB host observations, not minimum hardware or quality proof.
The initial driver attempt was deliberately stopped for a report-field casing
error before repeating with the corrected driver; its interruption evidence is
retained. The native suite passes 351 checks. Production code, runtime pins and
the tested `20358424` hardware ZIP are unchanged. Same-device dependency-version
upgrades, remaining lifecycle/account/volume tests, independent quality, broader
UI/resource evidence and distribution clearance remain open.

Independent post-run verification rehashes all 24,462 original CPU environment/
model files and all thirteen pinned model files in each device generation. Both
71-package inventories differ in exactly the three expected CPU/CUDA versions;
the original CPU marker, database/source and tested hardware ZIP also verify.
Three subsequent separately timed CPU startups with both generations installed
take 16,655.5011, 15,595.1877 and 15,208.4690 ms. They meet the target for these
trials and place most elapsed time between worker launch and readiness. They do
not reproduce or diagnose the earlier 30,862.9321 ms miss; keep that failure open
and investigate the model-startup interval under representative load. No input
budget, threshold, dependency pin or production code changed for this follow-up.

The new isolated constructor profiler separates the worker-readiness interval
without changing production code. On the fresh CPU environment it measures
0.652 s pinned-model verification, 2.421 s Torch import, 8.681 s Sentence
Transformers import and 3.704 s model loading. Bounded nested tracing preserves
all six authored query/text/image/audio/video/joint-AV vector hashes exactly.
Windows platform queries total about 0.05 s in the traced runs, so they are not
the observed dominant cost. The final trace measures 3,529 source compilations
(68,469,847 bytes) taking 4.897 s, with 3,282 import-name aggregates and no dropped
names. The fresh venv has 12,350 Python source files and no bytecode files; the
earlier fast reusable CPU venv contains 3,356 bytecode files. These observations
support testing setup-time bytecode preparation next, without reducing model or
modality coverage. They do not diagnose or waive the earlier 30.863 s miss.
The profiling process retains all 2,977,485,952 FP32 parameter bytes and remains
below 6 GiB whole-job commit on this development host. Its small authored inputs
and diagnostic overhead are separate from the published worker/resource gates.

A disposable copy of the fresh no-bytecode CPU environment now passes an offline
bytecode trial using pinned uv 0.11.27. The existing 71-package set checks without
downloads, and 12,350 bytecode files compile in 6.27 s. Every pre-existing cloned
venv file remains unchanged; only bytecode files are added. Independent capture
also confirms all 3,320 copied base-interpreter files remain byte-identical.
Three process-cold queries before preparation take 17,855.9540, 15,492.4147 and
17,008.1363 ms; afterward they take 10,610.5059, 10,333.6169 and 10,641.2525 ms.
Constructor source compilation drops from 3,529 files/5.0112 s to 175 files/0.3319 s.
All six authored vector hashes remain identical with every FP32 parameter loaded.

The compiled copy passes the pinned 21 files, all seven families and video preview,
with whole-job peak commit 6,245,781,504 bytes, below 6 GiB. Original venv, model,
interpreter, ready marker, ordinary database and selected source hashes remain
unchanged. The disposable runtime is removed. An older failed lifecycle runtime
was first recovered and removed through the verified cancellation/query/parser/
offline-retry path to reclaim space; its original failure logs remain retained.
This ordered current-host comparison does not identify the earlier 30.863 s miss
or establish physical 16 GB, quality or concurrency approval. Shipping setup must
compile only a new generation and retain immutable old generations for rollback;
that integration and exact packaged replays follow this experiment.

Shipping setup now requests bytecode compilation for unpublished generations and
uses setup environment revision 3. Model, dependency and interpreter pins remain
unchanged. The existing-ready-generation branch still performs only verification
and notices. Seventeen Python setup/model-reuse contracts, 351 native checks and
24 package contracts pass; the self-contained package retains 533 base files and
33 allowlisted runtime files.

An actual offline upgrade from the preserved revision-2 published package to the
new preview passes injected native-audit rejection after compiling 12,350 files,
old-package query/parser fallback, offline retry/model reuse, new query/parser,
exact old-marker rollback and current-generation reactivation. Every old venv and
model file hash remains unchanged. This is a cache-preparation environment
revision, not a dependency-version upgrade. The native policy is restored, and
ordinary storage/source hashes are preserved. Exact committed-package resource
replay and refreshed physical-PC validation delivery follow; the earlier cold
miss and broader release requirements remain recorded.

The exact committed `eacfe16` revision-3 package now passes three CPU process-cold
queries (13,192.8882, 10,771.6937 and 10,873.9388 ms), the pinned 21-file/seven-
family production-window workload, video preview and 100,000 segments across
400 files. Warm semantic p95 is 331.5853 ms (maximum 371.9998), with 27 unique
model calls and zero cache hits. Whole-job peak commit is 6,253,801,472 bytes for
extraction/preview and 5,437,054,976 bytes for scale; all five measured phases
remain below 6 GiB. These targets pass for this current 32 GB host with Laya off.

The same committed package also passes actual revision-3 CUDA preparation,
controlled audit rejection, CPU fallback, offline retry, all 21 files/seven
families and preview on both devices, CPU rollback, CUDA reactivation and final
CPU restoration. Both generations contain 12,350 prepared bytecode files and all
thirteen pinned model files verify. Current device-content cold queries take
10,346.1210 ms on CPU and 12,642.5038 ms on CUDA; whole-job peaks are 6,230,167,552
and 5,525,397,504 bytes. Independent verification preserves the old environment,
model, shared interpreter and original database/source bytes, and binds the
committed package, driver and runtime sources to the raw reports.

The refreshed hardware ZIP has SHA-256
`6c9807577c0a1c248c35582a0d2dcc5211dfb393418b9555fafd97cfd6b84a74`, with 533 base
files, 33 source-matched runtime files and all 563 ZIP entries verified. The prior
`20358424` bundle is retained. The user will run physical 16 GB validation later.
Bytecode preparation addresses the measured repeated-compilation cost; the
specific earlier 30.863 s miss remains historical evidence with an unproven cause.
Independent quality/abstention, same-device dependency-version upgrades, remaining
account/volume/parser lifecycle checks, WPF/Laya/concurrency and broader resource
coverage, and distribution clearance remain open. No input budget, admission
threshold, model precision or content-family scope changed.

Quality work resumes with six pinned natural Python files from independent public
repositories. A bounded inert-code intake verifies commit metadata, reconstructed
Git trees and selected blob hashes; ten new offline integrity contracts and nine
shared transport contracts pass. The completed private intake also replays offline
without changing its manifest. It has not run inference or frozen task labels.
Under the approved internal-preview scope, further task grounding, freezing and
inference are deferred. Keep these related sources/queries out of the future
release holdout. Physical 16 GB validation remains pending for the user's later run.
