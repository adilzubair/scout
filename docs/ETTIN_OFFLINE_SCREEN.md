# Ettin offline CPU screen — 2026-09-25

## Result

**Do not replace Laya with Ettin yet.** The INT8 Ettin models are fast and much
smaller as model artifacts, and 150M is the strongest of the three at choosing
the right positive file. None solves Scout's no-match decision well enough on
the existing filename-only development cases. The earlier mxbai-xsmall screen
still admits more correct positives at the same false-match budget. No new
holdout or ordinary Windows laptop has been tested.

This is a first-stage model screen, not an end-to-end Scout outcome. The
current C# `RankingPolicy` expects Laya's typed probability distribution,
independent relevance, group, and action decisions. Raw Ettin logits cannot
be plugged into that policy without a new admission and behavior layer.

## Setup and results

All three official `model_quint8_avx2.onnx` backbones were downloaded from
pinned model revisions along with tokenizer and Dense/LayerNorm heads. The
ONNX exports contain only the transformer, so
[`scripts/ettin-onnx-probe.py`](../scripts/ettin-onnx-probe.py) applies the
heads after CLS pooling. The screen used 4 CPU threads and 128-token maximum
pairs on an Intel Core Ultra 9 275HX. The same existing authored 64-case set
was used for the earlier compact probes; 57 cases had all-file shortlists:
29 positives and 28 negatives. Those shortlists contain at most 5 candidates,
so these request times do not represent a full 32-candidate production list.
This AVX2 experiment covers the current x64 CPU only; older x64 and Windows
Arm64 devices need separate architecture and performance checks.

| Model | INT8 backbone / full selected artifact | Correct first choice among 29 reachable positives | Positive/negative max-score AUC | Correct positives with at most 4/28 false matches | Request p50/p95 | Cold model load | Process peak working set |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| [Ettin 32M](https://huggingface.co/cross-encoder/ettin-reranker-32m-v1) | 32.4 / 36.6 MB | 25/29 | 0.65 | 9/29 | 7.6 / 9.2 ms | 0.49 s | 129 MiB |
| [Ettin 68M](https://huggingface.co/cross-encoder/ettin-reranker-68m-v1) | 69.3 / 73.9 MB | 25/29 | 0.73 | 8/29 | 18.5 / 21.0 ms | 0.80 s | 176 MiB |
| [Ettin 150M](https://huggingface.co/cross-encoder/ettin-reranker-150m-v1) | 150.6 / 156.6 MB | 27/29 | 0.81 | 12/29 | 32.9 / 41.6 ms | 1.04 s | 269 MiB |
| Previous local MiniLM | 90.9 MB FP32 weight | 25/29 | 0.63 | 9/29 | 10 / 15 ms | — | — |
| Previous mxbai xsmall | 141.7 MB FP16 weight | 25/29 | 0.68 | 14/29 | 46 / 73 ms | — | — |

The admission counts above are **optimistic in-sample upper bounds**: each
threshold was chosen after seeing these same cases. They are not an unseen
accuracy estimate or a deployable threshold. The screen's 29/29 recall refers
only to its file-candidate subset; 3 of the full set's 32 positive targets did
not reach this shortlist. A reranker cannot recover them.

Thirty warmed runs on a synthetic 32-title shortlist, with the same CPU and
token limit, measured 37/39 ms p50/p95 for 32M, 89/100 ms for 68M, and
165/181 ms for 150M. Those timings measure scoring only; the titles were drawn
from the existing fixture and do not establish production latency on a laptop.

The process's end-of-screen **private bytes** were about 871, 915, and
1,003 MiB for 32M, 68M, and 150M respectively. Working set and private bytes
are different Windows counters. The Python/ONNX probe is not a packaged C#
runtime, and these counters are not minimum RAM requirements. The selected
artifact sizes exclude duplicate FP32 model files, alternative ONNX exports,
Python, ONNX Runtime, and the Scout app. Cold model load excludes Python
interpreter startup and Scout's UI/index startup.

## Scoring check

The published ONNX files output `last_hidden_state`, not a final reranker
score. The probe implements the model card's CLS pooling, Dense, GELU,
LayerNorm, and final Dense modules using the released head weights. The
quantized models rank the [official example](https://huggingface.co/cross-encoder/ettin-reranker-32m-v1)
correctly. For a direct FP32 check, the 32M backbone was loaded with PyTorch
using the same tokenizer and head calculation. It chose the same first file in
47/57 cases, with max-score correlation 0.955. Both variants chose 25/29
reachable positives correctly; FP32 admitted 8/29 versus INT8's 9/29 at the
optimistic four-false-match budget. Quantization changes some results but does
not explain the no-match weakness.

## Reproduce locally

The model files and case-level outputs are ignored by Git. The pinned model
revisions were:

| Model | Hugging Face revision | INT8 ONNX SHA-256 |
| --- | --- | --- |
| 32M | `b33e5ceb5110773ea9cf5e00c9bedc83a8c2afdd` | `7ca231b98dc591122774127865a293aae90b9a2ccb148afe8c2587d03703f8d0` |
| 68M | `d166fa88ddde3c42bc3ee92f7df476d941c8204a` | `32972293b8c229a906129aee4feb08fee3df993f9e5ed247179c94bcbbaae06a` |
| 150M | `025501c4e0f9bbeb4c5b198318e0089ff061cc14` | `7ae500b430815623ae87f4c05ffe6fccaab88752963c6a1140cb4ef86f60da70` |

With ONNX Runtime 1.30.0, `huggingface_hub`, `tokenizers`, NumPy and
`safetensors` available in the ignored Python environment:

```powershell
.runtime/.venv/Scripts/python.exe scripts/fetch-ettin-model.py --size 32m
.runtime/.venv/Scripts/python.exe scripts/ettin-onnx-probe.py --fixtures artifacts/diagnostics/semantic-helper/validation.json --model .runtime/models/ettin-reranker-32m-v1 --output artifacts/diagnostics/offline/ettin-32m-recheck.json --threads 4
```

The runner refuses to overwrite output. Run the same command with `68m` or
`150m` substituted to screen those checkpoints. It records fixture and model
hashes and case-level scores without placing private paths in the committed
report.

## Next decision

Keep the current default unchanged. The 150M model may be useful as a bounded
descriptive-file reranker if a separate, well-calibrated no-match verifier can
be made lightweight. First improve candidate recall and collect a newly frozen
set of real Windows search tasks, including unsupported descriptions. Evaluate
final Scout results, false matches, exact names, actions/groups, and memory on
an 8–16 GB CPU-only laptop before considering a default switch. This work
requires no Jev API key.
