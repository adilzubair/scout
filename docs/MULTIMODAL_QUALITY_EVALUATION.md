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
