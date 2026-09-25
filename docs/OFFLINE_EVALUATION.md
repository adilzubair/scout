# Offline search evaluation — 2026-09-25

No TypeSafe account or Jev API key is required for Scout's next decisions.
Intern's [public README](https://github.com/dabit3/intern) supplies a behavioral
checklist: fast local results, descriptive file requests, distinct creation/
download/open evidence, browser history, scopes, groups, workspaces and explicit
execution. Scout can test those workflows on Windows without contacting Jev.
That establishes feature coverage, not equal semantic model accuracy.

## First compact CPU screen

Two already cached cross encoders were run offline against the previously
inspected 64-case authored filename-description set. The probe uses only the
query and candidate filename. Fifty-seven cases had at least one file candidate
to score: 29 positive and 28 negative. Three positive targets never reached
this candidate pool; a reranker cannot recover them. The other four cases had
no candidates, so no model request was needed. This probes ranking and
abstention separation, not Scout's final-result policy.

| Measurement | MiniLM local | mxbai xsmall |
| --- | ---: | ---: |
| Weight file | 90.9 MB | 141.7 MB |
| Correct first choice, recalled positives | 25/29 | 25/29 |
| Positive versus negative max-score AUC | 0.63 | 0.68 |
| CPU request median / p95, 4 threads | 10 / 15 ms | 46 / 73 ms |
| Best correct positives at no more than 4/28 false matches | 9/29 | 14/29 |

The last row is **optimistic**: its threshold was selected after seeing these
same cases. It is not a deployable threshold or an unseen accuracy estimate.
Both models rank plausible filenames quickly but are poor at deciding when no
file really fits. Neither is ready to replace Laya or become the standard
semantic path. Previous use of these models to rerank shortlists also failed to
show enough final-result improvement; see [the earlier audit](SEMANTIC_SEARCH.md).
CPU timings were measured on an Intel Core Ultra 9 275HX and exclude Python/
transformers import, retrieval, UI rendering and the full Scout policy. Model
initialization used cached files; it is not a clean-install startup result.

The local, reproducible probe is `scripts/compact-probe.py`. It refuses to
overwrite an output and loads models with `local_files_only=True`:

```powershell
$env:HF_HUB_OFFLINE = '1'
$env:TRANSFORMERS_OFFLINE = '1'
.runtime/.venv/Scripts/python.exe scripts/compact-probe.py --fixtures artifacts/diagnostics/semantic-helper/validation.json --model .runtime/models/relevance-minilm --output artifacts/diagnostics/offline/compact-minilm.json --threads 4
.runtime/.venv/Scripts/python.exe scripts/compact-probe.py --fixtures artifacts/diagnostics/semantic-helper/validation.json --model .runtime/models/relevance-mxbai --output artifacts/diagnostics/offline/compact-mxbai.json --threads 4
```

Fixtures and case results are ignored local files because they can include
private paths. The probe outputs only case IDs and scores, with a fixture hash
to identify the input. These cases are now development regressions, not a new
holdout.

## Decision path without Jev

1. Keep local name, app, folder, date and action handling as the always-on
   baseline, and test it against Intern's published workflows and leading
   Windows search tools. Record final visible results and UI latency.
2. Gather a new, independently labeled set of real Windows search tasks, with
   close distractors and unsupported requests. Split by task/domain before any
   tuning. Do not infer truth from document contents that Scout never indexes.
3. Screen compact models on CPU using end-to-end Scout results, false matches,
   cold start, memory and disk footprint. Test on at least one ordinary 8–16 GB
   laptop before enabling a model by default.
4. Once semantic search meets those gates, add deeper content search as a
   separately measured feature. Compare recall, update time, disk/RAM cost and
   privacy controls against Windows Search and Everything.

This process can support a claim that Scout meets specific user-task and
hardware targets. It cannot support a numeric claim that Scout's model equals
Jev when Jev itself has not been measured on the same tasks.

See [the Laya replacement research](LAYA_REPLACEMENT_RESEARCH.md) for the
current model shortlist and its offline evaluation gates.
