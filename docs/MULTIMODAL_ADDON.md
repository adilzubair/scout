# Multimodal add-on development preview

Date: 2026-10-14. Branch: `feature/multimodal-semantic-search`.

Scout now has an independent setup entry point, authenticated embedding worker,
Windows process lifetime, and Multimodal settings page. The preview installs and
tests the full EmbeddingGemma 2 model; it does not yet index selected folders or
add semantic matches to search results. The next slice is the durable segment
index and work queue. All content families remain in the release scope.

## Settings and installation

Open Settings → Multimodal. Install or verify the add-on for CPU or CUDA, then
use Test add-on to check offline startup and a query embedding. Installing does
not enable Laya or change its Python environment. Enabling the preview requires
an installed matching device, at least one existing folder, and at least one
selected content type. All seven content families are selected by default.

The enablement, indexing pause, selected folders/types, device, CPU threads and
index quota are stored separately in `ScoutSettings.Multimodal`. Pause, scope
and quota are preferences for the upcoming queue; no background content jobs
exist in this slice. The existing keyword content-search setting is independent.

Setup uses `%LOCALAPPDATA%\Scout\addons\multimodal` (under `SCOUT_DATA_DIR` in
isolated diagnostics). Its dedicated Python, dependency cache, model, Office
renderer and setup tool stay outside the base application installation. The
bootstrap pins uv 0.11.27 by SHA-256 and length and requests Python 3.12.13. It
disables uv's global executable aliases and Windows registry registration.
Dependencies use the existing CPU/CUDA hash locks. Downloaded artifacts remain
outside Git and the base ZIP.
[uv Python installation behavior](https://docs.astral.sh/uv/concepts/python-versions/).

Each dependency identity gets a stable generation directory. A killed setup
releases its OS file lock; retry reuses that generation and download caches.
Windows venv directories are not renamed because launchers may contain absolute
paths. Model downloads resume through Hugging Face's cache. Office downloads
resume with validated HTTP ranges and verify the complete artifact before use;
a mirror that ignores ranges restarts that artifact. The small bootstrap ZIP
restarts after an interrupted transfer. Setup publishes an atomic installation
marker only after full model integrity, Office inventory, and actual text,
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
No UI or index queue depends on Laya readiness. Later queue work must count
active indexing as busy rather than allowing idle unload between long jobs.
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
| Video | At most 16 equal-sized prepared frames, 16 million total pixels, ordered frame indices and a window of at most 60 seconds |

The entire request is limited to 32 MiB. Filesystem paths, URLs and unknown
content fields are rejected. Office/PDF rendering, audio/video decoding and
segmentation occur before this boundary in later readers; this is not permission
for the worker to read arbitrary files. Responses contain a finite normalized
768-dimensional vector and a checked token count, never a launch target. The
worker performs offline model loading and does not log inputs or API bodies.

## Verification and remaining gates

An isolated CPU generation was created from cached locked dependencies and
previously verified local model/Office artifacts. The exact PowerShell entry
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

The Office helper is not yet invoked by this worker, and its network/resource
restrictions remain part of the extraction integration. Native source validation,
application lifecycle integration, prioritization, query fusion, evidence
previews, holdout quality, 16 GB hardware measurements and final distribution
review remain required before release. This preview does not establish general
retrieval quality or all-format fidelity.

Developer verification (use an isolated add-on root and data directory):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-multimodal.ps1 -AddonRoot C:/Path/To/PrivateTestAddon -Device cpu
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_multimodal_setup.py
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_multimodal_worker.py
.runtime/semantic-search/cpu-venv/Scripts/python.exe runtime/test_multimodal_worker_inference.py --model .runtime/semantic-search/model --device cpu
dotnet run --project tests/Scout.Tests -c Release -- --multimodal-host C:/Path/To/PrivateTestAddon
```

Development setup also accepts explicit `--existing-model`, `--existing-office`,
`--cache-dir` and `--offline` arguments to `runtime/semantic_search/setup.py`.
Those imports are checksum-verified and copy only the renderer's inventory.
Diagnostic files and screenshots stay under ignored `artifacts/diagnostics/`.
