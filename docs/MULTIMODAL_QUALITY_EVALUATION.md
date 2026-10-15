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
