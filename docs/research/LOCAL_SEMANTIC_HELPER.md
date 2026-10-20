# Local semantic helper

**Research status:** the 4B helper is excluded from the standard Settings flow.
It is retained for local experiments behind
`SCOUT_EXPERIMENTAL_SEMANTIC_HELPER=1`; its weights remain outside the app ZIP.
The measurements below explain why it was useful, but its footprint does not
meet the ordinary-laptop product target.

Scout can optionally use a stronger local model to compare descriptive file
queries with the existing shortlist. Laya still supplies typed scope, action,
readiness and group decisions, and handles apps, system commands and short
keyword searches. Exact names and explicit chronological results retain their
deterministic protections. Ordinary local results render before refinement.

The helper uses Qwen3-4B-Instruct-2507, quantized to Q4_K_M, through a pinned
llama.cpp Windows Vulkan runtime. Both run locally alongside Laya. Queries and
filenames stay on this PC; personal document contents are not read or indexed.
Setup downloads about 2.5 GB for the helper model plus a 32 MB runtime archive.
The feature defaults off for existing and new installations. In the research
configuration, set the environment variable before starting Scout, then select
the experimental helper in Settings before running setup.

## Measured improvement

The evaluation was frozen before model experiments, using 90 previously inspected
cases as development and **64 fresh authored cases from 16 new domains** as
reserved validation. Each new domain has two positive descriptions and two
near-neighbor negatives. The tests use actual Scout retrieval and final ranking;
they do not score only the raw model choice. The new pools are authored metadata,
not claims about the contents of personal files. Paraphrases within each domain
are correlated, and filename-derived labels sometimes admit other interpretations.

Frozen corpus SHA256:
`257251b4d6fd27ace6ec6d1a6c146131579534b7a7402f38b5e381c3ad2cb64e`.

| Measurement | Laya alone | Laya + helper |
| --- | ---: | ---: |
| Inspected development | 49/90 | 77/90 |
| Development false matches | 2/16 | 1/16 |
| Reserved validation | 38/64 | 56/64 |
| Reserved positive matches | 22/32 | 28/32 |
| Reserved false matches | 16/32 | 4/32 |
| Prior 504-case real-metadata regression | 484/504 | 497/504 |
| Prior regression false matches | 0/113 | 0/113 |
| First existing authored suite | 56/56 | 54/56 |
| Second existing authored suite | 39/40 | 40/40 |

All four existing ambiguity checks pass. The helper loses two old presentation
choices: "presentation for the leadership team" favors Company strategy over
Board update, and "slides about the direction of our company" favors Roadmap
over Company strategy. Broad descriptions remain ambiguous. Four fresh negative
queries still admit a wrong file, and three fresh positive cases miss retrieval
before inference. This is a substantial measured gain, not proof of general
semantic accuracy or a live comparison with Intern/Jev.

The integrated adapter on the RTX 5060 Laptop GPU takes **250 ms median / 281 ms
p95** on the fresh validation requests, with zero two-second timeouts. The larger
prior regression workload takes **141 ms median / 547 ms p95**, also with zero
timeouts when one Scout model stack is loaded. Running a separate benchmark
alongside an open Scout duplicates GPU models and caused two timeouts in an
earlier measurement. These are refinement timings, not initial UI latency.

The packaged Windows app passes **45/45 desktop checks** against 75,714 actual
indexed files. Four new checks exercise receipt, schema, architecture and
security descriptions through the live UI with the helper enabled. Local
results render in **66 ms median / 109 ms p95**. Those four descriptive searches
settle in **666–1,194 ms** including UI scheduling, refinement and the diagnostic's
settling wait. Edited groups, manual selection, rapid typing, folder-scoped searches
and native folder validation pass. No personal documents are launched.
The packaged UI smoke test also passes rendering, scopes, settings navigation,
validation focus, editable groups and retained manual selection.

## Decision and failure handling

For file-only shortlists and queries of at least four whitespace-separated words,
the helper performs one choice across up to 32 supplied labels, followed by a
separate relevance check of the proposed filename. It never generates paths,
shell commands or execution targets. Responses must fit the supplied label
grammar and contain a complete normalized probability distribution. The separate
check must support the proposal at 0.9; existing C# admission thresholds then
apply. These model scores are conditional probabilities, not calibrated certainty.

Both requests share the existing two-second deadline. Full prompts are tokenized
before inference; oversized input is rejected rather than silently truncated.
Unavailable helpers, invalid output and budget failures fall back to Laya/local
results. A valid no-match decision remains an abstention. The helper warms before
readiness, accepts authenticated loopback requests only, and belongs to a Windows
job that kills it when its owning sidecar exits, including abrupt crashes.

A 1.7B model was evaluated first and rejected for too many near-neighbor false
matches. The 4B prompt and relevance check were selected on development before
opening reserved results. Integration checks subsequently fixed response-format
validation of tiny probability remnants, without tuning the model to validation
failures. The broad runtime selection/shortlist behavior is unchanged.

## Reproduction

All corpora, outputs and model weights remain under ignored local directories.
The helpers do not install dependencies during ordinary inference.

```powershell
# Or select the option in Settings before running Set up local AI.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-laya.ps1 -Cuda -SemanticHelper

.runtime/.venv/Scripts/python.exe runtime/search_quality.py --fixtures artifacts/diagnostics/semantic-helper/validation.json --models .runtime/models --helper --output artifacts/diagnostics/semantic-helper/new-predictions.json
dotnet run --project tests/Scout.Tests -c Release -- --search-eval artifacts/diagnostics/semantic-helper/frozen.json artifacts/diagnostics/semantic-helper/new-assessment.json validation artifacts/diagnostics/semantic-helper/new-predictions.json

.runtime/.venv/Scripts/python.exe runtime/test_semantic_helper.py
.runtime/.venv/Scripts/python.exe runtime/test_helper_process.py
.runtime/.venv/Scripts/python.exe runtime/test_http.py --helper
```

The inference runner refuses to overwrite results. The assessment records failed
cases without failing its process exit code; inspect its summary. This corpus is
now inspected regression data for any future model tuning. Preserve a fresh set
before another tuning phase.
