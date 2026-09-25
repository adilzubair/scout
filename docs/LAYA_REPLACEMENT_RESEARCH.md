# Laya replacement research — 2026-09-25

## Decision

**Test Ettin Reranker 32M and 68M first; do not replace Laya yet.** They are
plausible CPU choices for Scout's descriptive file searches, with permissively
licensed models and publisher-provided INT8 ONNX files. The 150M Ettin model is a
quality reference for a small finalist set. Qwen3-Reranker-0.6B is a larger
knowledge-oriented reference, not the ordinary-laptop default.

This is an online model survey, **not a Scout accuracy or latency result**. None
of these new candidates has run on Scout's fixtures. The published retrieval
benchmarks use document passages and always rank candidates; Scout must also
reject searches for which no indexed file fits. The old 64-case Scout set has
already been inspected and is useful for development, not a new holdout.

## What Scout needs from a replacement

The current Laya sidecar answers typed questions about up to 32 candidates:
winner versus none, independent relevance, group membership, one versus all,
action, and readiness. Its 2-second request deadline includes all of those
decisions. A pairwise reranker emits one score per query/candidate. Replacing
Laya therefore requires a separate no-match decision and local handling of
scope, actions, and other typed behavior; swapping the model alone is not a
complete replacement. The existing C# path already protects exact names and
chronology. See `runtime/ranking.py` and `docs/LIGHTWEIGHT_BASELINE.md`.

The current English Laya weight file is 842.6 MB. On a Core Ultra 9 275HX, its
CPU request p50/p95 was 1,515/2,141 ms, with 8/57 timeouts at 2 seconds and
36/64 correct final Scout results. Earlier local MiniLM and mxbai-xsmall
rerankers were fast but had weak positive/negative separation; their best
same-set thresholds still made too many unsupported matches. See
`docs/OFFLINE_EVALUATION.md`.

## Shortlist

The quality column below is **one publisher's common MTEB(eng, v2) retrieval
protocol**: mean NDCG@10 over six first-stage retrievers, reranking top 100.
These figures compare these rerankers on that protocol only. They are not
percent accuracy, Scout outcomes, or an Intern/Jev comparison. CPU throughput
uses that publisher's Intel Core i7-13700K with 512-token maximum inputs; it is
not a Windows laptop measurement. [Method and full comparison](https://huggingface.co/blog/ettin-reranker).

| Model | Parameters | Published NDCG@10 | Published CPU pairs/s | Relevant packaged weight | Scout research role |
| --- | ---: | ---: | ---: | ---: | --- |
| [Ettin Reranker 32M](https://huggingface.co/cross-encoder/ettin-reranker-32m-v1) | 32.8M | 0.5779 | 92.5 | [32.4 MB INT8 ONNX](https://huggingface.co/cross-encoder/ettin-reranker-32m-v1/commit/e3da5c5e7c2788d9e65351c380b19342d089800c) | First low-footprint candidate |
| [Ettin Reranker 68M](https://huggingface.co/cross-encoder/ettin-reranker-68m-v1) | 68.6M | 0.5915 | 31.2 | [69.3 MB INT8 ONNX](https://huggingface.co/cross-encoder/ettin-reranker-68m-v1/commit/5ee8c7f9f0b951946db55ed4d60b0891539d5cb9) | Quality/latency tradeoff |
| [Ettin Reranker 150M](https://huggingface.co/cross-encoder/ettin-reranker-150m-v1) | 151M | 0.5994 | 14.0 | [151 MB INT8 ONNX](https://huggingface.co/cross-encoder/ettin-reranker-150m-v1/tree/main/onnx) | Finalist-only quality reference |
| [GTE ModernBERT base](https://huggingface.co/Alibaba-NLP/gte-reranker-modernbert-base) | 150M | 0.5843 | 14.7 | 598 MB FP32 | Independent 150M comparator |
| [Granite English R2](https://huggingface.co/ibm-granite/granite-embedding-reranker-english-r2) | 150M | 0.5656 | 14.5 | [598 MB FP32](https://huggingface.co/ibm-granite/granite-embedding-reranker-english-r2/tree/main) | Independent 150M comparator |
| [Qwen3 Reranker 0.6B](https://huggingface.co/Qwen/Qwen3-Reranker-0.6B) | 596M | 0.5940 | not in CPU comparison | 1.19 GB BF16 | Larger knowledge reference |

Ettin's released checkpoints are Apache-2.0, English cross encoders. The
author distilled them from a larger reranker over roughly 143 million broad
and retrieval-specific examples; that supports testing them for richer
semantic associations than the older compact models, but does not prove better
knowledge on short filenames. [Training recipe and data](https://huggingface.co/blog/ettin-reranker).
The official model repositories include INT8 ONNX and OpenVINO files. Package
only one architecture-appropriate model and tokenizer; repository totals
include duplicate formats. Quantization can change rankings and abstention
scores, so the exact shipped file must pass the Scout evaluation. [ONNX Runtime
quantization guidance](https://onnxruntime.ai/docs/performance/model-optimizations/quantization.html).

At the published CPU throughput, scoring all 32 candidates would take roughly
0.35 seconds with Ettin 32M, 1.0 second with 68M, or 2.3 seconds with 150M
*before* retrieval, tokenization, policy, and UI. These are arithmetic
projections from a desktop CPU benchmark, not latency predictions for an
ordinary laptop. A bounded 8–12-item rerank after a high-recall first stage
could make larger models feasible, but only if it retains the right file.

Qwen3-Reranker-0.6B is Apache-2.0, multilingual and instruction-aware. Its
foundation model makes it a reasonable probe for world-knowledge queries. Its
BF16 checkpoint is already larger than Laya's measured English weight file,
and it must score candidates separately, so it is a possible optional deeper
mode rather than the lightweight default. [Official model card](https://huggingface.co/Qwen/Qwen3-Reranker-0.6B).

A compact general language model is another route for interpreting a complex
request, but it would add generated-token latency and still need a verified
no-match policy. [Qwen3.5-0.8B](https://huggingface.co/Qwen/Qwen3.5-0.8B)
is a recent Apache-2.0 example. Its own card describes prototyping and task
fine-tuning as the intended use at this scale, and does not report Scout-like
file selection. It is lower priority than a purpose-built reranker for the
instant search path.

[Jina Reranker v3.5](https://huggingface.co/jinaai/jina-reranker-v3.5) is a
strong 0.6B listwise research reference, but its published CC-BY-NC-4.0
license prevents treating it as a ready commercial default. Larger 4B
rerankers and the existing Qwen 4B helper conflict with the laptop footprint
goal.

## Why more world knowledge may still fail

Scout currently indexes filenames and metadata and reads contents only on
explicit preview. A model can connect words and concepts that appear in those
fields. It cannot reliably know that an unnamed file contains a particular
topic when the index does not expose that content. More capable reranking also
cannot restore a relevant item missing from the first-stage candidate pool:
three of 32 positive examples missed that pool in the prior local screen.

The product therefore has three independent problems to measure:

1. **Candidate recall:** retrieve the right file among the 32 or any smaller
   finalist set. Expand the first stage if semantic queries miss relevant
   filenames.
2. **Selection and abstention:** rank relevant candidates and return no match
   when all are wrong. Raw reranker scores are not calibrated probabilities;
   tune admission on independent negatives and verify it on a new holdout.
3. **Information coverage:** add opt-in, local content extraction and indexing
   for genuine deeper search, with measured update cost, size, privacy, and
   file-type coverage.

## Proposed offline experiment, with no Jev key

1. Download only Ettin 32M and 68M into ignored `.runtime/models/` and pin
   exact revisions/checksums. Probe their official INT8 ONNX artifacts on CPU;
   an initial FP32 screen is acceptable for model selection but is not a
   shipping result. ONNX Runtime supports C# on Windows x64 and Arm64, so a
   successful encoder path could later avoid the Python/PyTorch sidecar.
   [ONNX Runtime C# guide](https://onnxruntime.ai/docs/get-started/with-csharp.html).
2. Run the existing 64-case set as a development screen: top choice, no-match
   separation, final Scout outcomes, cold load, p50/p95, working set, and
   one-versus-all/action regression. Keep per-case local paths and scores in
   ignored diagnostics.
3. Freeze a new independently labeled holdout before tuning the admission
   rule. Include positives, close negatives, no-match, vague requests,
   shorthand/knowledge associations, temporal, folder, app/file, and plural
   requests. Split related task families across development and holdout.
4. Test the exact candidate, policy and package on an 8–16 GB CPU-only Windows
   laptop. Compare against Scout without AI and the current Laya path. Ship a
   default model only if final outcomes improve while false matches, p95,
   cold load, memory and installed size meet the release gates.

Intern's public behavior remains a feature checklist and independent Windows
task benchmark. Without measuring Jev on the same tasks, no numeric claim of
matching Intern's semantic quality is justified. No Jev API key is required.
