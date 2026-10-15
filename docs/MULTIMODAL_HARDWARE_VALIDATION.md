# Optional multimodal add-on: physical-PC validation

This internal validation bundle uses the exact published Scout application and
its bundled .NET runtime. No .NET SDK, Python installation, Office or LibreOffice
is needed. It downloads pinned CPython, CPU dependencies and model weights from
their publishers. Allow at least 12 GiB of free space as a test reserve; this is
not a measured product requirement. Use a normal Windows account on Windows 11
x64, plug the PC into power and close other heavy applications.

Extract the ZIP fully into a short, plain local folder, such as a folder named
`Scout-check` directly under your user directory. Do not run inside the ZIP,
OneDrive, a network share or a linked folder. For the physical 16 GB PC, open
Windows PowerShell in the extracted folder and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -Physical16Gb
```

The switch records your confirmation that this is the physical 16 GB PC. The
report also measures RAM/CPU/OS independently. Omit it for a development-machine
rehearsal. The runner verifies every shipped file hash before launching the
workload. The adjacent ZIP checksum identifies this exact bundle; it is not a
publisher signature. No admin rights or global execution-policy change is needed.

Each run creates its own `r/.../h-...` folder and private add-on. It reads
only the supplied authored fixtures and generated performance files. It does
not open your personal folders or normal Scout database, and it sends no results
automatically. The model/runtime download requires internet access; inference
runs locally. Expect setup plus several minutes of CPU work. Keep failed runs
and logs rather than deleting them. A rerun creates a separate run and repeats
downloads; a failed run can be investigated before retrying.

The workload records a new model process's first-query time, actual extraction
and 768-dimensional embeddings across seven families (including six Office
formats, PDF, image, code, audio/video containers), and a bounded video preview.
Office layout limitations are reported. It then measures 24 uncached warm
queries through the production retrieval adapter against 100,000 synthetic
segments, with Laya off. Synthetic vectors are performance fixtures, never
retrieval-quality labels.

Windows job accounting records peak committed memory for each phase's entire
process tree, including nested model/parser processes and the test app. This is
a conservative whole-workload measurement, not sampled working set. Targets for
review are first query at most 20 seconds, runtime validation/100K job peaks below
6 GiB and semantic p95
at 100K at most 1 second. First query is process-cold; setup may have warmed the
OS file cache. Installation peak is recorded separately and is not the worker/
extractor runtime target. A passing program exit means the engineering workload completed;
inspect the recorded targets separately.

Return the small `diagnostics-....zip` printed at the end, along with whether the PC was on AC power and
whether other demanding software was running. Review logs before sharing: they
contain local paths. The ZIP includes hardware, timings, resource reports and
failure logs; it excludes model weights, runtimes, download caches and generated
indexes. Keep the full local results folder and extracted package until any sandbox recovery has
been investigated. After a successful run and review, the whole extracted bundle
can be removed to reclaim its private runtime; it does not remove normal Scout.

This bundle does not close the full release gate by itself. Ordinary WPF search
regression, Laya-on resource use, representative throughput/battery/disk growth,
independent retrieval quality and native distribution obligations remain open.
No public distribution approval is asserted.

The 32 GB development rehearsal completed the workload: process-cold first query
17.57 s, 100K semantic p95 545.98 ms, whole-job peaks 7.37 GiB (all-family) and
6.54 GiB (scale). Those whole-tree memory totals do not establish the <6 GiB
worker/extractor target. This is a baseline validation bundle, not a release
candidate or a claim that the minimum-machine/resource gates have passed.
