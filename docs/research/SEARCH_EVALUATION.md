# Search upgrade — 2026-09-24

Scout now handles both `3 newest files` and `newest three files`, leading folder
clauses, common format names, punctuation in partial filenames, and words inside
camel-case filenames. Chronological ordering preserves the filesystem timestamp
precision. App descriptions such as `text editor` retain application intent.

The model shortlist reserves capacity for broader retrieval, with a larger local
recall pool and diversity across repeated titles. All candidates still pass the
requested folder, type, scope and time restrictions. The app passes its resolved
intent into Laya instead of parsing it again with different scope information.
Already enforced folder paths are removed from the model's query; compact folder
details are supplied when they help interpret the description. Readable filename
labels keep their original candidate IDs.

When several useful files divide the choice probability, independent relevance
checks can admit the leading candidates. Both a single-candidate retrieval check
and a separate matching question must support the item. These checks do not
override a stronger comparative ranking, exact names or chronological ordering.
They remain conservative heuristics, not calibrated confidence estimates.

## Frozen evaluation

The saved pre-change runner uses commit `afa8ff8` search behavior. The corpus has
**504 distinct queries, 75,707 existing files and 36 sampled folder trees**. It
was frozen before production edits, with 233 development and 271 validation
cases. There are 250 distinct first-tier targets among cases with at most ten
equivalent first-tier answers. Counts of query variants are not independent tasks.

Ground truth comes from filenames, paths and filesystem timestamps, independently
of Scout's parser and ranking. Timestamp ties accept any valid member of the same
tier. No document contents were read. Description labels are inferred from
explicit filename categories and are reported separately; they are not user
judgments about document contents.

Folder trees are sampled deterministically, capped at three per top-level
project, and split before evaluation. Development and validation use different
query templates. Duplicate filename cases can accept equivalent files outside
the sampled tree; description categories can overlap root locations. The split
does not make every file or every concept independent.

Corpus SHA-256:
`9c11d7623f395043f5477347f498db3fd81fc73a598ccb74b3cc229b534e8a9b`

| Validation measure | Saved baseline | Final build |
|---|---:|---:|
| Actual final result, including intent/scope checks | 213/271 (78.6%) | 259/271 (95.6%) |
| Local results alone | 209/271 | 251/271 |
| Folder/date/count/name/path/command cases | 209/251 | 251/251 |
| Metadata-description cases | 4/20 | 8/20 |
| Desired first-tier item present in model shortlist | 228/271 | 271/271 |
| Out-of-scope results or model candidates | 0 | 0 |
| False matches on 56 known-empty cases | 0 | 0 |

The final development result is 223/233 versus 199/233 for the saved baseline.
The stricter matching gate trades two development answers for rejecting the
authored false match described below.

This assesses the actual C# final selection, including admission thresholds and
model abstention. It does not credit a raw Laya guess that Scout would reject.
The known-empty cases include missing tokens/folders and absent activity evidence;
they do not establish rejection quality for arbitrary semantic descriptions.

Production behavior and prompts were locked for the first validation run, which
scored 259/271. A later check against the existing authored regression suite found
that independent relevance could displace a stronger comparative choice and
admit an unrelated document for a narrowly described one. Correcting those issues
and checking app-role/scene parsing restored that suite's 56/56 and 39/40 results.
The final validation rerun remained 259/271. The rerun is confirmation, not a new
unseen test set. The reserved cases' remaining misses were not used for tuning.

Development prompt comparisons retained their predictions locally: titles only,
compact location context, independent relevance and readable filename labels.
Verbose or indiscriminate folder context could hurt relevance, so the final
adapter includes it only when query words make it useful. The 12 remaining
validation misses are all descriptions. This is a substantial improvement to
metadata search, not proof of parity with Intern/Jev or of broad semantic quality.

## Performance and Windows checks

On the RTX 5060 Laptop GPU with local English Laya, final validation model requests
took **94 ms median / 141 ms p95**, after warm-up; all 44 requests finished within
the two-second deadline. Model startup took about 5.1 seconds. These are adapter
times, excluding the app's debounce, HTTP transport and rendering.
The desktop sidecar now runs a small warm-up before publishing its ready signal,
so the first real request does not initialize GPU inference kernels. The final
scoped desktop model check completed in 172 ms.

The final validation search-engine measurements were **19 ms median / 30 ms p95**
across the 75,707-file catalog. Baseline engine measurements were 18/29 ms. The
retrieval expansion has similar local cost; the quality gains do not require
waiting for Laya on exact or chronological searches.

Desktop measurements uncovered a separate UI delay: animating the native acrylic
window height added roughly 200 ms to many query updates. Fitting its height
immediately preserves the interface and opening fade. Installed-app queries then
took **38–73 ms**, compared with 47–289 ms in the preceding animated run. The UI
heartbeat's worst gap fell from 216 to 65 ms; p95 stayed approximately 47 ms.
These are sequential measurements on this PC, not a hardware-independent claim.

The build passes **176 C# checks**, **13 Python adapter checks**, and the
authenticated loopback HTTP tests. Existing authored quality cases pass 56/56
and 39/40, with all four ambiguity checks passing. The remaining authored miss
is the uncertain `Diploma.pdf` description; no false match is admitted for its
seven negative cases.

Windows checks exercise real file searches, scopes, editable groups, manual
selection, rapid typing, native folder validation, GPU model hosting, PDF/image/
Office/text previews, settings navigation, screenshots, installed apps, global
hotkey and create/modify/delete watchers. Private documents are not launched.
The real-file audit includes the newly supported count, location and JSON/CSV
query forms. Live audits refresh expected metadata separately when user files
change; they never alter the frozen benchmark.
The final desktop run passed **41/41 checks** with the warmed sidecar and
immediate window resize, including a scoped Laya inference over real files.
Across its 40 file queries, local results rendered in **58 ms median / 89 ms
p95**, with a 160 ms first-query maximum, against 75,710 live indexed files.

## Reproduce locally

Keep the copied database, frozen corpus and prediction files under ignored
`artifacts/diagnostics/search-upgrade`. They contain private metadata and are
excluded from the application ZIP. The generation script refuses to overwrite
an existing corpus; the inference runner likewise refuses to overwrite predictions.

```powershell
dotnet build tests/Scout.Tests -c Release -m:1
.runtime/.venv/Scripts/python.exe scripts/build-search-evaluation.py --snapshot artifacts/diagnostics/search-upgrade/snapshot.db --output artifacts/diagnostics/search-upgrade/new-corpus.json
dotnet run --project tests/Scout.Tests -c Release --no-build -- --search-eval artifacts/diagnostics/search-upgrade/new-corpus.json artifacts/diagnostics/search-upgrade/new-cases.json development
.runtime/.venv/Scripts/python.exe runtime/search_quality.py --fixtures artifacts/diagnostics/search-upgrade/new-cases.json --models .runtime/models --output artifacts/diagnostics/search-upgrade/new-predictions.json
dotnet run --project tests/Scout.Tests -c Release --no-build -- --search-eval artifacts/diagnostics/search-upgrade/new-corpus.json artifacts/diagnostics/search-upgrade/new-report.json development artifacts/diagnostics/search-upgrade/new-predictions.json
```

The evaluation command reports failures as measurements rather than a nonzero
exit status. Inspect the summary and per-case verdicts. Use `validation` for the
reserved split only after implementation choices are fixed. Pass `--ranker` to
the Python runner to compare a preserved adapter. Saved baseline binaries remain
local, outside the package.
