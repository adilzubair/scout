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
dotnet run --project tests/Scout.Tests -c Release -- --search-eval artifacts/diagnostics/task-benchmark/holdout.json artifacts/diagnostics/task-benchmark/local-report.json holdout
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

## Prospective authored screen (2026-09-25)

With no user task labels available, we built a new **authored** filename-only
screen using [20 synthetic document topics](../scripts/build-prospective-tasks.py).
Each topic has one intended file, four related decoys, two descriptive positive
queries and two close no-match queries, all constrained to its explicit folder.
The first 10 topics are development; the other 10 were held out by topic. This
gives 80 queries but only **20 independent target files**, so the percentages
below are especially uncertain. The source copied an indexed snapshot of
75,707 actual candidates and added 100 synthetic candidates. No file content
was read. The synthetic paths are index fixtures, not physical files.

The tasks and labels were frozen before inspecting model output. The ignored
benchmark's SHA-256 is `b8bce644b7aa6abc8ebb962bc0716c81e50ba6723d57e6ce8bcd25527b2cc497`;
its task-label input is `00c2c28a30453226fc2a185fa40a1327834e216e07cd0ca6df7114b2e4c9b820`.
All case-level reports and copied metadata remain under ignored
`artifacts/diagnostics/task-benchmark/`.

| Path | Development positive correct / 20 | Development false matches / 20 | Holdout positive correct / 20 | Holdout false matches / 20 |
| --- | ---: | ---: | ---: | ---: |
| Scout without AI | 0 | 0 | 0 | 0 |
| Current CPU Laya plus Scout policy | 3 | 4 | 2 | 5 |
| Ettin 150M, offline top-title score and development threshold | 9 | 2 | 10 | 3 |

Scout shortlisted 18/20 development and 19/20 holdout positive targets. The
CPU Laya path made 38 development and 37 holdout requests, exceeding its
two-second deadline in 17 and 23 respectively. The Laya holdout line is a
**complete C# result replay** with saved model predictions. The Ettin line is
only an **offline first-choice/admission estimate**: no Scout runtime path,
actions, grouping, packaged memory or end-to-end latency was exercised. It
scored 38 development and 37 holdout all-file shortlists. At four CPU threads
on this workstation, Ettin 150M's holdout title-scoring p50/p95 was 32/38 ms;
the Python probe's end private bytes were about 1,002 MiB. Those are not
ordinary-laptop measurements.

We screened Ettin 32M, Ettin 150M and mxbai xsmall on development only. With
at most two development false matches, their admitted correct positive counts
were 2, 9 and 7 among the 19 positive cases with file candidates. We selected
Ettin 150M and froze its score threshold at `10.095595359802246` before the
holdout run. The [admission script](../scripts/compact-admission.py) records a
policy hash of `ac3dd9f0d36f294e323da4162f770fa749fc909875b352b4710ce842f1e85e2b`.
We did not retune it on holdout. Its 10/20 with 3/20 false matches is a useful
engineering lead, but is not good enough to make it Scout's default.

Reproduce the authored screen and policy selection with ignored outputs:

```powershell
.runtime/.venv/Scripts/python.exe scripts/build-prospective-tasks.py --base artifacts/diagnostics/search-upgrade/frozen-v2.json --source-out artifacts/diagnostics/task-benchmark/prospective-source.json --tasks-out artifacts/diagnostics/task-benchmark/prospective-tasks.json
.runtime/.venv/Scripts/python.exe scripts/freeze-task-benchmark.py --source artifacts/diagnostics/task-benchmark/prospective-source.json --tasks artifacts/diagnostics/task-benchmark/prospective-tasks.json --output artifacts/diagnostics/task-benchmark/prospective-frozen.json
dotnet run --project tests/Scout.Tests -c Release -- --search-eval artifacts/diagnostics/task-benchmark/prospective-frozen.json artifacts/diagnostics/task-benchmark/development-local.json development
.runtime/.venv/Scripts/python.exe scripts/ettin-onnx-probe.py --fixtures artifacts/diagnostics/task-benchmark/development-local.json --model .runtime/models/ettin-reranker-150m-v1 --output artifacts/diagnostics/task-benchmark/development-ettin-150m.json --threads 4
.runtime/.venv/Scripts/python.exe scripts/compact-admission.py --development artifacts/diagnostics/task-benchmark/development-ettin-150m.json --max-false-matches 2 --policy-out artifacts/diagnostics/task-benchmark/ettin-150m-policy.json
```

The Python builders and probes refuse to overwrite their outputs; choose new
paths for a new run. To test the saved policy, run the same search evaluation and Ettin probe
with `holdout` paths, then invoke `compact-admission.py --policy ... --holdout
...`. The threshold was selected using a small authored development set, so
its holdout result is not a population estimate.

**Decision:** keep the shipping default unchanged. Prototype Ettin 150M as a
bounded, optional title reranker only after a separate no-match verifier and
candidate-recall improvement are tested. Before a default switch or an Intern
comparison claim, collect observed Windows tasks and run the same task set in
both products, then test package size, cold start, resident memory and p95
latency on 8–16 GB CPU-only laptops. None of this requires a Jev API key.
