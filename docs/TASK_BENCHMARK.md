# Scout task benchmark — 2026-09-25

## Purpose

Choose the next search model and admission rule using **final Scout results**
on independently labelled Windows tasks. A task is a query a person would type,
the indexed file/app they expect (or `no match`), and optionally its action,
folder, format and group. This is a key-free local evaluation; no Jev account
or file contents are required.

The existing 64-case semantic-helper fixture is now **development evidence**.
It has 32 authored positive and 32 authored negative filename descriptions.
On the checked CPU run, 29/32 positive targets reached the model shortlist and
Scout returned 36/64 correct final outcomes, with 13/32 negative descriptions
incorrectly matched. It is too small and has been inspected too often to choose
a new default model. [Baseline](LIGHTWEIGHT_BASELINE.md).

## Retrieval experiment and decision

We tried admitting every eligible file from a uniquely resolved small folder
when a description shared no title words. On the same 64 development cases,
positive shortlist recall rose to **32/32**, but Scout's final CPU Laya result
fell to **32/64**, with **16/32** negative descriptions incorrectly matched.
The three newly reachable positive targets were still rejected. The wider
retrieval was removed from the default path. These two separate CPU model
runs are subject to inference variability, but the added false match and lack
of benefit on the three missing targets are enough to defer this expansion.

The failure has two layers: some metadata-only descriptions do not retrieve a
candidate, and the present model/admission policy cannot reliably distinguish
the correct description from a close negative once candidates are supplied.
The next model or policy must demonstrate both higher positive coverage and
fewer false matches on a *new* task set. No threshold should be selected using
the frozen holdout's own labels.

Separately, the parser now treats `markdown recording the technical choices we
made` as a Markdown description rather than a video or creation-date query.
That format/intent correction is a narrow production fix: replaying the saved
CPU answers with the parser change alone raised positive shortlist recall from
29/32 to **30/32**, while final outcomes stayed **36/64** and false matches
stayed **13/32**. It does not solve the model's semantic no-match problem.

## Freeze manually labelled tasks

Store the source dataset, task labels and output under ignored
`artifacts/diagnostics/`. They may contain private filenames. Use a recent
indexed-candidate snapshot for the intended task date; a stale snapshot may
label a real file as absent. The [freeze tool](../scripts/freeze-task-benchmark.py)
accepts the dataset format already produced by
[`build-search-evaluation.py`](../scripts/build-search-evaluation.py), but
replaces its generated cases with manually supplied labels. It refuses to
overwrite an existing benchmark and records SHA-256 hashes for both inputs.

Example task file (substitute real indexed titles or full paths):

```json
{
  "cases": [
    { "query": "open my project roadmap", "expected": "Roadmap.pdf", "family": "descriptive-file" },
    { "query": "report about an unrelated missing project", "expected": [], "family": "no-match" },
    { "query": "preview the latest invoice", "expected": "Invoice.pdf", "action": "preview", "family": "action-and-time" }
  ]
}
```

`expected` accepts one unique indexed title, candidate ID or full target path;
use a path or ID when titles repeat. `[]` explicitly means no match. Optional
fields include `id`, `split` (default `holdout`), `mode` (`one`, `set`, `empty`),
`count`, `order` (tiers of equivalent IDs/titles), `folder`, `direct`,
`extensions`, `basis`, `action`, and `literal`. The evaluation runner currently
uses `Scope.All`; scope-specific tasks need an extension to the runner before
they can be scored as such. Keep related queries in the same split and report
unique targets and task families alongside case counts.

```powershell
.runtime/.venv/Scripts/python.exe scripts/freeze-task-benchmark.py --source artifacts/diagnostics/search-upgrade/frozen.json --tasks artifacts/diagnostics/task-benchmark/tasks.json --output artifacts/diagnostics/task-benchmark/holdout.json
dotnet tests/Scout.Tests/bin/Release/net10.0/Scout.Tests.dll --search-eval artifacts/diagnostics/task-benchmark/holdout.json artifacts/diagnostics/task-benchmark/local-report.json holdout
```

The summary now reports `positives`, `positiveShortlistRecall`,
`positiveFinalCorrect`, `negatives`, `negativeCorrect` and `falseMatches`.
`shortlistRecall` remains for older report consumers but counts empty cases as
vacuously recalled, so use **positiveShortlistRecall / positives** when judging
candidate generation. The evaluation JSON also records each case's final
result, scope/intent verdict, model shortlist and elapsed C# search time. The
latter excludes model inference, UI and indexing.

## Release decision

Freeze and hash the tasks before tuning. Develop on separate cases. Then run
Scout without AI, the current Laya path, and any proposed small model plus
admission policy on the untouched tasks. Score final file choices and parsed actions,
positive recall, unsupported matches, exact names, recency, folder/scope,
plural groups and cold/warm latency. A CPU-only 8–16 GB Windows laptop must
also meet the measured package, RAM and p95 latency gates. Intern can be
compared through the same visible tasks in its UI; this requires no Jev API key.

Manually labelled real tasks have been requested but have **not yet been
frozen**. This document and the current authored fixture are infrastructure
and development evidence, not an independent product quality estimate.
