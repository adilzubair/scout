# Multimodal quality evaluation

The release protocol is `evaluation/multimodal/protocol.json`. Keep its frozen
admission floor and existing dataset receipts intact. Engineering fixtures, the
authored pilot and the natural-audio diagnostic below are development evidence.
The independent 400-task, seven-family holdout remains a separate gate.

## Natural-audio development diagnostic

`scripts/prepare-esc10-evaluation.py` prepares a small slice of the
[official ESC-10 subset](https://github.com/karolpiczak/ESC-50/tree/33c8ce9eb2cf0b1c2f8bcf322eb349b6be34dbb6).
The upstream license explicitly identifies ESC-10 as CC BY 3.0. The rest of ESC-50
has different terms and is excluded. The script retains the complete upstream
license and each selected clip's attribution, and admits only individual CC0 or
CC BY entries. Dataset citation: Karol J. Piczak, *ESC: Dataset for Environmental
Sound Classification*, ACM Multimedia, 2015, DOI 10.1145/2733373.2806390.

The default selection is twenty natural recordings: one eligible unique original
from each of folds 1 and 2 for each of the ten classes. Selection is deterministic
and occurs before inference. Original Freesound identities, fold, class, upstream
revision and blob hashes are retained in `provenance.json`; downloaded sources
have opaque filenames. The Git tree is reconstructed and checked against its
pinned root, and every downloaded blob is checked for membership and contents.
Source and metadata downloads are individually bounded to 2 MiB. A run takes
at most fifty clips; redirects are rejected. Interrupted runs can reuse verified
files; existing dataset, provenance or freeze receipts are never overwritten.

Labels come from the upstream human-authored classes. They establish clip-level
presence only. Each class query accepts both matching recordings and is marked
ambiguous. All ten class groups are development-only. There are twenty original
sources, ten task groups and ten queries, rather than twenty independent tasks.
The complete five-second clip is the expected locator; this cannot measure event
onset accuracy. Unlisted sounds cannot be treated as absent, so this diagnostic
has no negative queries. It cannot measure abstention. Its small corpus also
makes recall at twenty weak evidence; inspect acceptable first-file ranking.
Possible overlap with model training is unknown.

From the repository root, use the existing verified local Python runtime:

```powershell
& .runtime/semantic-search/cpu-venv/Scripts/python.exe scripts/prepare-esc10-evaluation.py --output artifacts/diagnostics/multimodal/esc10-natural-audio-v1
& .runtime/semantic-search/cpu-venv/Scripts/python.exe -m unittest discover -s scripts -p test_prepare_esc10_evaluation.py
```

Use a new output directory for a new diagnostic. `--offline` requires all selected
source and metadata files to be present and verifies them without network access.
The preparation step never invokes a model. Freeze before evaluation using the
test driver's `--multimodal-freeze <dataset> <protocol>` command, then run
`--multimodal-eval <dataset> <protocol> <verified-addon> <private-python-home>
<new-results-directory> development`. It runs actual production readers, native
CPU embeddings, SQLite retrieval, keyword comparison and production-adapter
replay at 768/512/256 dimensions. Laya remains a separate comparison.

Keep source media, notices, frozen datasets, indexes and detailed query results
under ignored `artifacts/`. They are not included in the application or hardware
bundle. Exclude these opened development originals and related derivatives from
the future holdout, including any copies obtained through another provider.
Per-clip identities in the retained provenance support that review. Never derive
new labels from retrieval predictions.

The first frozen run (`f8b9cb3db5315ab27974d507c7ee3f1c57795b3efc2235a791cee1309417746d`)
indexed all twenty clips into twenty segments with no missing required source or
production-adapter disagreement. All ten queries returned no files at each tested
dimension. Inspection found that the query parser maps the generic word
"recording" to a hard video filter, excluding the audio corpus before similarity
ranking. Preserve this failure as a parser regression; it is not a measurement
of the audio model's ranking. Do not change these frozen queries to hide the bug.

Generic recording requests now use the union of audio and video file extensions.
Explicit audio, video and concrete file formats retain their narrower constraints.
Both ordinary metadata search and semantic eligibility share this vocabulary,
including Windows Search extension expansion. Document descriptions using
"recording" as a verb retain their existing behavior.

The unchanged frozen diagnostic was rerun after the parser fix. An acceptable
first file was returned for 7/10 class queries at 768, 512 and 256 dimensions;
native production-adapter replay agreed with fusion at 768. All twenty files
were admitted for every query, making the 10/10 recall-at-twenty result trivial.
The three incorrect leading classes were rooster → chainsaw, rain → sea waves,
and crackling fire → rain. Keyword search returned no files with these opaque
names. Nineteen sources completed without a partial flag; one retained the
visible `audio_peak_clipped` flag. All twenty were indexed, with no required
source missing. No query was advertised as ready/confident by the ranking policy.
The admission floor stayed at 0.35. These results expose ranking and selectivity
limitations; they do not clear the release-quality gate or the earlier negative
admission failure. The full 336 regression checks and 24 package contracts pass.

## Unopened natural-image candidates

`scripts/intake-coco-images.py` prepares an image candidate pool from the official
COCO 2017 validation annotations. The complete annotation archive is pinned to
252,907,541 bytes and SHA256
`113a836d90195ee1f884e704da6304dfaaecff1f023f49b6ca93c4aaae470268`.
Only the two bounded validation JSON members are read. The publisher's terms are
retained with their pinned Git blob hash. COCO annotations are CC BY 4.0; images
have their own licenses and attribution obligations. See the
[publisher's terms](https://raw.githubusercontent.com/cocodataset/cocodataset.github.io/master/dataset/termsofuse.htm).

The first batch selects forty images deterministically before inference, using
only entries marked CC BY 2.0 in the publisher's image metadata. Each original
JPEG is checked against its HTTPS publisher's ETag checksum and receives a local
SHA256, opaque filename and atomic download receipt. Transfers are limited to
8 MiB per image, 100 images and 512 MiB per batch. Redirects are rejected for
image downloads. Interrupted transfers reuse only verified receipts and bytes.
All images, captions, object annotations and attribution evidence remain private
under ignored diagnostics, outside the base package and hardware bundle.

```powershell
& .runtime/semantic-search/cpu-venv/Scripts/python.exe scripts/intake-coco-images.py --annotations artifacts/diagnostics/multimodal/coco-intake/annotations_trainval2017.zip --output artifacts/diagnostics/multimodal/coco-images-holdout-candidates-v1 --count 40
& .runtime/semantic-search/cpu-venv/Scripts/python.exe scripts/intake-coco-images.py --capture-attribution --output artifacts/diagnostics/multimodal/coco-images-holdout-candidates-v1
```

The archive is available from the official download page. Acquire it separately
and verify the pinned hash; the script does not install a downloader or fetch the
whole image archive. `--offline` verifies existing source/metadata receipts and
makes no network requests. Use a new pool directory for a new batch. Completed
intake and attribution evidence cannot be overwritten. Rate limits, authorization
errors and server failures stop attribution capture; 404/410 and unresolved
canonical routes are recorded without following an unrelated photo or host.

The production AppContainer review exporter accepts bounded `coco-<id>` identities
alongside Commons identities. `--multimodal-review` uses the actual selected-root
snapshot, no-network readers and prepared-media receipt checks, with a deterministic
embedding boundary for export. It never invokes the embedding model or retrieves
against candidate queries. The first batch exported all forty sources, and every
prepared image was inspected on hash-linked contact sheets. All 200 upstream human
captions were cross-checked. They are not 200 independent tasks. Forty draft queries
were authored from visible content; no release task or split has been approved.

Review exposed errors and disagreements in the annotations and draft observations.
The first draft misidentified a character-shaped iced cookie as a jar; individual
image inspection corrected it before inference. Pig/dog and ski-direction conflicts
in upstream captions were excluded from draft query claims. Both observation
versions and their hash links are preserved. Captions and finite object annotations
do not prove exhaustive absence, so no negative tasks were inferred from omissions.

Current Flickr author/license capture found 31 metadata matches, two conflicts
(CC BY-ND and CC BY-NC) and seven unresolved originals. The nine conflicts/unresolved
originals are excluded from provisional holdout use pending review. The other 31
remain provisional: a metadata match is not completed rights or related-source
review. Current author URLs and original photo IDs support conservative grouping;
the batch has 38 provisional author/photo groups. Full-corpus derivative/event
overlap and source rights still need review before freezing. Possible model-training
overlap is unknown. No holdout predictions have been opened.

Thirteen offline intake/attribution contracts and all 337 regression checks pass.
Actual offline replay reproduced all source bytes, captions, selection and attribution
evidence, with only batch timestamps changing. These checks establish reproducible
candidate preparation, rather than retrieval quality or license clearance.

## Unopened document and Office candidates

`scripts/intake-zenodo-content.py` intakes an explicit file selection from pinned
public Zenodo metadata snapshots. Each selection names the record ID, exact
metadata SHA256 and original filenames. The tool requires public access, a declared
CC BY 4.0 license, creator attribution and publisher file checksums. It constructs
only the selected record's HTTPS file URL, rejects redirects, verifies exact byte
length/MD5 and records a local SHA256. It never opens a document or runs formulas.
The original metadata/selection bytes are retained alongside the originals.

Files are limited to 32 MiB, batches to 100 files/512 MiB, and transfers to a checked
600-second deadline with 30-second socket timeouts. A first 120-second transfer
attempt failed and remains recorded; the successful bounded retry reused the
already verified file. Offline replay verifies all bytes and provenance without
network requests. Existing candidate manifests, provenance and frozen datasets
cannot be overwritten. Ten offline source-intake contracts pass.

```powershell
& .runtime/semantic-search/cpu-venv/Scripts/python.exe scripts/intake-zenodo-content.py --selection artifacts/diagnostics/multimodal/zenodo-content-discovery/selection-v1.json --metadata artifacts/diagnostics/multimodal/zenodo-content-discovery --output artifacts/diagnostics/multimodal/zenodo-office-candidates-v1
```

The first batch contains eleven originals (75,911,809 bytes): three presentations
(PPTX/ODP), three documents (PDF/DOCX) and five spreadsheets (XLSX/CSV). They come
from four concept-record groups. Alternate formats and deposit versions remain
related; eleven files are not eleven independent source groups. The deposits cover
ecological restoration, historical correspondence, logistics-training telemetry
and plant gene-expression research. Record declarations and embedded third-party
credits still require rights review before approving holdout use.

The generalized production review exporter accepts these content pools, verifies
opaque source paths/checksums and runs the same no-network readers across all
seven configured families. It records known unsupported/timeout outcomes and
continues to remaining sources; any unavailable source yields a nonzero exit and
an explicit report. Source changes, invalid receipts and unexpected integrity
failures still abort. No embedding or retrieval predictions are used for review.

Real-source review exposed three defects, now corrected:

- Spreadsheet chunks could lose their sheet/header context and begin mid-value.
  Chunks now repeat bounded context, preserve ordinary cell boundaries, repeat an
  oversized cell's address on continuation and identify the covered cell range.
  Exact emitted strings are retokenized because trimming can change BPE boundaries.
  Abbreviated context is visible as `sheet_context_limit`; source/segment quotas
  remain explicit. Multirow/merged-header selection remains a fidelity-review case.
- A transient Windows sharing lock during snapshot deletion could mask a parser
  timeout. Sharing/lock violations retain the owned copy for retry before the next
  job/start. Link/path/integrity and other unexpected errors remain fatal. A real
  held-file contract verifies error preservation, retry and original-file safety.
- The pinned PDF binding's BSD check called `platform.system()`. On CPython 3.12
  Windows that enters WMI detection, causing both natural PDFs to time out in
  AppContainer. The loader supplies the already-known Windows OS name only during
  that import and restores the original function even on failure. The exact PDFs
  then complete inside the unchanged zero-capability boundary and three-minute
  deadline. Vendor wheels and model artifacts are unchanged.

Keep the failed baseline and intermediate boundary/cleanup failures. The final
eleven-source run prepares 1,288 verified segments and removes its owned work
directory. All 1,014 text segments (including 571 sheet segments) satisfy the
2,048-token raw reader limit with the pinned actual tokenizer. Both PDFs have no
partial flag. Office layout, unsupported embedded-image, OCR and dense-sheet
limits remain visible in the other sources; this is not complete fidelity proof.
Original file hashes remain unchanged. All 340 regression checks, 24 package
contracts and 21 reader contracts pass at this milestone.

Twenty-eight draft questions were authored from independently inspected original
Office XML/cached values, with source-part hashes and candidate slide/paragraph/
cell locators. They remain unapproved and unsplit pending full-corpus ambiguity,
rights and related-source review. None has been used for model inference, and no
negative task was inferred from an omission. The future 400-task holdout and its
seven-family requirements remain unchanged.

## ODS row structure and additional originals

Conformance review against the [ODF row-container specification](https://docs.oasis-open.org/office/OpenDocument/v1.3/OpenDocument-v1.3-part3-schema.html)
found that direct-child-only traversal skipped declared header rows, row groups
and row-list containers. The reader now visits these containers in document order,
counts repeated/empty rows at their real positions and preserves declared multirow
headers. Group-local context is inherited and restored at group boundaries rather
than leaking into a sibling group or later outer rows. Without declared headers,
the repeated context is labelled `Leading row context`, rather than claiming a
data row is a declared header. Merged-cell spans remain explicit.

Context storage is bounded to 128 entries/8,192 characters, independently of the
original cell bodies. Abbreviation reports `sheet_context_limit`; original header
cells still pass through the normal cell-aware, token-bounded writer. Row-container
nesting is limited to 64 levels. Parser capabilities, archive/XML limits, model
artifacts and admission thresholds are unchanged. Runtime-byte hashing changes
the reader identity, so the durable index invalidates earlier extraction results.

Four new regression cases fail on the original traversal and pass after the fix;
all 25 reader contracts pass. The actual zero-capability parser and trusted receipt
bridge also pass four authored cases: grouped rows with merged/multirow headers,
group-local context, oversized header context and excessive nesting rejection.
The three accepted files produce twelve text segments, at most 1,515 raw tokens
with the pinned tokenizer. Originals remain unchanged and owned work is removed.
The boundary uses deterministic unit vectors solely to validate receipts; it does
not load the embedding model or establish retrieval quality.

An additional pinned public intake contains 36 originals (6,766,914 bytes): 34 ODS
files and two readmes from nine concept/version groups. Public record metadata,
declared CC BY 4.0 attribution, publisher MD5, source SHA256 and original selection
bytes are preserved. Offline replay verifies the same originals and provenance.
The no-model sandbox review prepares 31 sources into 5,067 verified text segments;
the actual tokenizer maximum is 1,019 raw tokens. Five sources are explicitly
unavailable because `content.xml` exceeds the existing 8 MiB XML-part budget. The
batch returns nonzero and retains those failures. Thirteen prepared sources reach
the per-file segment limit. None of the inspected natural sources within the XML
budget uses declared row containers; the new row-container fix is established by
the authored conformance cases, not by pretending this natural batch exercises it.

These originals remain unapproved and unsplit. Embedded-object/layout limits,
large legitimate XML parts, XLSX table/merged-header relationships, rights and
cross-corpus grouping still require work. No model predictions or new admission
threshold decisions were made from this candidate pool.

## Open release evidence

Natural images, video, joint audiovisual events, multilingual and complex Office
content still need independent content-grounded tasks. The holdout needs supported
negatives, close distractors, ambiguous queries and independent source groups
across all seven content families. Freeze those labels before opening results.
The earlier authored pilot admitted every negative query at the provisional
0.35 floor; that failure remains open. Do not infer a fix from positive-only audio.

Physical 16 GB Windows validation, remaining lifecycle/resource checks and runtime
distribution clearance remain separate release gates. The user will run the
physical hardware bundle later. This diagnostic cannot make the add-on release ready.
