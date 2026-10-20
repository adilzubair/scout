# Kev and Von for Scout — source review, 2026-09-25

## Decision

**Keep both out of Scout's default search path for now.** Neither has been run
on Scout's fixtures or an ordinary Windows laptop. Kev is the closer functional
replacement for Laya, but its smallest current official model needs a 0.8B
Qwen base and the author reports a large quality gap to Jev outside its training
distribution. Von 1.2 is a smaller 395M encoder with a compatible decision API,
but its published FP32 weights are larger than Laya's, its CPU speed is
unreported, and its own public hard-decision results are weak. These are
research candidates, not evidence that Scout has reached Intern's search level.

| Candidate | What it could do for Scout | Published weights / runtime | Quality evidence and limit | Research priority |
| --- | --- | --- | --- | --- |
| [Kev 0.8B](https://huggingface.co/jaredpalmer/kev-0.8b) | Choice, no-match option, binary and score questions in one decision-model API | [Qwen3.5-0.8B-Base](https://huggingface.co/Qwen/Qwen3.5-0.8B-Base/tree/main) is 1.75 GB BF16, plus Kev adapter/head; official Kev repo's 65.5 MB includes tokenizer and adapter, **not the base** | Latest model card reports 0.648 out-of-domain development and 0.697 locked test accuracy on its own tasks; 0.636 on 231 public JevBench items. No Scout task result. | Secondary quality probe only; first prove a small, fast Windows CPU package |
| [Kev 4B](https://huggingface.co/jaredpalmer/kev-4b) | Stronger typed decisions and more knowledge | 4B Qwen base is multi-GB; author measures GPU and Apple Silicon serving, not ordinary Windows CPU | Improved author task results, but out-of-domain development 0.817 versus Jev 0.857; no Scout result. | Optional deep-search reference, not laptop default |
| [Von 1.2](https://huggingface.co/wfzyx/von) | Choice, binary and score questions with a single-pass option scorer for each question | 395M FP32; `model.safetensors` and `option_marker.pt` are **1.58 GB each** in the official repo | Public JevBench evaluation table: easy 100%, standard 63.9%, hard 38.7%; calibration was fitted on these public items. No Scout result. | Functional reference; footprint and CPU latency must be measured before integration |
| Current Scout Laya | Existing typed decision path with Scout's admission policy | English weight 842.6 MB; current Python/CUDA runtime is much larger | Local desktop CPU request p50/p95 1,515/2,141 ms and 36/64 final Scout results on the older development set | Baseline to beat on **final Scout outcomes** |

Kev source: [repository](https://github.com/jaredpalmer/kev), [0.8B card](https://huggingface.co/jaredpalmer/kev-0.8b), [4B card](https://huggingface.co/jaredpalmer/kev-4b), and [base weights](https://huggingface.co/Qwen/Qwen3.5-0.8B-Base/tree/main). Von source: [model card](https://huggingface.co/wfzyx/von), [SDK dependencies](https://github.com/wfzyx/von/blob/master/pyproject.toml), and [published files](https://huggingface.co/wfzyx/von/tree/main). Laya measurements: [lightweight baseline](LIGHTWEIGHT_BASELINE.md).

## Kev: promising contract, weak small-model transfer

Kev is built for the same `choice` / `noul` / `score` decision shapes as Laya's
sidecar. Its 0.8B release is a LoRA adapter and pointer head on a Qwen3.5 base;
the small adapter download does not eliminate the base-model weight. The
current model card explicitly recommends larger Kev where accuracy matters.
On its out-of-domain development set, Kev 0.8B reports 0.648 versus Jev's
0.857; the latest update improves long real-document and generated-skill
tests, but the author says those gains are mainly within the trained sources.
The same card reports MMLU-Pro 0.230 for Kev 0.8B versus 0.840 for Jev, so
there is no evidence that this small model is Scout's sought-after world
knowledge upgrade. [0.8B model card](https://huggingface.co/jaredpalmer/kev-0.8b).

The official [serving table](https://github.com/jaredpalmer/kev#serving-performance)
reports Kev 0.8B at 22.7 ms for six short-text questions on an L4 GPU; it does
not publish Windows CPU latency. Kev 4B is slower even on Apple Silicon: 721 ms
for five questions about new text on an M5, according to the author. Kev's
large-size quality gains therefore cannot be treated as evidence of a suitable
CPU-only laptop default. Its [Python dependencies](https://github.com/jaredpalmer/kev/blob/main/pyproject.toml)
include PyTorch, Transformers and PEFT; there is no official compact Windows
ONNX package in the checked release.

A [third-party Kev.js export](https://huggingface.co/ai-ecoverse/kev.js)
advertises 0.79–0.82 GB INT8/WebGPU downloads for 0.8B. It is a converted
browser variant, not an official Scout/Windows CPU benchmark; its parity,
latency, RAM and license chain would need independent checking before use.

## Von: useful design, heavy stock package

Von 1.2 uses a ModernBERT-large backbone with one packed sequence for the
options of a **single question**. Its 1.2 attention mask makes answers
independent of option order; the author reports zero answer flips on the
public hard-tier reorder test, down from 49.5% for 1.1. This is relevant for
file lists whose order changes. [Von 1.2 card](https://huggingface.co/wfzyx/von).

The stock SDK's [model constructor](https://github.com/wfzyx/von/blob/master/src/von/models/option_marker.py)
loads `model.safetensors` through `AutoModel.from_pretrained`; its
[backend](https://github.com/wfzyx/von/blob/master/src/von/backends/option_marker_backend.py)
then downloads and loads `option_marker.pt`, a full state dictionary. Thus the
published repository totals 3.16 GB, while each file represents roughly a
1.58 GB FP32 model. A purpose-built loader might avoid storing both copies,
but that optimization is not available in the stock path and has not been
verified. The backend's `evaluate` loop calls each question separately, so
its advertised single pass is **per question**, whereas Scout asks many
questions across chunks, finalist checks, action and readiness. Its ~18 ms
figure is for GPU; the checked primary sources provide no Windows CPU p95 or
working-set measurement. [Runtime code](https://github.com/wfzyx/von/blob/master/src/von/backends/option_marker_backend.py), [files](https://huggingface.co/wfzyx/von/tree/main), [README benchmark table](https://github.com/wfzyx/von#empirical-benchmark).

The Von model card describes English-only operation and difficulty with long
multi-hop policy decisions. Its public JevBench calibration is fitted on the
same 231 public items; even the card's hard-tier accuracy is inconsistent
between the calibration table (36.9%) and evaluation table (38.7%). The README
also retains a 1.1 comparison table after announcing 1.2. Those figures are
research signals, not a reliable claim of parity with Jev or Intern on file
search. [Von card](https://huggingface.co/wfzyx/von), [README](https://github.com/wfzyx/von).

## What a key-free Scout test would require

No Jev API key is necessary. If either remains interesting, pin its revision
and run the exact package and CPU path against Scout's existing case set first,
then a new untouched task holdout. Measure final file/action outcomes, no-match
false positives, candidate recall, p50/p95 including load, peak working set,
package size, and 8–16 GB Windows laptop performance. The Scout result must be
compared with its non-AI path and Laya baseline, while Intern is assessed with
the same *user-visible tasks* through its UI. Published decision benchmarks
cannot establish search-product parity.

The larger problem remains data coverage: Scout currently indexes filenames
and metadata, not file contents by default. Neither Kev nor Von can identify
the topic of a file whose indexed fields contain no such evidence, or rescue a
relevant file absent from the candidate pool. See [replacement research](LAYA_REPLACEMENT_RESEARCH.md)
and [Ettin local screen](ETTIN_OFFLINE_SCREEN.md).
