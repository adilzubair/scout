"""Offline CPU screening for a compact filename reranker.

Reads a Scout search-evaluation report and a local model directory. This probes
ranking and negative-case separation only; it does not replace Scout's full
retrieval, Laya decisions, or final-result evaluation. Inputs may contain private
metadata, so keep outputs under ignored artifacts/diagnostics.
"""

import argparse
import hashlib
import json
from pathlib import Path
import statistics
import time


def percentile(values, fraction):
    return sorted(values)[min(len(values) - 1, int(len(values) * fraction))] if values else 0


def auc(positive, negative):
    if not positive or not negative:
        return None
    return sum((p > n) + 0.5 * (p == n) for p in positive for n in negative) / (len(positive) * len(negative))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--threads', type=int, default=4)
    parser.add_argument('--batch-size', type=int, default=16)
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit('Refusing to overwrite probe results.')
    if args.threads < 1 or args.batch_size < 1:
        raise SystemExit('Threads and batch size must be positive.')
    from transformers import AutoModelForSequenceClassification, AutoTokenizer
    import torch

    torch.set_num_threads(args.threads)
    cases = [case for case in json.loads(args.fixtures.read_text(encoding='utf-8'))['cases']
             if case['candidates'] and all(candidate['kind'] == 'file' for candidate in case['candidates'])]
    started = time.perf_counter()
    tokenizer = AutoTokenizer.from_pretrained(args.model, local_files_only=True)
    model = AutoModelForSequenceClassification.from_pretrained(args.model, local_files_only=True).to('cpu').eval()
    cold = time.perf_counter() - started

    results, times, positive_scores, negative_scores = {}, [], [], []
    with torch.inference_mode():
        for index, case in enumerate(cases):
            begin = time.perf_counter()
            candidates = case['candidates']
            query = case.get('modelQuery') or case['Query']
            labels = [candidate['title'].replace('_', ' ').replace('-', ' ') for candidate in candidates]
            scores = []
            for offset in range(0, len(labels), args.batch_size):
                batch = labels[offset:offset + args.batch_size]
                inputs = tokenizer([query] * len(batch), batch, padding=True, truncation=True,
                                   max_length=128, return_tensors='pt')
                logits = model(**inputs).logits
                if logits.shape[-1] != 1:
                    raise ValueError('Expected a one-logit cross encoder.')
                scores.extend(logits[:, 0].float().tolist())
            elapsed = (time.perf_counter() - begin) * 1000
            times.append(elapsed)
            winner = max(range(len(scores)), key=scores.__getitem__)
            expected = set(case['Expected'])
            recalled = bool(expected.intersection(candidate['id'] for candidate in candidates))
            result = dict(positive=bool(expected), recalled=recalled,
                          topId=candidates[winner]['id'], topLogit=scores[winner],
                          topCorrect=candidates[winner]['id'] in expected if expected else None,
                          elapsedMs=elapsed)
            results[case['Id']] = result
            (positive_scores if expected else negative_scores).append(scores[winner])
            if index % 20 == 19:
                print(f'{index + 1}/{len(cases)} cases', flush=True)

    positive = [row for row in results.values() if row['positive']]
    negative = [row for row in results.values() if not row['positive']]
    summary = dict(cases=len(results), positives=len(positive), negatives=len(negative),
                   recalled=sum(row['recalled'] for row in positive),
                   topCorrect=sum(row['topCorrect'] for row in positive),
                   negativeSeparationAuc=auc(positive_scores, negative_scores),
                   coldSeconds=cold, p50Ms=statistics.median(times), p95Ms=percentile(times, .95),
                   positiveMedianLogit=statistics.median(positive_scores) if positive_scores else None,
                   negativeMedianLogit=statistics.median(negative_scores) if negative_scores else None)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(model=args.model.name, threads=args.threads,
        fixtureHash=hashlib.sha256(args.fixtures.read_bytes()).hexdigest(),
        weightBytes=sum(path.stat().st_size for path in args.model.glob('*.safetensors')),
        summary=summary, cases=results), indent=2), encoding='utf-8')
    print(json.dumps(summary, indent=2), flush=True)


if __name__ == '__main__':
    main()
