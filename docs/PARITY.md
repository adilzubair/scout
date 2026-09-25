# Intern on Windows

Behavioral reference: `dabit3/intern` commit `c0bedee4b374b5f177b670016f5ff2b8e5bcf7eb`, cloned under `.reference/intern`. Reference code/assets are not included in Scout's package. Scout is independently implemented in C#/WPF and Python.

Implemented behavior:

- Fast local results, asynchronous Laya decisions, cancellation, stable manual selection, explicit Enter before execution.
- Apps, files, browser links, pins, learned aliases, scopes and editable workspaces.
- Named and absolute folder restrictions, opening known folders, bounded latest-N groups, literal filename/path matching, and screenshot evidence. The real-file desktop audit covers these separately from the authored model benchmark.
- Separate created, modified, arrived/downloaded, opened and visited evidence; natural language time windows; missing evidence must not invent a match.
- Calculator, web fallback, Windows system actions, named user automations, file previews and native icons.
- Suggested Open all rows with editable membership; confidence/readiness that cannot activate a stale result.
- Local Laya evaluated on realistic distractors, paraphrases, temporal queries and ambiguity, including GPU latency. A compatible API is not evidence of Jev-level accuracy.

Verification is documented in `VALIDATION.md`. Readiness uses clear local evidence, while raw model readiness is retained internally pending broader calibration. Semantic groups remain constrained by candidate eligibility and explicit user confirmation. Windows system mutations are implemented but excluded from automated execution tests. Live macOS/Jev equivalence has not been measured.

The [ordinary-laptop baseline](LIGHTWEIGHT_BASELINE.md) records why Scout's
current CPU Laya stack cannot be treated as an Intern-quality default. The
standard Settings flow no longer offers the 4B helper, which improves the
authored semantic suite but exceeds the target footprint. A direct Intern/Jev
head-to-head and a fresh compact-model holdout are release gates for a parity
claim.

Windows mappings: Spotlight → Windows Search and local file watchers; Finder recent use → Windows Recent Items and Scout launches; Chrome history → Chrome/Edge visits and completed downloads; Quick Look → bounded built-in previews; Shortcuts → user-selected Windows shortcuts/Power Automate links; Focus → Windows notification settings; Trash → Recycle Bin.

Creation means the filesystem's creation timestamp, which can change when a file is copied. It does not establish document authorship. Recent Items depends on Windows/application history support; filesystem access times are not treated as proof of opening.
