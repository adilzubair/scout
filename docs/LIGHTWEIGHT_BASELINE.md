# Ordinary laptop baseline — 2026-09-25

Scout's standard path keeps local AI optional and leaves the 4B semantic helper
out of Settings. The launcher and local index work with AI disabled. When AI is
enabled, Laya starts with the visible app, stays available while the panel is
shown, and stops one minute after the panel is hidden. Reopening the panel
restarts it. Quitting Scout stops the sidecar immediately. The optional helper
can only run when `SCOUT_EXPERIMENTAL_SEMANTIC_HELPER=1` is set before launch.

## Measured local footprint

This is an installed development setup, not a clean laptop install. The ZIP
excludes Python and model weights. A CPU-only setup uses a different PyTorch
wheel, so the CUDA environment size below must not be presented as its size.

| Item | Measured size |
| --- | ---: |
| Current Windows app ZIP | 84.1 MB |
| English Laya checkpoint weight file | 842.6 MB |
| Optional 4B helper weight file | 2,497.3 MB |
| Installed CUDA Python environment | 7.61 GB |

The CPU benchmark ran on an Intel Core Ultra 9 275HX, which is faster than many
target laptops. The Laya process reached about 2.4 GB working set at one sample,
3.2 GB peak working set, and 4.25 GB private bytes. These are process counters
from that run, not a minimum RAM specification.

## CPU-only quality and latency

The existing 64-case reserved authored validation set has 32 positive and 32
near-neighbor negative filename descriptions. Its labels are based on authored
metadata, not document contents. Related cases are correlated, so 64/64 would
not establish general accuracy.

| CPU Laya measurement | Result |
| --- | ---: |
| Cold model load | 8.60 s |
| Model request median / p95 | 1,515 / 2,141 ms |
| Two-second model timeouts | 8 of 57 requests |
| Final Scout result correct | 36/64 |
| Positive descriptions found | 17/32 |
| Wrong matches on negative descriptions | 13/32 |
| Expected result reached shortlist | 61/64 |

The final-result score uses saved CPU predictions replayed through Scout's C#
retrieval and admission policy. The C# replay itself took 24 ms median / 27 ms
p95; that timing **excludes model inference**. The CPU run does not meet a good
default experience: it approaches the two-second refinement deadline even on
this high-end CPU and is too willing to match unsupported descriptions. Local
results remain fast and usable without it. Do not turn on CPU AI by default or
claim Intern-level semantic quality from these results.

The previous CUDA comparison on the same authored set was 38/64 with Laya alone
and 56/64 with Laya plus the research helper. The larger model is a useful
quality reference but misses the footprint target. All three results are now
inspected regression measurements, not a fresh holdout for model selection.

## Next release gates

1. Measure a compact candidate on a **new frozen holdout** with positive,
   negative, ambiguous, temporal, folder and mixed app/file cases. Keep exact
   names and temporal ordering protected by local logic.
2. Require improvement in final Scout outcomes, low false matches, acceptable
   median and p95 latency on an 8–16 GB CPU-only Windows laptop, cold load and
   steady memory. Report the installed runtime and clean CPU package size.
3. Use Intern's public app behavior and test examples as an offline feature
   checklist. Score Scout on independent Windows queries with explicit expected
   outcomes, including abstention, groups/actions and end-to-end latency. No Jev
   account or API request is part of this plan. Describe any quality claim as a
   Scout benchmark result, not measured Jev equivalence.
4. Validate the approved content-search first pass on an 8 to 16 GB Windows
   laptop. It is opt-in, limited to selected folders, and keeps extracted text
   in a separate local index that is cleared when disabled. Laptop measurements
   for indexing time, search latency, memory, index size and update cost are
   still pending. No measured footprint or timing claim is available yet.

Reproduce the CPU model and C# policy measurements with ignored local fixtures:

```powershell
.runtime/.venv/Scripts/python.exe runtime/search_quality.py --fixtures artifacts/diagnostics/semantic-helper/validation.json --models .runtime/models --device cpu --output artifacts/diagnostics/lightweight/cpu-laya-baseline.json
dotnet tests/Scout.Tests/bin/Debug/net10.0/Scout.Tests.dll --search-eval artifacts/diagnostics/semantic-helper/frozen.json artifacts/diagnostics/lightweight/cpu-laya-assessment.json validation artifacts/diagnostics/lightweight/cpu-laya-baseline.json
```

The runner refuses to overwrite existing output. These ignored reports contain
case-level local paths and should stay off Git.

See [the compact-model CPU screen](OFFLINE_EVALUATION.md) for the first local
experiment and its limitations.
