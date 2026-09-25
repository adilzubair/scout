"""Offline Scout filename screen for Ettin's modular cross encoders.

Ettin's published ONNX artifact exports the transformer only. The repository's
small Dense/LayerNorm heads are applied here with NumPy. This is a development
probe, not Scout integration or a calibrated no-match policy. Keep fixture and
output files under ignored diagnostics because their case IDs are local data.
"""

import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics
import time


def percentile(values, fraction):
    return sorted(values)[min(len(values) - 1, int(len(values) * fraction))] if values else 0


def auc(positive, negative):
    if not positive or not negative:
        return None
    return sum((p > n) + 0.5 * (p == n) for p in positive for n in negative) / (len(positive) * len(negative))


def admission_at_false_match_budget(rows, budget):
    """Optimistic in-sample upper bound, never a deployment threshold."""
    thresholds = sorted({row['topLogit'] for row in rows.values()})
    thresholds.append(float('inf'))
    best = None
    for threshold in thresholds:
        false = sum(not row['positive'] and row['topLogit'] > threshold for row in rows.values())
        if false > budget:
            continue
        correct = sum(row['positive'] and row['topCorrect'] and row['topLogit'] > threshold for row in rows.values())
        if best is None or correct > best['correctPositives']:
            best = {'correctPositives': correct, 'falseMatches': false,
                    'threshold': threshold if math.isfinite(threshold) else None}
    return best


def memory_mib():
    """Current and process-lifetime peak working set on Windows."""
    import ctypes
    from ctypes import wintypes

    class Counters(ctypes.Structure):
        _fields_ = [('cb', wintypes.DWORD), ('PageFaultCount', wintypes.DWORD)] + [
            (name, ctypes.c_size_t) for name in ('PeakWorkingSetSize', 'WorkingSetSize',
                'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage',
                'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]

    counters = Counters()
    counters.cb = ctypes.sizeof(counters)
    current_process = ctypes.windll.kernel32.GetCurrentProcess
    current_process.restype = wintypes.HANDLE
    get_memory = ctypes.windll.psapi.GetProcessMemoryInfo
    get_memory.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
    get_memory.restype = wintypes.BOOL
    if not get_memory(current_process(), ctypes.byref(counters), counters.cb):
        raise OSError('GetProcessMemoryInfo failed')
    scale = 1024 * 1024
    return {'workingSetMiB': counters.WorkingSetSize / scale,
            'peakWorkingSetMiB': counters.PeakWorkingSetSize / scale,
            'privateMiB': counters.PrivateUsage / scale}


def load_ranker(model_dir, threads, max_length, backend):
    import numpy as np
    from safetensors.numpy import load_file
    from tokenizers import Tokenizer

    tokenizer = Tokenizer.from_file(str(model_dir / 'tokenizer.json'))
    tokenizer.enable_truncation(max_length=max_length, strategy='longest_first')
    tokenizer.enable_padding(pad_id=tokenizer.token_to_id('[PAD]'), pad_token='[PAD]')
    if backend == 'onnx-int8':
        import onnxruntime as ort

        options = ort.SessionOptions()
        options.intra_op_num_threads = threads
        options.inter_op_num_threads = 1
        session = ort.InferenceSession(str(model_dir / 'onnx' / 'model_quint8_avx2.onnx'),
                                       sess_options=options, providers=['CPUExecutionProvider'])
        if [out.name for out in session.get_outputs()] != ['last_hidden_state']:
            raise ValueError('Expected transformer-only ONNX export; recheck the head implementation.')
        width = session.get_outputs()[0].shape[-1]

        def encode_backbone(inputs):
            return session.run(None, inputs)[0][:, 0, :]
    else:
        import torch
        from transformers import AutoModel

        torch.set_num_threads(threads)
        model = AutoModel.from_pretrained(model_dir, local_files_only=True).to('cpu').eval()
        width = model.config.hidden_size

        def encode_backbone(inputs):
            with torch.inference_mode():
                return model(**{key: torch.from_numpy(value) for key, value in inputs.items()}) \
                    .last_hidden_state[:, 0, :].numpy()

    dense = load_file(str(model_dir / '2_Dense' / 'model.safetensors'))
    norm = load_file(str(model_dir / '3_LayerNorm' / 'model.safetensors'))
    output = load_file(str(model_dir / '4_Dense' / 'model.safetensors'))
    if dense['linear.weight'].shape != (width, width) or output['linear.weight'].shape != (1, width):
        raise ValueError('Head weights do not match the ONNX backbone.')
    erf = np.vectorize(math.erf, otypes=[np.float32])

    def rank(query, titles, batch_size):
        scores = []
        for offset in range(0, len(titles), batch_size):
            pairs = [(query, title) for title in titles[offset:offset + batch_size]]
            encoded = tokenizer.encode_batch(pairs)
            inputs = {key: np.asarray([getattr(item, attr) for item in encoded], dtype=np.int64)
                      for key, attr in [('input_ids', 'ids'), ('attention_mask', 'attention_mask')]}
            hidden = encode_backbone(inputs)
            x = hidden @ dense['linear.weight'].T
            x = .5 * x * (1 + erf(x / math.sqrt(2)))  # PyTorch GELU, not tanh approximation.
            x = (x - x.mean(axis=1, keepdims=True)) / np.sqrt(x.var(axis=1, keepdims=True) + 1e-5)
            x = x * norm['norm.weight'] + norm['norm.bias']
            scores.extend((x @ output['linear.weight'].T + output['linear.bias'])[:, 0].tolist())
        return scores

    return rank


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--threads', type=int, default=4)
    parser.add_argument('--batch-size', type=int, default=16)
    parser.add_argument('--max-length', type=int, default=128)
    parser.add_argument('--backend', choices=['onnx-int8', 'torch-fp32'], default='onnx-int8')
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit('Refusing to overwrite probe results.')
    if min(args.threads, args.batch_size, args.max_length) < 1:
        raise SystemExit('Threads, batch size and max length must be positive.')
    cases = [case for case in json.loads(args.fixtures.read_text(encoding='utf-8'))['cases']
             if case['candidates'] and all(candidate['kind'] == 'file' for candidate in case['candidates'])]
    started = time.perf_counter()
    rank = load_ranker(args.model, args.threads, args.max_length, args.backend)
    cold = time.perf_counter() - started
    loaded_memory = memory_mib()
    rows, elapsed_ms, positive_scores, negative_scores = {}, [], [], []
    for index, case in enumerate(cases):
        begin = time.perf_counter()
        candidates = case['candidates']
        query = case.get('modelQuery') or case['Query']
        titles = [candidate['title'].replace('_', ' ').replace('-', ' ') for candidate in candidates]
        scores = rank(query, titles, args.batch_size)
        elapsed = (time.perf_counter() - begin) * 1000
        elapsed_ms.append(elapsed)
        winner = max(range(len(scores)), key=scores.__getitem__)
        expected = set(case['Expected'])
        row = {'positive': bool(expected),
               'recalled': bool(expected.intersection(candidate['id'] for candidate in candidates)),
               'topId': candidates[winner]['id'], 'topLogit': scores[winner],
               'topCorrect': candidates[winner]['id'] in expected if expected else None,
               'elapsedMs': elapsed}
        rows[case['Id']] = row
        (positive_scores if expected else negative_scores).append(scores[winner])
        if index % 20 == 19:
            print(f'{index + 1}/{len(cases)} cases', flush=True)
    positive = [row for row in rows.values() if row['positive']]
    negative = [row for row in rows.values() if not row['positive']]
    summary = {'cases': len(rows), 'positives': len(positive), 'negatives': len(negative),
               'recalled': sum(row['recalled'] for row in positive),
               'topCorrect': sum(row['topCorrect'] for row in positive),
               'negativeSeparationAuc': auc(positive_scores, negative_scores),
               'optimisticAdmission4': admission_at_false_match_budget(rows, 4),
               'coldSeconds': cold, 'p50Ms': statistics.median(elapsed_ms),
               'p95Ms': percentile(elapsed_ms, .95), 'loadedMemory': loaded_memory,
               'endMemory': memory_mib()}
    model_file = (args.model / 'onnx' / 'model_quint8_avx2.onnx' if args.backend == 'onnx-int8'
                  else args.model / 'model.safetensors')
    report = {'model': args.model.name, 'threads': args.threads, 'batchSize': args.batch_size,
              'maxLength': args.max_length, 'backend': args.backend,
              'fixtureHash': hashlib.sha256(args.fixtures.read_bytes()).hexdigest(),
              'modelHash': hashlib.sha256(model_file.read_bytes()).hexdigest(),
              'weightBytes': model_file.stat().st_size, 'summary': summary, 'cases': rows}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, allow_nan=False), encoding='utf-8')
    print(json.dumps(summary, indent=2), flush=True)


if __name__ == '__main__':
    main()
