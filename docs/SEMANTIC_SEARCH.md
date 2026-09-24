# Descriptive search audit — 2026-09-24

Follow-up: an optional stronger local model now improves the fresh descriptive
benchmark from 38/64 to 56/64. See [LOCAL_SEMANTIC_HELPER.md](LOCAL_SEMANTIC_HELPER.md)
for the current architecture, tests, latency and remaining errors. The audit
below documents the preceding Laya-only phase.

The previous 20-case metadata-description validation set is now a development
diagnostic, since its failures are being inspected. Its 12 misses were six wrong
comparative choices, three abstentions, and three admission/blending failures.
Every expected item reached the shortlist and was considered by the adapter;
context truncation was not the cause in these cases.

One ranking defect discarded the model's influence whenever its largest choice
probability was below 0.45, even if two independent relevance checks strongly
supported that comparative winner. The final policy now preserves a supported
comparative winner while protecting exact matches and explicit chronological
ordering. A lower-ranked item's independent relevance cannot displace a stronger
comparative choice.

Replaying the saved predictions fixes two schema searches: 261/271 overall,
including 10/20 descriptions, versus 259/271 and 8/20. No additional inference or
threshold relaxation is needed. The 177 C# checks pass; existing authored suites
remain 56/56 and 39/40. This replay is a regression measurement, not a new unseen
accuracy estimate.

`runtime/audit_search.py` joins saved C# reports and adapter predictions to expose
choice, abstention, budget and admission failures. Its output contains private
metadata and belongs under ignored `artifacts/diagnostics/semantic-upgrade`.

## Broader evaluation and release decision

The next corpus was frozen before model experiments: **90 cases**, comprising
42 descriptions of real filename metadata and 48 authored positive/negative
challenges. Development has 44 cases; validation has 46, reserving entire new
domains (including architecture, deployment, security, backup, shipping and
accessibility). Related wording and duplicate project copies are correlated;
these are not 90 independent documents or a population accuracy estimate.

The metadata source contains 75,707 files. Ground truth for real files is inferred
from titles, not document contents. Broad descriptions can have other plausible
interpretations. Authored pools deliberately distinguish close alternatives,
such as a purchase receipt versus an invoice, and include unsupported requests.
No personal document contents were read or indexed.

Frozen corpus SHA256:
`e23c07564ebdef3583164efebd8aa86fb02bf31297c07d3f07577267a3348e58`.
The preserved baseline is `33459c4`; final production behavior adds the ranking
fix in `53717f7` and parser fixes in `5c75833`.

| Measurement | Baseline | Release |
| --- | ---: | ---: |
| Development correct | 27/44 | 32/44 |
| Reserved validation correct | 16/46 | 17/46 |
| Development shortlist recall | 42/44 | 44/44 |
| Validation shortlist recall | 37/46 | 44/46 |
| Cases with folder leaks, both splits | 2 | 0 |
| False matches, development negatives | 1/8 | 1/8 |
| False matches, validation negatives | 1/8 | 1/8 |

The parser now gives explicit quoted/absolute locations precedence over earlier
descriptive prepositions. For example, `notes from the team discussion in
"C:\Meetings"` retains the discussion words and enforces the folder. Mentioning
an application inside a PDF description no longer hides an explicit location.
A document "recording what was discussed" remains a document search rather than
becoming a video request.

The reserved result is weak: only **10/38 positive descriptions** succeeded,
including **0/22 real markdown descriptions**, even though those 22 expected
files reached the shortlist. Seven of eight unsupported requests were rejected;
one driving-license request incorrectly admitted a passport. Two authored
positive cases still missed retrieval. This is not evidence of Intern parity or
reliable general semantic understanding. The larger test set exposes limitations
that the older high overall pass rate hid.

## Model experiments

Development experiments compared the English, typed-decisions and multilingual
Laya checkpoints; smaller comparison groups; alternate filename/query wording;
and independent relevance checks across the whole shortlist before comparison.
None consistently improved upon the English adapter. Its production prompt,
checkpoint, acceptance thresholds and inference workload remain unchanged.

Two local helper rerankers were also evaluated for selecting four or eight
candidates before Laya: `cross-encoder/ms-marco-MiniLM-L6-v2` at revision
`233902d25c440f23af6f7d6e94d2946bac0bee0a` and
`mixedbread-ai/mxbai-rerank-xsmall-v1` at revision
`b5c6e9da73abc3711f593f705371cdbe9e0fe422` (both Apache-2.0).
They did not provide an accuracy advantage sufficient to justify their additional
latency. They remain ignored local experiment downloads and are not loaded by
Scout, included in setup, or distributed in the ZIP.

A proposed stronger relevance admission check rejected one development false
match but also lost two previously working calendar descriptions. It was
rejected after the full regression run. The reserved validation had already
been opened at that point; the table transparently reports the final release
replay. No prompts or model selection were tuned to its failures.

Validation inference with the unchanged English adapter took **93 ms median /
125 ms p95** on the local CUDA device (43 model requests, zero two-second
timeouts). These are adapter timings, not end-to-end UI latency. The C# fixes
add no model inference. The existing authored suites remain **56/56** and
**39/40**, plus **4/4** ambiguity checks. The C# suite passes **180/180** checks;
the 13 adapter contracts and authenticated HTTP checks also pass.

The full prior 504-case real-metadata corpus now passes **484/504**, versus
482/504 at the start of this phase. All 113 negatives remain empty, at least one
expected answer reaches the shortlist in every positive scenario, and no
folder/type scope leaks occur. These reused cases are regression measurements,
not unseen tests.

The published Windows app passes **41/41 desktop checks** against 75,714 live
indexed files, including the real Telegram company-profile inference, edited
groups, retained selection, rapid typing and native folder validation. The 40
ordinary file queries rendered local results in **67 ms median / 124 ms p95**;
the scoped Laya inference reported 172 ms. The test used an isolated backup of
the user's settings/index and independently refreshed expected filesystem
metadata. It did not open personal documents or change radio states.

## Reproduction

Keep all outputs in ignored `artifacts/diagnostics`; they contain private paths.
The builder reads a previously frozen corpus, never live document contents, and
refuses to overwrite its output. The inference runner now accepts `--checkpoint`
and records the selected checkpoint alongside fixture/ranker hashes.

```powershell
.runtime/.venv/Scripts/python.exe scripts/build-semantic-evaluation.py --source artifacts/diagnostics/search-upgrade/frozen-v2.json --output artifacts/diagnostics/semantic-upgrade/new-frozen.json
dotnet tests/Scout.Tests/bin/Release/net10.0/Scout.Tests.dll --search-eval artifacts/diagnostics/semantic-upgrade/new-frozen.json artifacts/diagnostics/semantic-upgrade/new-development.json development
.runtime/.venv/Scripts/python.exe runtime/search_quality.py --fixtures artifacts/diagnostics/semantic-upgrade/new-development.json --models .runtime/models --checkpoint english --output artifacts/diagnostics/semantic-upgrade/new-predictions.json
dotnet tests/Scout.Tests/bin/Release/net10.0/Scout.Tests.dll --search-eval artifacts/diagnostics/semantic-upgrade/new-frozen.json artifacts/diagnostics/semantic-upgrade/new-assessment.json development artifacts/diagnostics/semantic-upgrade/new-predictions.json
```

Assess through the C# policy, not just the model's highest probability. Evaluation
commands record failed cases without returning a failing process exit code.
For another tuning phase, this now-inspected corpus becomes regression data;
reserve new domains/examples before further changes.

The next model work needs a demonstrated improvement on filename-description
matching with near-neighbor negatives, before any replacement is enabled. More
permissive confidence thresholds are not a solution to the observed errors.
Optional document-content retrieval is a separate feature and remains unimplemented.
