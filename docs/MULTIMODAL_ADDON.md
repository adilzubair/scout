# Multimodal add-on development preview

Date: 2026-10-15. Branch: `feature/multimodal-semantic-search`.

Scout now has an independent setup entry point, authenticated embedding worker,
Windows process lifetime, and Multimodal settings page. The preview installs and
tests the full EmbeddingGemma 2 model and indexes explicitly selected folders in
the background. Constrained semantic matches and evidence navigation now connect
to search results. The separate durable queue, native snapshots, isolated
document/image/audio/video readers, app scheduler and retrieval preview are implemented.
All content families remain in the release scope. LibreOffice is removed from
the product path by user direction. Office evidence will use extracted text,
cells, chart data and embedded images, without exact Office page/slide rendering.

## Settings and installation

Open Settings → Multimodal. Install or verify the add-on for CPU or CUDA, then
use Test add-on to check offline startup and a query embedding. Installing does
not enable Laya or change its Python environment. Enabling the preview requires
an installed matching device, at least one existing folder, and at least one
selected content type. All seven content families are selected by default.

The enablement, indexing pause, selected folders/types, device, CPU threads,
battery preference and index quota are stored separately in
`ScoutSettings.Multimodal`. Selected semantic folders also join the ordinary
metadata library while enabled. The existing keyword content-search setting is
independent. Content indexing is off by default; all seven families are selected
when users opt in, and indexing pauses on battery by default.

OCR uses locally installed Windows language packs inside the same no-network
AppContainer as the readers. Settings lists available languages. Leave the OCR
field blank to follow Windows preferences, or select up to three language tags
such as `en-US, ar-SA`. Scout does not install language packs. A missing pack or
OCR budget exhaustion reports partial coverage and retains visual embeddings.
Changing OCR languages invalidates prior content evidence and rebuilds the index.
After adding a pack in Windows Settings, use **Retry partial or failed content
files** to rebuild incomplete reads. OCR evidence keeps the image/PDF page, slide
or sheet locator and records the actual recognition languages.

Each source gets at most 64 OCR images and 20 seconds of OCR work; a recognition
operation has a three-second deadline. Input is normalized to at most four million
pixels and 4096 pixels per dimension, within the parser's existing memory/job
limits. Visual segments remain when text recognition is unavailable. OCR can make
transcription errors; supported-language quality is still a holdout gate.

The OCR slice passes 292/292 Release checks, seven Python OCR/scan contracts and
the existing ten document-reader contracts. Authored PNG, multipage TIFF and
scanned PDF sources pass actual AppContainer OCR, full-model CPU embedding,
SQLite publication and page-two retrieval evidence. Missing-language coverage
retains image vectors. English (US) is validated by this smoke; Arabic OCR is not
installed on the test host. The scan fixtures also exposed and fixed PDFium
page/bitmap cleanup against the pinned wrapper. These are compatibility checks,
not the independent quality evaluation.

**Review content coverage** lists up to 500 queued, partial, unsupported or failed
files with explanations. It follows the enabled scope and clears rows after
revocation. Known legacy Office, macro-enabled formats and unsupported image/media
containers receive explicit statuses. Known archives in enabled content folders
are recorded by filename for coverage whenever a content family is selected;
archive bodies are never copied, expanded or embedded. Ordinary filename search
remains available for unsupported content. Generic archives do not grant content
processing permission for any of their members.

Encrypted PDFs and ODF streams, binary Office containers, malformed images and
oversized image headers have bounded, terminal statuses. A binary Office status
does not distinguish encryption from a mislabeled legacy container. Transient
access/process failures retain retry behavior. Sparse XLSX ranges now enclose
all included cells; reversed cell ranges are rejected, and unsupported drawing
positions/ODF merged-cell overflow are explicit. Transparent images are composited
onto white before JPEG preparation and OCR. GIF frames join the supported image
reader; HEIC, SVG and raw-camera formats currently remain unsupported.

This slice passes 296/296 Release checks, fourteen document/image reader contracts,
the seven OCR and thirteen media-reader contracts, plus nine actual AppContainer
format-status fixtures. WPF verifies the scoped coverage list and revocation and
uses actual sandbox extraction for DOCX/ODT/PPTX/ODP/XLSX/ODS evidence previews.
Those UI checks use a deterministic embedding boundary; actual model quality is
evaluated separately. They preserve paragraph, slide and sheet/cell evidence,
without promising exact Office layout. Packaged crash/upgrade and quality/resource
gates remain open.

The content-quality evaluator now freezes dataset and source hashes together with
`evaluation/multimodal/protocol.json`. It rejects related groups or byte-identical
sources crossing development/holdout, helpful filenames, unsafe source paths and
invalid file/locator labels. A freeze cannot be overwritten, and source/dataset/
protocol mutations invalidate a run. Labels and source bytes are fixed before
inference; related task counts and independent source-group counts are reported
separately. This replaces the filename-only benchmark as the basis for future
content evaluation, while preserving that benchmark as regression evidence.

The evaluator runs actual sandbox extraction, native source checks, full-model
CPU embeddings and durable indexing, then compares current keyword content
search, semantic-only retrieval and fusion at 768/512/256 dimensions. Shorter
dimensions use normalized prefixes of the same native output. A second pass
replays cached real embeddings through the production app adapter and compares
the resulting file order; replay latency is not model inference latency. Reports
include source coverage, first-file/Recall@20, locator and negative-admission
counts, ambiguous-case certainty, cache use, query activation state and code/model
hashes. Actual locators, temporal start offsets and leading cosine scores remain
in the report for audit; time-window overlap is not a claim of exact timestamp
alignment. Laya on/off and full UI outcomes remain separate unfinished checks.

The initial frozen development pilot has fourteen authored files, seven related
source groups and twenty-one tasks across all seven families. It is **not the
400-task holdout**: its synthetic voice and drawn motion clips do not cover natural
photos, environmental recordings, real video or joint audiovisual content. With
the unchanged provisional 0.35 floor, semantic/fused search found the expected
first file and locator on all seven positive cases, but admitted files on all
seven negatives at each tested dimension. These tiny counts demonstrate an
abstention problem; they do not establish population accuracy. No threshold has
been tuned from them. Release-quality gates explicitly remain ineligible for a
development pilot.

Setup uses `%LOCALAPPDATA%\Scout\addons\multimodal` (under `SCOUT_DATA_DIR` in
isolated diagnostics). Its dedicated Python, dependency cache, model and setup
tool stay outside the base application installation. No Office suite is required. The
bootstrap pins uv 0.11.27 by SHA-256 and length and requests Python 3.12.13. It
disables uv's global executable aliases and Windows registry registration.
Dependencies use the existing CPU/CUDA hash locks. Downloaded artifacts remain
outside Git and the base ZIP.
[uv Python installation behavior](https://docs.astral.sh/uv/concepts/python-versions/).

Each dependency identity gets a stable generation directory. A killed setup
releases its OS file lock; retry reuses that generation and download caches.
Windows venv directories are not renamed because launchers may contain absolute
paths. Model downloads resume through Hugging Face's cache. The small bootstrap ZIP
restarts after an interrupted transfer. Setup publishes an atomic installation
marker only after full model integrity and actual text,
image, audio and video embedding checks pass. Reverification does not modify a
completed generation's dependency environment. A failed new generation leaves
the prior installation marker in place.

Cancel setup or close Settings to stop the owned setup process tree. Installation
and removal are immediate actions. Removal first stops Scout's worker, refuses
linked paths/running setup, and deletes only the dedicated add-on directory;
it also saves disabled enablement. Laya and the ordinary file library stay
outside that directory. Old completed generations are retained for switching
devices; automatic generation/cache reclamation is still release work.

## Worker lifecycle and protocol

The host reports not installed, installing, disabled, available/unloaded,
loading, ready, paused, or failed. Applying settings cancels the current session
and stops its worker. Starting Scout with the add-on absent/disabled needs no
Python. An installed worker loads on demand, unloads after five idle minutes,
and is terminated at exit through a Windows job with kill-on-close semantics.
No UI or index queue depends on Laya readiness. Active content jobs hold an
activity lease to prevent idle unload during snapshot acquisition or parsing.
[Windows job lifetime](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).

Windows venv launchers can create a separate interpreter process. Readiness
therefore checks that the reported worker PID belongs to Scout's job, then
checks that authenticated health returns the same PID, model revision, device,
dimensions and exact full modality list. A parent-identity watcher is a second
exit mechanism. Startup has a 120-second deadline. User cancellation returns
promptly; the inference gate stays held until the actual Torch pass ends.
Settings changes/exit instead terminate the entire session.

The worker binds an ephemeral IPv4 loopback port and requires a new random
256-bit bearer token for health and embedding requests. Browser Origin/fetch
headers and foreign Host values are rejected. Tokens never enter readiness
files, process arguments or logs. Redirects/proxies are disabled in the C#
client. Input bodies are bounded, fixed-length JSON; expensive decoding and
inference are serialized. The server caps active request handlers and rejects
busy embedding requests rather than queueing decoded media indefinitely.

Protocol version 1 supports query text and prepared document segments:

| Segment | Representation and initial bounds |
| --- | --- |
| Text | Bounded Unicode text, with the full-model token check before inference |
| Image | Base64 single-frame PNG/JPEG/WebP/BMP/TIFF; 8 MiB encoded artifact budget and 4 million decoded pixels |
| Audio | Base64 little-endian mono float32 at 16 kHz; finite samples in [-1, 1], at most 60 seconds |
| Video | At most 16 equal-sized prepared frames, 16 million total pixels, ordered frame indices and a window of at most 60 seconds; optional synchronized mono 16 kHz audio for the same window |

The entire request is limited to 32 MiB. Filesystem paths, URLs and unknown
content fields are rejected. Office/PDF extraction, audio/video decoding and
segmentation occur before this boundary in isolated readers; this is not permission
for the worker to read arbitrary files. Responses contain a finite normalized
768-dimensional vector and a checked token count, never a launch target. The
worker performs offline model loading and does not log inputs or API bodies.

## Verification and remaining gates

An isolated CPU generation was created from cached locked dependencies and
previously verified local model artifacts (the old renderer-bearing generation
was a historical setup test). The exact PowerShell entry
point then downloaded/verified its pinned bootstrap, acquired its private Python,
and reverified that generation. The corrected bootstrap leaves no Python test
registry entry. This is not yet an end-to-end clean-account internet install;
the imported development generation references the original development
interpreter. That clean install, device upgrades, forced termination mid-download,
disk exhaustion and failed-generation reclamation remain release checks.

The real CPU and CUDA worker processes pass authenticated query plus prepared
text/image/audio/video inference with outgoing Python socket connections blocked.
The C# host passes query/image responses, disable and restart with Laya absent,
startup cancellation, and inference cancellation followed by another query.
Six socket/protocol contracts, four setup/resume contracts, fourteen Office
contracts and seven earlier multimodal contracts pass. Scout's Release suite
passes 231 checks with zero build warnings, including settings compatibility,
installation identity, scoped removal, and job termination of descendants.
The isolated WPF smoke workflow covers the new settings page and navigation.

The separate `SemanticIndex`/`SemanticIndexer` foundation now persists 768d
segments, typed page/cell/line/time evidence and durable pending/running leases
in its own SQLite database. Configuration generations invalidate late results;
changed/deleted sources immediately lose previous segments. A single scheduler
owns restart recovery. Transactions enforce complete file replacement and the
index quota; failed jobs retry with backoff or receive an explicit unsupported
status. Cancellation returns work to the queue. Root/type/exclusion revocation
purges rows and checkpoints/compacts SQLite, including its WAL. This is logical
purge, not a claim about physical storage erasure.

Nineteen additional C# contracts verify that foundation, including extraction
finishing after revocation and old source refreshes racing newer metadata.
The Release suite now passes 250 checks. No semantic database is created in a
normal Scout profile yet; application scheduling and readers remain pending.

The native source adapter now uses canonical handle paths, the Windows file ID,
volume identity and change/write/creation times for revisions. It rejects
network paths, alternate streams, hidden/reparse/offline/recall sources and
ancestors, checks selected roots, and copies through a handle that blocks
concurrent writes/deletion. The owned snapshot is hashed and the source is
rechecked before publication. Six additional contracts cover preserved-metadata
replacement, scope rejection, snapshot cleanup and cancellation; 256 Release
checks pass. This adapter is not connected to personal indexing yet.

The renderer-free reader foundation now extracts all six Open XML/OpenDocument
formats, including paragraph groups, slide text/notes, cached chart series,
shared/inline strings, numeric/boolean cells, cached formula values, merged
headings, repeated ODF cells and embedded images. Missing caches/unsupported
objects and absent Office layout get explicit coverage reasons. It never
calculates formulas or fetches external resources. PDF text/page images,
multi-page images, source lines and CSV cell evidence have bounded readers too.

The extraction launcher creates a zero-capability Windows AppContainer with a
clean environment, no inherited handles, a suspended start assigned to a job
before execution, 1 GiB per-process/2 GiB job limits and a bounded process count.
Only dedicated runtime directories receive read access; owned jobs receive write
access. It restores its package-specific grants and removes the profile on normal
disposal. The CLI refuses execution outside this sandbox and emits bounded,
hashed prepared segments. Windows canonical NT handle names preserve path
boundaries without DOS-volume namespace access or ancestor permission changes.
Crash recovery of permissions, resource pressure and application scheduling still
need lifecycle integration before release.

All six Office formats pass actual AppContainer extraction with the published
tokenizer and C# locator validation. Twelve prepared Office segments (including
embedded image content) pass full-model CPU embedding. Ten reader contracts and
258 Release checks pass; authenticated CPU all-modality inference also passes.
A real schema 2 CPU generation was installed from cached locked dependencies and
verified model weights without any Office runtime. The base ZIP was inspected
and contains no Office helper, manifest or downloader. These are development
checks, not a completed clean-account install or end-to-end retrieval evaluation.

The Office helper is excluded from the application package and new setup no
longer downloads or verifies it. Schema 2 installations contain only the model
and interpreter paths; existing schema 1 model generations remain usable even
when their obsolete Office folder is absent. The model and dependency identity
checks remain mandatory. Further reader coverage and parser lifecycle hardening,
application integration, prioritization, query fusion, evidence
previews, holdout quality, 16 GB hardware measurements and final distribution
review remain required before release. This preview does not establish general
retrieval quality or all-format fidelity.

The renderer-free restart adds streaming media-file readers through the existing
PyAV 19.0.1 wheel; no separate FFmpeg executable is installed. Audio is downmixed
and resampled to mono 16 kHz in 12-second windows with 2-second overlap. Video uses
8-second windows with 1-second overlap and samples at most one frame per second
by default, with separate audio segments when a track is present. The reader
preserves presentation timestamps, normalizes their common playback origin, and
handles Matroska's millisecond timestamp precision without inventing audio gaps.
The prepared video metadata uses a virtual 100 Hz timeline to represent sampled
timestamps; it does not describe the original file's frame rate. Receipts also
retain actual sampled timestamps.

When audio covers an entire video window, the reader also emits a joint audiovisual
representation. It stitches overlapping audio windows by sample position, keeps
only two decoded audio windows in memory, and never pads missing sound with
silence. Frame-only and audio-only vectors remain available. Missing audio coverage,
the shared 90-second preparation deadline, or the existing segment/output limits
leave explicit partial-coverage reasons. Joint windows use the existing `video`
modality and timestamp preview; their evidence identifies synchronized frames and
sound. Changing reader/protocol bytes invalidates the app's reader identity, so
earlier separate-only vectors are reindexed.

The memory-only protocol accepts a strict nested `audio` object with `data` and
`sampleRate`; paths, URLs and unknown fields remain forbidden. Python verifies
audio duration against the virtual video clock within its 10 ms rounding precision.
The trusted C# bridge additionally requires audio samples to match the actual
locator span within two samples. Both channels share the model's 8,192-token limit;
oversized combined inputs are rejected before inference, without truncation.

An authored offline compatibility probe runs the exact pinned model on CPU and
CUDA. Hooks verify both encoders run for a joint input; changing either channel
changes its vector. The two-second example uses 300 tokens, versus 248 for its
frames and 54 for its sound separately. This establishes runtime compatibility,
not accuracy on natural audiovisual events. Nineteen reader and seven worker
contracts cover stitching, missing audio, budgets and strict nested payloads.
The independent natural-media and 400-task holdout gates remain open.

The actual CPU extractor smoke publishes seventeen files across all seven families,
including two joint MP4/MKV requests through the authenticated full-model host and
SQLite timestamp evidence. Mutation, revocation, cancellation and snapshot cleanup
still pass. Independent CPU and CUDA worker HTTP smokes accept combined payloads
with outgoing Python sockets blocked. The Release suite passes 305/305 checks.

The production-adapter scale benchmark uses seeded synthetic 768d vectors and
250 text segments per generated local source. It publishes through the actual
SQLite queue, then exercises metadata constraints, cached vector loading, native
source validation, evidence retrieval, lexical fusion and uncached CPU query
embedding. Each scale has three warmups and 24 distinct measured queries, with
zero query-cache hits. It verifies forty current file matches with evidence.
Generated source/index directories are removed after each completed scale; JSON
measurements remain under ignored diagnostics. No extraction or content-quality
claim is derived from the synthetic setup throughput.

The first run on the available Intel Core Ultra 9 275HX / 32 GB Windows 11 machine,
with Laya off, measured:

| Segments | Generated files | Warm semantic p95 | Query embedding p95 | Initial worker/matrix/query |
| --- | --- | --- | --- | --- |
| 25,000 | 100 | 393 ms | 67 ms | 15.5 s |
| 100,000 | 400 | 471 ms | 66 ms | 18.0 s |
| 500,000 | 2,000 | 893 ms | 61 ms | 33.5 s |

The semantic column includes query embedding, adapter retrieval and fusion. The
initial column includes the worker activation caused by configuring each scale;
it is not isolated matrix loading. The largest index was 2,121,932,800 bytes and
the benchmark app's private memory was 1,880,809,472 bytes, excluding the separate
model worker. Raw samples were independently rescored and generated-index cleanup
was checked. The original run retained its exact benchmark-source snapshot,
binary hashes and separately captured hardware metadata. Future runs write
hardware/CLR/SIMD and binary/model-manifest identities automatically. The Release
suite now passes 307 checks, including percentile and provenance validation.

This grouped-file synthetic workload does not prove performance for every file
distribution, full WPF latency, ordinary-search p95 regression, decoder throughput,
or the physical 16 GB CPU laptop gate. The 500K initial activation is substantial
and remains visible in the report. Large-library measurements and all remaining
quality, hardware and packaging gates are still required.

Decoding reads the owned snapshot through an explicit demuxer, denies secondary
I/O and URL protocols, and runs inside the same zero-capability AppContainer.
Bounds include 600 seconds per track timeline, 60,000 decoded frames and 200,000
packets across the file, 90 seconds of decoding wall time, 16 MiB packets,
16-million-pixel source video frames, 250,000-pixel prepared frames and the
existing 256-segment/256 MiB prepared-output budget. The launcher also enforces
its process/job memory and wall limits. Sampling, uncovered tracks, clipping,
estimated end times and reached limits produce explicit partial-coverage reasons.
No speech transcript or visual caption is fabricated.

The initial thirteen Python contracts covered real codec combinations, silence, video-only
and audio/video input, overlapping windows, variable timestamps, coarse audio
timestamps, sparse held frames, genuine gaps, corrupt/renamed playlists and
duration/frame/output limits. Sixteen authored smoke files pass actual sandboxed
extraction and C# locator/receipt checks. Validated combinations include WAV PCM,
MP3, AAC in M4A and raw AAC, FLAC, Ogg Opus, H.264 in MP4/MOV/MKV/M4V, VP8 in
WebM and MPEG-4 video in AVI. These do not establish support for every codec that
can occur inside those containers. Before joint windows were added, all 22 prepared
segments (12 audio, 10 video) passed full-model offline CPU and CUDA embedding with normalized 768-dimensional
vectors and checked token counts. The largest input in this pack uses 1,098
tokens; smoke inference totals are 68.4 seconds on CPU and 4.0 seconds on CUDA.
Those totals exclude extraction/startup and are not query-latency, quality or
minimum-hardware benchmarks. The Release suite remains 258/258 with zero build
warnings, and the six existing worker protocol contracts still pass.
File-to-index integration, decoder cancellation
and crash recovery, audio/visual event quality and full-resource measurements
remain release work.

The trusted `SemanticExtractor` bridge now owns snapshot acquisition, sandbox
launch, bounded manifest/segment validation and full-model requests. It exposes
only a hash-verified tokenizer copy to parsers. It checks source hashes, fixed
segment names/lengths/digests, modality families, normalized vectors, token counts,
locators, media sample bounds, playback duration and sampled-frame timestamps.
Unknown/duplicate fields, paths in prepared inputs and malformed coverage are
rejected. The native source identity is checked between segments, and the original
content hash is checked again before returning a publishable result. Cancellation
drains the parser job before its owned copy and package-specific grant are removed.

Sandbox permission changes now have a bounded, flushed recovery journal written
before the change. Recovery accepts only the same package SID and approved
runtime/owned-job directories, validates the entire record before applying it,
preserves other principals' access rules, and retains invalid recovery data for
repair. Per-job grants are released after each extraction. Actual owner-process
exit without disposal and subsequent recovery pass, including rejection of an
out-of-scope recovery path. Upgrade/removal recovery across changed runtime roots
and packaged forced-exit cases remain additional release checks.

Model and extractor runtime leases use shared Windows byte-range locks. The
existing setup/removal entry points require exclusive locks, so a live runtime
blocks them before deletion begins; a model and parser can hold leases together.
The model host gives queued interactive queries priority over document embeddings,
including queries arriving during cold loading. An in-flight Torch pass still
finishes or its owned worker is stopped; cancellation never starts a second pass
over the first one.

Seventeen authored source files across all seven content families pass actual
native acquisition, AppContainer extraction, full-model CPU embedding, typed
receipt validation and atomic SQLite publication. Source mutation and revocation
during embedding are rejected, cancellation removes owned snapshots, and disabling
scope purges the resulting rows. Eight new C# checks bring the Release suite to
266/266 with zero warnings. This is a bridge smoke, not Scout's selected-folder
background scheduler or final search UI; those integrations remain required.

The app scheduler now has separate metadata-reconciliation and content-worker
tasks. Metadata events signal work without decoding, hashing or model inference
inside the metadata refresh gate. Reconciliation checks native source identity,
deduplicates paths and periodically rechecks skipped/changed sources. A selected
folder's removal, exclusion or type change updates eligibility and cancels the
old generation before asynchronous cleanup. Settings displays ready, queued,
processing, partial, unsupported and failed counts plus index size.

Pause and quota exhaustion start no new content jobs. Low disk space, available
memory and the optional battery rule pause extraction without spending a retry
attempt; foreground query priority remains independent. The initial guard keeps
512 MiB free beyond a source snapshot, requires 4 GiB available physical RAM
before cold loading and 512 MiB with a live model. These guards are not measured
minimum-hardware claims. Failed reads retain their bounded backoff and terminal
status; Settings can retry failed files without rebuilding unchanged ready files.
Install/verify/test actions suspend and drain the content queue, then restore the
saved configuration. Removal disables indexing, stops workers, clears only the
semantic database/WAL/SHM and removes the add-on; corrupt derived storage does not
require changing the ordinary library or original files.

Eleven scheduler contracts bring the Release suite to 277/277. They cover default
off, selected-root consent, nonblocking metadata refresh, late publication after
revocation, deletion/hidden-file events, pause/maintenance, quota/resource waits,
retry, duplicate metadata representations and corrupt-index removal. A real CPU
smoke indexed sixteen authored sources across all seven families through the app
scheduler, then verified watched arrival/rename, maintenance resume and complete
scope revocation. WPF smoke checks also cover the progress and retry controls.
Clean-account packaging, packaged forced process-death resume,
quality and hardware/resource gates remain required.

The retrieval preview now applies file/folder/type/time constraints before vector
ranking and checks native source identity before admitting evidence. It combines
the best segments per file with ordinary results, preserving literal names,
explicit chronology, count limits and the user's selection. Query responses are
debounced, cached by index generation and discarded when superseded. Laya remains
optional and receives metadata only; content evidence survives its refinement.
The initial cosine admission floor of 0.35 is provisional and uncalibrated.
It does not establish relevance confidence or satisfy the holdout quality gate.

Content results show their matched page, slide, sheet/cell range, paragraph,
line or time window. Office previews show extracted text and available images
with a partial-coverage notice rather than reproducing Office layout. PDF matches
navigate to their page. Media matches open paused at their time; if Windows lacks
the source codec, Scout prepares a bounded clip in the same zero-capability
AppContainer used by its readers. That fallback emits at most 20 seconds of
640×360 H.264 video or mono 16 kHz PCM audio and accepts only a hash-verified owned
snapshot. It checks the original source again before playback. Closing or
revoking a preview cancels preparation and removes its owned files; an abandoned
clip is recovered before the next preparation. Cross-generation upgrade and
packaged crash/removal recovery still need release validation.

Current engineering validation passes 289/289 Release checks and the real CPU
selected-folder scheduler/query smoke. Four Python playback contracts cover
sixteen authored media fixtures, later windows, malformed requests and external
resource rejection; the existing thirteen media-reader contracts also pass.
Actual AppContainer playback tests cover WebM/MKV, later MP4/WAV windows and AAC,
then forced process exit, recovery, active-owner isolation and cancellation.
WPF checks retain manual selection after content arrivals, reject revoked content
actions, clear revoked evidence and advance playback for all eight required
audio/video extensions. These checks do not establish general retrieval quality,
minimum-hardware suitability or clean-account release readiness.

The evaluation source-intake tool discovers public Commons candidates using the
checked-in `evaluation/multimodal/media-queries.json`. It records page revision,
original URL/size/SHA-1, creator/license metadata and discovery topics. Downloads
accept only approved HTTPS hosts, selected containers and license labels, at most
32 MiB per file / 512 MiB per batch. Actual downloaded bytes must match the original
size and SHA-1; the local manifest also records SHA-256 and opaque filenames.
These filters do not approve rights or establish what the source contains.
Content, license, related-group and split review flags remain unapproved.

Discovery checkpoints completed requests and resumes only with the same query
definition hash. Both commands use an OS owner lock; completed manifests publish
atomically without overwriting evidence. An interrupted intake can reuse originals
only after rechecking their hashes. HTTP 429/503 retries honor bounded Retry-After
and stop after three attempts. Use `intake --existing-only` to preserve a selected
downloaded subset without any network request. Keep sources, attribution manifests
and later review outputs in ignored diagnostics; never embed or tune against an
unreviewed future holdout to choose its labels.

Fourteen offline contracts cover checksum/budget failure cleanup, altered sources,
host/redirect rejection, license filtering, lock release, immutable publication,
rate limits, interruption recovery and offline-only subset verification:

```powershell
.runtime/python/cpython-3.12.13-windows-x86_64-none/python.exe scripts/test_multimodal_media_intake.py
.runtime/python/cpython-3.12.13-windows-x86_64-none/python.exe scripts/multimodal-media-intake.py discover --queries evaluation/multimodal/media-queries.json --output artifacts/diagnostics/multimodal/candidate-discovery.json
# Select IDs after inspecting provenance; an intake is still an unreviewed pool.
.runtime/python/cpython-3.12.13-windows-x86_64-none/python.exe scripts/multimodal-media-intake.py intake --discovery artifacts/diagnostics/multimodal/candidate-discovery.json --output artifacts/diagnostics/multimodal/candidate-pool --ids commons-147960076
```

`--multimodal-review` prepares an intake manifest's verified sources through the
production native snapshots, no-network AppContainer and receipt validator. It
exports bounded image files, sampled video frames, extracted text and mono 16 kHz
PCM WAV windows with source/asset SHA-256 and locators. It starts no embedding host
and publishes no vectors or labels: an internal deterministic boundary double
allows the production bridge to validate payloads before export. Output is capped
at 512 MiB and marked `embeddingInference: not_run`, `labelsApproved: false`.
Its parser owner is isolated from app workspaces; a real add-on lease prevents
setup/removal while shared runtime files are read. Normal exit removes its private
tokenizer copy, source snapshots, AppContainer profile and temporary permissions.

The first real-media export exposed two full-entry-point startup timeouts through
the Windows venv launcher. Minimal sandbox probes passed for both interpreter
locations, so the underlying cause is not established. Extraction now starts the
private base interpreter directly and sets CPython's trusted
[`__PYVENV_LAUNCHER__` override](https://github.com/python/cpython/blob/3.12/Modules/getpath.py)
to the installed venv executable. The same eight-source export then completed.
A live probe verifies the selected venv, package path, parent/child zero-capability
tokens, source/runtime denial, network denial and clean environment. The sandbox
keeps its existing suspended-start/job limits; there is no unsandboxed fallback.
Fixed body-free parser progress markers distinguish startup from reader work and
are removed with the owned job.

The eight-source review exported seven photos and one 88-second audio file without
inference. The audio initially produced 147 windows due to alternating approximately
10 ms PTS deviations. The reader now aligns deviations up to 20 ms against its
sample counter and reports `audio_timestamp_adjusted` as partial coverage. It
inserts/drops no samples and preserves larger gaps; each incoming timestamp is
checked against the counter so drift cannot accumulate without triggering a gap.
The same sandboxed source now produces nine overlapping windows. Reconstructing
the exported PCM16 stream, after removing configured overlap, preserves all
1,414,003 samples byte for byte against the prior export. Seventeen media contracts
cover bounded adjustment, larger gaps, accumulated drift and variable decoded
frame sizes alongside the existing codec, duration and output-limit checks.
Seven prepared photos were visually inspected before inference; auditory content,
license, related-group and split review remain incomplete. These are engineering
and source-review results, not a quality holdout.

Developer verification (use an isolated add-on root and data directory):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-multimodal.ps1 -AddonRoot C:/Path/To/PrivateTestAddon -Device cpu
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_multimodal_setup.py
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_multimodal_worker.py
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_media_readers.py
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_playback.py
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_ocr.py
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/author_format_fixtures.py
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_multimodal_worker_inference.py --model .runtime/semantic-search/model --device cpu
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_joint_av_inference.py --model .runtime/semantic-search/model --device cpu --output artifacts/diagnostics/multimodal/joint-av-cpu.json
.runtime/semantic-search/.venv/Scripts/python.exe runtime/test_joint_av_inference.py --model .runtime/semantic-search/model --device cuda --output artifacts/diagnostics/multimodal/joint-av-cuda.json
dotnet run --project tests/Scout.Tests -c Release -- --multimodal-host C:/Path/To/PrivateTestAddon
dotnet run --project tests/Scout.Tests -c Release -- --extract-media .runtime/semantic-search/cpu-venv/Scripts/python.exe .runtime/python/cpython-3.12.13-windows-x86_64-none .runtime/semantic-search/model
dotnet run --project tests/Scout.Tests -c Release -- --semantic-extractor artifacts/diagnostics/multimodal/addon-no-office .runtime/python/cpython-3.12.13-windows-x86_64-none
dotnet run --project tests/Scout.Tests -c Release -- --semantic-scheduler artifacts/diagnostics/multimodal/addon-no-office .runtime/python/cpython-3.12.13-windows-x86_64-none
dotnet run --project tests/Scout.Tests -c Release -- --semantic-playback artifacts/diagnostics/multimodal/addon-no-office .runtime/python/cpython-3.12.13-windows-x86_64-none
dotnet run --project tests/Scout.Tests -c Release -- --semantic-scans artifacts/diagnostics/multimodal/addon-no-office .runtime/python/cpython-3.12.13-windows-x86_64-none
dotnet run --project tests/Scout.Tests -c Release -- --semantic-formats artifacts/diagnostics/multimodal/addon-no-office .runtime/python/cpython-3.12.13-windows-x86_64-none
.runtime/semantic-search/.venv/Scripts/python.exe scripts/build-multimodal-evaluation-pilot.py --output artifacts/diagnostics/multimodal/evaluation-pilot-v1
dotnet run --project tests/Scout.Tests -c Release -- --multimodal-freeze artifacts/diagnostics/multimodal/evaluation-pilot-v1/dataset.json evaluation/multimodal/protocol.json
dotnet run --project tests/Scout.Tests -c Release -- --multimodal-review artifacts/diagnostics/multimodal/candidate-pool artifacts/diagnostics/multimodal/addon-no-office .runtime/python/cpython-3.12.13-windows-x86_64-none artifacts/diagnostics/multimodal/content-review
dotnet run --project tests/Scout.Tests -c Release -- --multimodal-scale artifacts/diagnostics/multimodal/addon-no-office artifacts/diagnostics/multimodal/retrieval-scale
dotnet run --project tests/Scout.Tests -c Release -- --extraction-python artifacts/diagnostics/multimodal/addon-no-office/generations/GENERATION/venv/Scripts/python.exe .runtime/python/cpython-3.12.13-windows-x86_64-none
dotnet run --project tests/Scout.Tests -c Release -- --multimodal-eval artifacts/diagnostics/multimodal/evaluation-pilot-v1/dataset.json evaluation/multimodal/protocol.json artifacts/diagnostics/multimodal/addon-no-office .runtime/python/cpython-3.12.13-windows-x86_64-none artifacts/diagnostics/multimodal/evaluation-results development
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_extracted_embeddings.py --model .runtime/semantic-search/model --root C:/Path/Printed/By/SandboxTest --device cpu
```

Development setup also accepts explicit `--existing-model`,
`--cache-dir` and `--offline` arguments to `runtime/semantic_search/setup.py`.
Model imports are checksum-verified. LibreOffice scripts/manifests remain in the
repository only as historical development probes and are excluded from the base ZIP.
Diagnostic files and screenshots stay under ignored `artifacts/diagnostics/`.

The base publish uses an explicit 33-file production runtime list. Test helpers,
authoring/evaluation scripts and historical Office renderer code stay outside the
ZIP; the extraction isolation probe is copied only to test output. The build runs
24 package contracts, checks the self-contained desktop runtime, compares shipped
runtime/setup files with their sources and rejects private indexes, model weights,
environments, linked entries and unexpected runtime files. ZIP creation uses `/`
paths and validates every entry's name, length and SHA-256 before replacing the
archive. A separate `Scout-win-x64.inventory.json` records the checked base files;
it explicitly grants no optional dependency distribution approval.

On 2026-10-15 the fresh 532-file payload and ZIP passed these integrity checks.
The published executable passed the built-in WPF flow with isolated data, including
search/selection, text evidence, coverage revocation, settings and screenshots.
This run used the package working directory and did not load the repository's
optional authored Office fixture directory. A separate offline run of the published
runtime's readiness function passed all five input forms against the existing CPU
development generation. Clean standard-account setup, update/removal, packaged
all-format extraction/playback and hardware/quality gates remain open.

The initial Release run had one quota scheduler timeout (306/307). With diagnostic
state reporting added, the targeted 16 scheduler checks and the full 307-check
rerun passed. No production quota change was made and the original timeout has no
confirmed cause; retain it as an intermittent lifecycle issue for investigation.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-package.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Publish
dotnet run --project tests/Scout.Tests -c Release --no-build -- --semantic-service-checks
# Set SCOUT_DATA_DIR to a new isolated directory before running this UI flow.
artifacts/Scout/Scout.exe --ui-smoke-test
```

Setup retains bounded, hash-verified package/interpreter notices and supplemental
upstream license texts in `generations/<generation>/notices/`. Re-verification
adds an immutable bundle and atomically updates `current.json`; it does not alter
the installed dependency environment. A collection failure prevents publication
of a new ready generation and leaves the prior installation marker intact.
Readiness now tests joint audiovisual input as well as each individual encoder.
The notice inventory records native reference hashes and outstanding source/codec
review; it always reports `distributionApproved: false`. See
[the third-party audit](THIRD_PARTY.md) for the specific unresolved native artifacts.

```powershell
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_notices.py
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_multimodal_setup.py
# Run using that generation's interpreter, so its own installed metadata is collected.
C:/Path/To/Addon/generations/GENERATION/venv/Scripts/python.exe runtime/semantic_search/notices.py --generation C:/Path/To/Addon/generations/GENERATION
```
