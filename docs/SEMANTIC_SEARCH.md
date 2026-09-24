# Descriptive search audit — 2026-09-24

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
