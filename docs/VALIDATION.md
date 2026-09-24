# Validation

Latest search upgrade (2026-09-24): **259/271** final validation cases, compared
with **213/271** for the saved baseline; **176 C# checks** and **13 Python adapter
checks** pass. See [the search evaluation](SEARCH_EVALUATION.md) for the frozen
metadata corpus, prompt experiments, current latency, remaining descriptive
misses and reproduction instructions. Earlier measurements are retained below.

Reference: Intern commit `c0bedee4b374b5f177b670016f5ff2b8e5bcf7eb`. Windows 11, .NET 10.0.400, Python 3.12.13, Laya 0.3.7, Transformers 4.57.6, PyTorch 2.8.0+cu128, NVIDIA RTX 5060 Laptop GPU (8 GB). Checkpoints are pinned to `5e7b2b1b8ca2ecdd3f2322d94069c9b6ce7e844b`.

## Bluetooth command gap — 2026-09-24

Wi-Fi commands had native handlers; Bluetooth commands were missing from both retrieval and execution. Added Bluetooth on/off with common phrasing, asynchronous Windows radio access, Bluetooth-only device selection, idempotence, timeout/denial messages and confirmation of the actual reported radio state before closing the panel. Repeated Enter cannot start concurrent system operations.

The C# suite now passes **154/154 checks**, including 22 new checks for command phrasing, ambiguity, scopes, isolation from Wi-Fi, multiple adapters, permission denial, unavailable/disabled hardware, cancellation and accepted requests that fail to change state. The Windows desktop diagnostic passes six command-selection checks and calls the real Bluetooth setter with the current state. Windows grants access, accepts the setter, and completes the actual UI action; Bluetooth and Wi-Fi states are preserved. Real off/on transitions were not performed, to avoid interrupting connected peripherals; transition behavior is covered with simulated radios.

Reproduce the hardware check with an isolated `SCOUT_DATA_DIR` and `Scout.exe --bluetooth-diagnose`. It writes `desktop-diagnostics.json` and exits. It never deliberately toggles the radio during this diagnostic.

## Real-file bug audit — 2026-09-24

The reported query `latest file in telegram folder` failed on the original 63,609-file index even though the Telegram files were present. Location words were treated as filename terms, excluding ordinary files inside that folder. The original audit also reproduced failures for Downloads, folder/type combinations, date ordering, counts, and filenames containing action words. The failing baseline is retained locally.

After the fixes, 34 filesystem-grounded cases pass with no skips. Expected paths are derived by independently enumerating real folders and reading filesystem timestamps, rather than by asking the parser/ranker for its preferred answer. The checks cover nested/direct children, quoted and absolute locations, latest/oldest/created ordering, today/yesterday, exact counts, PDF/Word/spreadsheet/archive types, Screenshots, folder opening, duplicate filenames, exact file paths and absent matches. No eligible presentation exists in the tested Documents folder: that case correctly requires zero results. Plain duplicate-name queries accept either exact filename while checking that the intended file remains available and the action stays Open.

The packaged Windows app passes all 34 cases before and after background enrichment/refinement, plus a real company-profile query with Laya enabled: **35/35 desktop search checks**. It indexes 63,850 real files after adding Pictures and the Windows Screenshots known folder. The expanded desktop test caught project screenshot folders leaking into a standard Screenshots search; known Windows/configured roots now take precedence. Every displayed hit and every model candidate remains constrained to the requested location.

Time from assigning a query in the actual WPF input to rendered local results: **57 ms median, 78 ms p95**, with a **161 ms first-query maximum**. Search-engine time was generally 16–32 ms; these are different measurements. Explicit chronological results no longer wait for Laya. A scoped company-profile inference completed in 125 ms and retained the correct PDF. The test also verifies editable three-file groups, manual selection surviving refresh, rapid typing cancellation, and valid directory actions without launching personal files.

The full C# suite passes **132 checks**, including new folder, literal-name, action, recency, screenshot and count regressions. Python adapter checks and the authenticated HTTP tests pass, including folder candidates. The final package also passes actual PDF/image/Office/text previews and CUDA model-host checks, installed app selection, Ctrl+Alt+Space hide/show, and file create/modify/delete watching without extra full scans. UI heartbeat gaps were 46 ms p95 and 70 ms maximum in that run. Private snapshots, paths and reports remain under ignored `artifacts/diagnostics`; none are distributed in the application ZIP. These cases substantiate the fixes on this PC, not a general accuracy guarantee or an Intern/Jev comparison.

To reproduce: run `Scout.Tests.dll --realworld <snapshot.db> <audit.json>` against a read-only backup copied into the workspace. Set `SCOUT_DATA_DIR` to an isolated directory containing that snapshot as `scout.db` and the audit report as `audit-input.json`, then run `Scout.exe --realworld-diagnose`. The diagnostic uses the copied settings, refreshes actual metadata, exercises the UI and local Laya, writes `desktop-diagnostics.json`, and exits. Close the normal Scout instance during desktop tests.

## Search quality

The new challenge corpus exercises names, prefixes, typos, creation/modification/opening distinctions, file-type families, system actions, natural descriptions, irrelevant queries and ambiguity. Assessment uses the actual C# retrieval and final ranking policy, including exact-name protection and abstention. It does not count a correct raw model guess that the app would reject.

| Corpus | Local only | Local + English Laya | Ambiguity checks |
|---|---:|---:|---:|
| Development, 56 target/no-match cases | 43/56 | 56/56 | 2/2 |
| New filenames and descriptions, 40 target/no-match cases | 21/40 | 39/40 | 2/2 |

The development set was used to choose prompt wording and the semantic-admission threshold. The second set used different documents/topics and was evaluated afterward. Its remaining miss was `pdf proof that I finished university`: the model's answer was too uncertain to admit `Diploma.pdf`. No false match was admitted for its seven unrelated or missing-evidence queries. These are small authored fixtures, not representative user telemetry, and the percentages are not a general accuracy guarantee. Groups have separate deterministic/interaction regression checks; these top-one figures do not measure semantic group precision.

The concise English retrieval prompt improved results materially over the earlier verbose JSON prompt. The earlier 200-case benchmark had repeated, easy lexical families and is retained as historical evidence only. It should not determine the current default checkpoint.

## Model latency

Full local rank calls, including typed metadata questions and extra membership passes for plural queries; warm-up excluded:

| English Laya on CUDA | p50 | p95 | Maximum |
|---|---:|---:|---:|
| Development corpus | 47 ms | 78 ms | 94 ms |
| New corpus | 47 ms | 94 ms | 94 ms |

Process/model initialization was about five seconds. These are Python rank-call measurements, not total keystroke-to-render times. The app additionally debounces input and performs retrieval/HTTP transport. CPU remains supported and passed the HTTP/adapter tests; the old CPU performance figures are not comparable with this expanded adapter. Upstream temperature warnings remain, so the UI does not interpret raw model confidence as calibrated certainty.

## Real Windows checks

An isolated test database indexed **63,595 files** and discovered **178 apps**. Apps were ready in about **568 ms**. The correct installed Notepad, Chrome, Edge, VS Code, Excel, Word and Terminal results rendered in **36–88 ms** during startup. Rapid typing measured **47 ms p95 UI heartbeat gaps**, with a 112 ms maximum in this run. Actual Ctrl+Alt+Space hide/show and file create/modify/delete events passed. File changes and reopening did not trigger extra full scans.

The desktop smoke test passed scopes, stale/manual selection behavior, arrival ordering, editable Open all, arithmetic, web fallback, GPU ModelHost integration, and actual Windows PDF/image/Office/text preview loading. Test instances exit automatically. Sleep, lock, Wi-Fi mutation, theme changes and Recycle Bin deletion are not executed by automated tests; their candidate selection and fixed command dispatch boundaries are verified without changing the PC.

Build/tests: zero compiler warnings; over 100 C# checks, 10 Python adapter checks, authenticated loopback HTTP and Jev-compatible endpoint checks. Runtime tests cover 32-candidate coverage, overlong titles, complete probability distributions, batching equivalence and incomplete-group deadlines. Preview tests reject external XML entities and bound document reads.

Reproduce with `scripts/build.ps1 -Publish`, `runtime/test_runtime.py`, `runtime/test_http.py`, and `scripts/evaluate.ps1 -Device cuda`. Outputs are in ignored `artifacts/evaluation` and `artifacts/diagnostics` directories. Diagnostic databases can contain private filenames; they are never included in the application ZIP.

## Limits

No live Jev account comparison or macOS execution was performed. Windows Recent Items, creation timestamps, indexed folders and preview codecs differ from their macOS counterparts. The adapter compares up to 32 retrieved candidates; it cannot find files outside the configured/indexed sources or infer facts that are absent from names and metadata. Laya may abstain on unfamiliar descriptions. Semantic probabilities remain uncalibrated beyond these small evaluations.
