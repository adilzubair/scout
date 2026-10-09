# Scout search index redesign — 2026-10-09

Implemented on `feat/fsearch-index-redesign`, starting from the complete baseline
`fd800451624bb7fa1d64751f917e9e36056385dc`. The branch is available for review;
no merge, push or release was performed. Unrelated `.omo/` work was preserved.

The design takes ideas from [fsearch](https://github.com/noahdunnagan/fsearch)
and adapts them to Scout's C# application, existing ranking and SQLite stores.
SQLite remains authoritative. The compact index is a disposable cache.

## Initial redesign: identical-coverage measurements

Both engines used the same frozen metadata, roots, query time and empty personal
library in a common Release harness compiled against their respective Core DLLs.
Each of eight queries received 10 warm-up passes and 30 measured passes.
Two separate-process rounds alternated baseline/redesign and redesign/baseline.
The table reports the median of the two rounds' aggregate percentiles. Ordered
hits, recall pools and notices matched in every retained round.

| Metadata corpus | Search p50, before → after | Search p95, before → after | Private memory, before → after | p50 reduction |
|---|---:|---:|---:|---:|
| Frozen 75,707 candidates | 25.78 → 9.07 ms | 35.18 → 31.05 ms | 146.9 → 67.9 MiB | 64.8% |
| Synthetic 250,000 | 50.86 → 24.04 ms | 72.01 → 46.57 ms | 295.1 → 96.7 MiB | 52.7% |
| Synthetic 1,000,000 | 213.58 → 97.88 ms | 286.33 → 197.54 ms | 1,131.9 → 332.3 MiB | 54.2% |

Private memory falls 53.8% on the current corpus and 70.6% at one million entries.
These are steady Core-process measurements after releasing the input JSON and
candidate fixture and collecting garbage, before warming searches. They include
the runtime and catalog, but exclude the WPF application and model. They are not
peak memory measurements. Cache construction and fixture parsing together take
3.06 → 2.57 seconds, 4.83 → 3.75 seconds and 20.35 → 13.46 seconds respectively;
these timings are not filesystem crawl times.

| Resource | Current corpus, before → after | 250,000, before → after | 1,000,000, before → after |
|---|---:|---:|---:|
| Managed live bytes | 127.0 → 48.2 MiB | 274.0 → 74.7 MiB | 1,097.9 → 300.9 MiB |
| Mean allocations per measured search | 191.9 → 254.3 KiB | 7.35 → 0.33 MiB | 28.81 → 0.33 MiB |

### Regressions and target status

In the initial comparison, the 30% median improvement and 25% million-candidate
private-memory reduction targets were met. Current-corpus memory also stayed
below its permitted 10% increase.
The initial redesign meets aggregate p95 tolerance, but **missed the p95 target
for individual chronological queries**. The follow-up slice below addresses this.
An empty scope lookup also regressed proportionally:

| Query | Corpus | p95 before → after |
|---|---|---:|
| `latest 3 files` | Current | 14.95 → 37.78 ms |
| `latest 3 files` | 250,000 | 47.95 → 64.03 ms |
| `latest 3 files` | 1,000,000 | 181.01 → 304.28 ms |
| `notes in "C:\Benchmark\Project00001"` | Current, absent synthetic scope | 0.19 → 0.57 ms |

These initial measurements preceded the chronological optimization below.
Current-corpus allocations rose 32.5%, about 62.4 KiB per search, despite lower live memory.
Two rounds on one Windows machine are evidence for this workload, not a guarantee
for every filesystem or query distribution.

## Follow-up slice: chronological selection (`266f79f`)

Unconstrained date-only searches now scan timestamp ticks directly in the packed
base and overlay, retaining only a bounded queue of finalists. Ordinary scoring
then supplies their original scores, pins and rich payloads. Folder/type/size and
other restricted date-only queries apply their existing restrictions before a
tick-based queue rejection. Exact filenames, download evidence, opening history,
calendar endpoints, offline roots, deletion markers and stable ties are preserved.
The snapshot format and persistent catalog memory layout are unchanged.

This comparison measured the original `fd80045` baseline, previous redesign
`e20f071` and slice `266f79f` in the same Release harness. Two rounds used
baseline/previous/slice, then slice/previous/baseline; every query still received
10 warm-up and 30 measured passes. All three frozen fixtures retained their hashes.
Ordered hits, recall and notices matched across all 18 process runs.

| `latest 3 files` | Original p95 | Previous redesign p95 | Slice p95 | Reduction from previous |
|---|---:|---:|---:|---:|
| Frozen 75,707 | 20.44 ms | 31.47 ms | 1.91 ms | 93.9% |
| Synthetic 250,000 | 49.66 ms | 85.54 ms | 4.98 ms | 94.2% |
| Synthetic 1,000,000 | 205.34 ms | 420.06 ms | 20.57 ms | 95.1% |

The measured chronological query now meets the p95 tolerance on all three corpora.
This does not establish the same gain for every filtered or described recency query.

| All eight queries | Original p50 / p95 | Previous p50 / p95 | Slice p50 / p95 |
|---|---:|---:|---:|
| Frozen 75,707 | 30.01 / 43.47 ms | 10.77 / 29.02 ms | 17.48 / 29.06 ms |
| Synthetic 250,000 | 56.14 / 75.59 ms | 37.66 / 70.47 ms | 31.88 / 48.44 ms |
| Synthetic 1,000,000 | 211.43 / 309.04 ms | 197.67 / 349.75 ms | 160.65 / 258.94 ms |

**The overall 30% median target is missed at one million entries in this run:**
211.43 → 160.65 ms is a 24.0% reduction. The current and 250,000-entry reductions
are 41.7% and 43.2%; aggregate p95 is within baseline tolerance for all three.
The stronger median gains in the initial comparison above were not reproduced
consistently. The first current-corpus aggregate median also regressed versus the
previous redesign, 10.77 → 17.48 ms, despite the chronological gain.

An additional two-round current-corpus comparison checked that regression.
Its aggregate p50 was 10.66 → 9.25 ms and p95 25.55 → 20.27 ms. All results again
matched. Combining all four previous/slice current-corpus rounds gives p50
10.66 → 10.22 ms and p95 27.12 → 21.19 ms.
The inconsistency warrants further controlled profiling of ordinary-query timings;
this slice establishes a repeatable chronological gain, not a uniform improvement
across every query.

In the full three-build comparison, slice private memory was 68.2, 98.3 and
331.1 MiB, versus 67.9, 97.5 and 331.0 MiB for the previous redesign. The original
million-entry baseline used 1,134.8 MiB: the 25% memory-reduction target still passes.
No date postings or extra full catalog were added; selection uses O(result limit)
temporary memory. Allocations remain approximately 252.6, 339.1 and 339.1 KiB per
search. The additional small-corpus repeat measured about 329.1 MiB private memory
in both builds, substantially above the primary run; the cause of this process
retention difference was not established and is not attributed to index growth.

Release compilation has zero warnings/errors and 256/256 C# checks pass. Twelve
added checks cover date bases, tick ties, window endpoints, evidence priority,
launch-derived opening payloads, aliases/pins, restrictions, overlays/extras,
offline roots and mapped replay/compaction. The 504 compatibility cases and 80
prospective cases match the previous redesign in full ordered results, scores,
notices, intents, shortlists and model payloads, excluding elapsed time. Existing
predictions therefore remain valid; model/checkpoint/prompt settings are unchanged.

Raw evidence is retained under ignored `chronological-slice/`: `paired/`,
`frozen-repeat/`, `checks.txt`, `differential.json`, `builds.json` and
`slice-summary.json`. Earlier crawl, desktop and HTTP limitations above remain;
this query-only slice did not rerun physical crawling, desktop smoke or the
unchanged model adapter/HTTP suites.

## Crawl, startup, persistence and desktop checks

A common lifecycle harness created the same 1,000 physical synthetic text files
for each build. Content indexing and the model were disabled. Thirty watcher
samples cover ten creates, modifications and deletes.

| Lifecycle measure | Baseline | Redesigned |
|---|---:|---:|
| Initial complete crawl | 369.3 ms | 483.3 ms |
| First file available | 279.7 ms | 303.9 ms |
| Cached files available | 111.9 ms | 86.5 ms |
| Cached reconciliation complete | 396.3 ms | 99.5 ms |
| Watcher p50 | 499.7 ms | 499.1 ms |
| Watcher p95 | 514.3 ms | 527.8 ms |
| Additional persisted compact base | none | 339,544 bytes |

The final cold 1,000-file crawl is **30.9% slower**. Cached availability is 22.7% faster
and reconciliation is 74.9% faster. Normal changes caused no full rescans; watcher
p95 meets the two-second target. These are single lifecycle runs, not the paired
30-pass search protocol.

A separate million-entry synthetic persistence audit produced a 158,128,718-byte
base (150.8 MiB). Base construction took 6.21 seconds and persistence 33.26 seconds.
Five validated mapped reloads had p50 2.86 seconds and p95 3.36 seconds. Seeding the
million-row authoritative SQLite database and change journal took 377.45 seconds.
That setup cost is reported separately from reload and physical crawling. Reload
still validates the checksum and reconstructs lexical/look-up structures; it is
not an instant map of every in-memory structure.

### Expanded drive coverage

A normal, non-elevated `C:\` native enumeration found **346,795 eligible files**
in **33.64 seconds**, with **90 unavailable directories** and at most four workers.
It enumerated metadata only; it did not read 311.9 GB of logical file contents,
extract document text, write SQLite rows or measure full application indexing.
This expanded coverage is separate from the identical-coverage benchmark above.

The final isolated application audit retained **346,839 cached files** and 184 apps.
A cached restart reconciled the actual drive in **34.96 seconds**, with a sampled
peak of **951.5 MiB private memory** (content/model disabled). The authoritative
metadata database occupied 313,782,272 bytes (299.2 MiB). The active compact base
occupied 122,159,577 bytes (116.5 MiB); all retained bases together occupied
247,851,252 bytes (236.4 MiB). Retired files still held by snapshot leases can
remain until collection or a subsequent compaction cleans them up. The drive
contents changed slightly between audits, so these are expanded-coverage observations, not paired speed comparisons.

The 20 ms WPF heartbeat had p95 gaps of **46.7 ms during reconciliation** and
**80.5 ms during interaction**. Sixty rapid input changes plus the final result
completed in **522 ms**. The audit passed stale-query protection and real watcher
create/modify/delete checks; its largest watcher removal was one entry and no
normal update caused a full rescan. Occasional dispatcher gaps remain: worst
**234 ms during reconciliation** and **858 ms during interaction**. Cold app queries
on the expanded catalog took 438–760 ms end to end. The p95 responsiveness check
passes; these outliers prevent a claim that crawling or interaction never stalls.

The first cold application attempt was stopped after cache-count instability,
with peak private memory **2.18 GiB**. Defensive handling and regression tests were
added for stale deletion notifications: existing roots retain their metadata, and
recreated files update without a full rescan. Two subsequent completed drive audits
retained coverage without large subtree removals. The original instability's cause
was not conclusively captured; **a verified complete cold whole-drive indexing time
is still unavailable**. The 33.64-second native enumeration is not a substitute
for that missing measurement. This remains a validation limitation.

A preceding desktop run timed out on physical global-hotkey injection. Final
whole-drive audits explicitly used `SCOUT_TEST_HOTKEY=0` and report
`globalHotkeySkipped=true`; physical hotkey behavior is unverified in this audit.
The model and content extraction remained disabled throughout the drive tests.

## Compatibility and new feature coverage

The frozen 504-case corpus retains exactly the same ordered hits, recall pools,
model shortlists, locations, notices and existing intent fields. Timestamp
serialization changes to UTC preserve the original instants. The baseline and
redesign both score 472/504 locally: 359/391 positive cases and 113/113 known-empty
cases. All 391 positive targets appear in the shortlist. Scope and intent failures
remain zero. Thus the redesign adds no false matches to these compatibility cases.

The prospective authored task corpus has 80 cases and 75,807 candidates. Both
engines recall 37/40 positive targets and reject all 40 negatives locally, but
local final positive correctness remains 0/40. This redesign does not solve the
existing semantic-description ranking problem. Model queries, candidate payloads,
plural intent and context match the saved development/holdout requests exactly,
so their saved English CPU predictions were reused for both builds. The ranker,
prompts, checkpoint manifest and server are unchanged. Both model replays give
5/40 positive correct, 31/40 negative correct and **9 existing false matches**,
with no scope failures. No changed request was assigned an old prediction.

New-feature coverage is tested independently with committed synthetic content and
explicit expected matches; it is not counted as a speedup over unsupported baseline
features. Examples:

- `seng` can find `SearchEngine.cs` when ordinary eligible filename matches are
  empty. Fallback is deliberately limited to one alphabetic term of 3–12 characters.
  It is labeled an abbreviation match and cannot alone light the ready indicator.
  Short terms, unrelated weak matches and scope violations are rejected.
- `grep:"payment received" in:"C:\Selected" ext:txt size:<16mb mtime:<7d`
  searches literal text in an enabled content folder. Lowercase patterns use
  smart-case matching; uppercase patterns preserve case.
- `regex:"Budget\s+[0-9]+" in:"C:\Selected" ext:txt` uses .NET non-backtracking
  regex. Invalid or unsupported expressions return an explanation. Cancellation
  or the 250 ms verification budget produces an explicit partial-results notice.
- Up to three verified excerpts appear in the existing preview; plain-text files
  include line numbers. Fresh edits cannot return a stale content match.

Existing word-based content search remains available. Trigram shortlisting is
conservative, particularly for Unicode and regex patterns without a provable
required literal. Short patterns use bounded verification. Extraction keeps the
16 MiB file and 64,000-character limits and existing Office/OpenDocument support.
Content is separately opt-in for selected folders; drive selection does not enable
content extraction. Content-only hits and document text stay out of model requests.

## Implementation and validation

The base stores packed metadata columns and shared name/token tables, with a small
separate catalog for other item kinds. Directory ranges and direct-child postings
constrain retrieval. Rich candidates are materialized for shortlists or explicit
enumeration. Immutable overlays and deletions replace the full candidate dictionary.
Shared name work has a 32 MiB bound and one-minute idle expiry. Only provably safe
subsequence membership narrowing reuses an extended prefix; ordinary typo/synonym
matching rescores or uses its exact semantic cache key. Final rankings are never cached.

SQLite updates and generation journal entries share transactions. Mapped snapshots
validate format, bounds, checksum, generation, journal floor and root/exclusion
fingerprints. Unique snapshot files and atomically replaced manifests preserve
search leases and changes arriving during compaction. The background threshold is
`max(1000, min(50000, 5% of base))`; dirty state is also saved after ten idle minutes.
Reconciliation writes deltas, keeps IDs/activity evidence and preserves unavailable
locations instead of deleting their cached rows. Searches suppress offline coverage.

Drive selection is disabled by default and lists ready local fixed drives. Native
[Windows enumeration](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstfileexw)
uses large-fetch when supported, cancellation and four workers. Hidden/system
children, reparse points, offline placeholders, exclusions and inaccessible locations
are skipped. Windows Hidden/System attributes on the volume root itself do not
incorrectly mark a selected drive unavailable. Overflow, startup and 30-minute
reconciliation restore watcher coverage; disabling the setting removes extra coverage.
The separate private content database maintains word and
[trigram FTS5](https://www.sqlite.org/fts5.html#the_trigram_tokenizer) tables transactionally.
These tables add disk/storage cost proportional to enabled extracted text; a large
real-document content-index size benchmark was not performed.

Release compilation passes with zero warnings/errors, and **256/256 C# checks pass**
after the chronological follow-up slice.
Coverage includes cached/uncached search, backspace, synonyms, typo thresholds,
aliases, scopes, pins, content-only hits, corrupt/stale snapshots, restart replay,
concurrent compaction, directory rename, normal watcher changes, overflow, settings,
unavailable roots, Unicode/long paths, exclusions, content clearing, regex errors,
partial searches, extraction limits and model-request privacy. Isolated desktop
and UI smoke tests pass, including PDF/image/Office/text previews and the Folders
settings page. The Python adapter/runtime suite passes 13 tests.

**The real-checkpoint HTTP test remains failing on this machine:** its two-candidate
CPU inference exceeds the unchanged two-second deadline (2.156 and 2.578 seconds
in isolated attempts). Authentication, origin rejection and health checks passed
before that assertion; later HTTP assertions were not reached. No deadline,
model or prompt was relaxed to turn this failure into a pass. The server, ranker,
model manifest and HTTP test sources are byte-equivalent to the baseline after
newline normalization. This is an unresolved runtime timing limitation, not a
claimed successful HTTP check.

## Folder-picker correction from user testing

Clicking Browse in Folders settings terminated Scout with an unhandled
`REGDB_E_CLASSNOTREG` (`0x80040154`): the custom COM helper used an unregistered
class identifier. The earlier UI smoke covered navigation and folder-list editing,
but did not open the native picker and therefore missed this defect.

The helper now uses the framework's
[OpenFolderDialog](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.openfolderdialog?view=windowsdesktop-10.0)
with the Settings window as its owner. All four folder lists share the corrected
helper; picker failures show inline feedback and allow entering a path manually.
An isolated, opt-in native audit exercises the real content toggle and Browse
button, selects a Unicode folder, cancels without modifying the list, checks
repeat-selection deduplication, opens/cancels the other three Browse buttons and
verifies that Settings navigation still responds afterward. All seven checks pass;
the Release build has zero warnings/errors and the 256 regular checks also pass.
No user settings are saved by the audit.

```powershell
dotnet run --project tests/Scout.Tests -c Release --no-build --no-restore -- --folder-picker-smoke IGNORED_REPORT_JSON
```

The command requires an interactive Windows desktop. Native audit evidence and
regular-check output are retained under ignored `artifacts/diagnostics/folder-picker-fix`.
This App-only correction does not change the search engine or earlier performance
measurements.

## Settings and indexing readiness follow-up

The Folders page now combines additional folders and content folders into one
**Search folders** list. Built-in locations appear in the same list. Add/Browse
is above the rows; Enter adds a typed path. Each folder has a **Search contents**
checkbox, with extraction off for newly added folders. Existing enabled content
scopes are preserved. A subfolder covered by a selected parent shows an inherited
checkmark and explains where to change that setting; overlapping scopes do not
inflate indexed counts. Exclusions and automation folders are collapsed under an
advanced expander. Whole-disk filename coverage remains independently opt-in.

Settings wheel movement is 32 pixels per 120-unit detent, with proportional
precision-wheel movement and clamping at each edge. Multiline fields scroll
independently and hand wheel movement to the page at their boundaries. Touch
panning stays vertical. The layout retains keyboard focus, validation feedback,
theme resources and the reduced-motion preference.

The compact launcher now displays indexing status even when no query is entered:
**Preparing search**, **Indexing file names**, **Indexing contents**, then
**Search ready**. Clicking that status opens Folders directly. The page shows
live stages and counts; each active folder reports filename readiness and, when
selected, content readiness with its indexed document count. Unavailable locations
and partially readable trees are flagged. Explicit content queries explain when
their index is still being prepared. Filename searching and UI refresh continue
while contents are being indexed. Saved preferences are persisted before the
background reconciliation so another Settings window reflects the active request.

Counts come from directory metadata and the private content database on background
workers, rather than materializing the complete file catalog on the UI thread.
The filename count includes filenames with unsupported content types; the
document count includes only successfully extracted, nonempty text. Existing
16 MiB / 64,000-character limits, exclusions, selected-folder restrictions and
local-only content handling remain in place. There is no discovery percentage:
the total is unknown during enumeration. Readiness describes the last completed
reconciliation; watcher updates and new extraction still have a delay. Content
readiness waits for reconciliation of all selected content scopes, and partial
folder access can leave some documents absent.

The native audit uses a diagnostic application host that loads Scout's resources
without invoking normal startup, claiming the launcher mutex or opening user data.
It exercises the unified Browse flow, explicit content opt-in, Unicode selection,
Cancel, deduplication, both advanced pickers and subsequent Settings navigation.
Evidence stays under ignored artifacts/diagnostics/settings-ux; this follow-up
does not change search ranking or replace the earlier paired performance results.

Validation: Release build succeeded with zero warnings/errors; **263/263** regular
checks and **7/7** native picker checks passed. The isolated desktop smoke passed
navigation, validation focus, Enter-to-add, initially visible Browse, proportional
page scrolling, multiline scrolling and edge handoff at fractional DPI, plus
light/dark rendering and existing launcher workflows. Tests cover cold and cached
startup stages, separate filename/content readiness, pending-search explanations,
content reconciliation failure, Unicode/overlap counts, clearing, scope replacement
and unavailable-drive status. The currently running launcher was left open;
the verified executable is in the ignored settings-ux/build/bin/Scout.App/release
directory. Exit the running Scout before opening this copy.

## Slash filters and independent directory indexing follow-up

Slash filters are optional and preserve existing unprefixed searches, natural-language
folder commands, exact filenames/paths and advanced operators. `/folder projects`
returns directories by name; `/folder` includes indexed empty directories. `/image`
lists images and `/image holiday` narrows by name. `/pdf`, `/file`, `/video`, `/audio`,
`/document`, `/spreadsheet`, `/presentation`, `/archive`, `/app`, `/link`, `/workspace`
and `/all` are also supported. Type `/` for help. Filters respect the selected scope
tab; words following them remain name terms, including words such as "last week".
Use `mtime:<7d` for an explicit age restriction. Ordinary body-word search stays in
unprefixed queries; explicit `grep:`/`regex:` can be combined with file filters.
Slash searches use local ranking and omit web fallback; launching records activity
without learning the whole filter as a personal alias.

Previously folders were inferred from files and selected with folder phrases or
absolute paths. Native discovery now creates stable Folder candidates independently,
using the existing SQLite journal, compact index and mapped snapshots. Empty folders
therefore remain searchable after restart, and directory renames/deletes update their
names and scopes. Hidden/excluded directories remain excluded, offline drive folders
are suppressed without discarding their cached records, and disabling drive coverage
removes its directory records as well. File/content counts exclude directory rows.
Existing installs discover these additional rows during background reconciliation.

This supersedes the always-visible completed launcher status from the preceding
Settings follow-up: progress/problems remain visible, but successful completion
collapses the status row. Full readiness/counts remain available in Settings → Folders.

Validation: Release build with zero warnings/errors, **290/290 C# checks**, and the
isolated desktop smoke passed. New checks cover all filter families, quoted paths,
operators, empty/Unicode directories, cache switches/backspace, content-only hits,
content cleanup with mixed directory metadata, snapshot corruption/replay, watcher
create/rename/subtree deletion, restart, exclusions, offline drives and retained folder
activity. UI captures verify slash results/help, no web fallback, compact ready state,
light/dark rendering, settings scrolling and existing launcher workflows.
The same 504 compatibility cases and 80 prospective cases retain identical ordered
hits, recall, shortlists, locations, notices, existing intent fields and correctness
on identical candidate coverage. No predictions were regenerated: payloads remain
identical in those cases. Expanded directory coverage is a separate feature change.

One isolated 1,000-file/11-directory lifecycle run measured 731 ms initial crawl,
421 ms first availability, 72 ms cached availability and 110 ms reconciliation.
The base occupied 345,156 bytes; 30 watcher updates had p95 515 ms and no normal
full rescans. These are synthetic, single-run observations, not a paired speed
claim or a whole-drive measurement. Directory rows add storage/name-index work;
whole-drive memory and crawl costs were not remeasured for this follow-up. The
previous paired performance results and missed targets above remain unchanged.
Evidence and the verified test executable are under ignored
`artifacts/diagnostics/folder-search`; the running user instance was left open.

## Reproduction and private artifacts

Private snapshots, filenames, content, binaries and raw reports remain under ignored
`artifacts/diagnostics/fsearch-redesign`. `verified-paired/comparison.json` is the
aggregate search report; `baseline-lifecycle.json`, `verified-lifecycle.json`,
`snapshot-1000000.json`, `drive-enumeration.json` and `quality-differential.json`
provide the corresponding audits. Earlier exploratory reports are not final evidence.
The baseline app/test binaries and full commit SHA are retained in `baseline`.
To compare the launchers manually, run `baseline/Scout.exe` and the rebuilt
`src/Scout.App/bin/Release/net10.0-windows10.0.19041.0/Scout.exe` sequentially,
each with a different isolated `SCOUT_DATA_DIR`. Use the same configured roots
and a copied frozen database for compatibility comparisons; enable whole-disk
coverage only for the separate expanded-coverage trial. Content mode examples
require explicitly enabled content folders in the redesigned profile.

Frozen SHA-256 values:

| Fixture | SHA-256 |
|---|---|
| Existing metadata corpus | `9c11d7623f395043f5477347f498db3fd81fc73a598ccb74b3cc229b534e8a9b` |
| Prospective task corpus | `b8bce644b7aa6abc8ebb962bc0716c81e50ba6723d57e6ce8bcd25527b2cc497` |
| Synthetic 250,000 | `9917611863346210e6c9ff48ce5b5203f6df4c525e6e88a95346219898dad801` |
| Synthetic 1,000,000 | `70cb8350918a12ce9ea8f11a3f87cd2f6f8e06f6d7746293745b69b47842054e` |

The committed fixture generator is deterministic and refuses to overwrite an
existing fixture. Synthetic metadata includes repeated names, nested directories,
Unicode, aliases and timestamp ties; it provides no evidence of real crawl speed.

```powershell
dotnet build Scout.slnx -c Release -m:1
dotnet run --project tests/Scout.Tests -c Release --no-build --no-restore
.runtime/.venv/Scripts/python.exe runtime/test_runtime.py
.runtime/.venv/Scripts/python.exe runtime/test_http.py
```

For the paired benchmark, build `tests/Scout.Benchmark` twice into separate ignored
output directories, setting `ScoutCoreAssembly` to the preserved baseline Core DLL
and then to the redesigned Release Core DLL. Use the same harness source for both:

```powershell
dotnet build tests/Scout.Benchmark -c Release -p:ScoutCoreAssembly=ABSOLUTE_CORE_DLL_PATH -o IGNORED_OUTPUT_DIRECTORY
python runtime/index_comparison.py --baseline BASELINE_HARNESS_DLL --redesigned REDESIGNED_HARNESS_DLL --corpus FROZEN_CORPUS_JSON --corpus SCALE_250000_JSON --corpus SCALE_1000000_JSON --output IGNORED_REPORT_DIRECTORY
```

`--previous INTERMEDIATE_HARNESS_DLL` adds a third build to the same alternating
rounds. `--aggregate-only` rechecks retained reports without rerunning engines.
The lifecycle harness similarly accepts `ScoutCoreAssembly` and `ScoutAppAssembly`. The test
runner exposes `--index-fixture OUTPUT COUNT`, `--index-snapshot FIXTURE OUTPUT`,
`--index-lifecycle OUTPUT` and `--index-crawl ROOT OUTPUT`. Real desktop diagnostics
require an isolated `SCOUT_DATA_DIR` and `Scout.exe --diagnose`. Setting
`SCOUT_TEST_HOTKEY=0` explicitly skips and records the physical hotkey check;
`crawl-audit.jsonl` under the private snapshot directory records aggregate
reconciliation/deletion counts in diagnostic mode.

Local milestones: `e0c7ae5` benchmarks, `af9d74c` compact base, `79a5208` snapshots
and replay, `41a8cbe` retrieval cache, `3885c46` checkpoint/tie protection,
`09e6412` matching/content, `8eaf3b8` drive indexing, `e289fb1` excerpts/verification,
`ad40a45` allocations, `6a3f809` volume-root correction, `a064f94` comparison tools,
`647662f` base-size compaction thresholds and `604629c` stale-delete protection,
diagnostic counters and background publication.
