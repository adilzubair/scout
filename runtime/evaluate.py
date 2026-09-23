"""Compare all checkpoints to the exact C# shortlist and local ranking."""
import argparse
import gc
import json
import os
from pathlib import Path
import statistics
import time

os.environ.setdefault('USE_TF', '0')
os.environ.setdefault('HF_HUB_OFFLINE', '1')
from ranking import rank
from server import load_agent

def percentile(values, p):
    return sorted(values)[min(len(values) - 1, int(len(values) * p))] if values else 0

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--models', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--device', default='cpu')
    parser.add_argument('--checkpoint', choices=['all', 'english', 'multilingual', 'typed-decisions'], default='all')
    args = parser.parse_args()
    cases = json.loads(args.fixtures.read_text())['cases']
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = {'dataset': '200 synthetic cases, fixed C# shortlist; not representative real-user validation', 'device': args.device, 'models': {}}
    for model in (['english', 'multilingual', 'typed-decisions'] if args.checkpoint == 'all' else [args.checkpoint]):
        print('Loading ' + model, flush=True)
        start = time.perf_counter()
        try:
            agent = load_agent(model, args.models, args.device)
        except Exception as error:
            report['models'][model] = {'error': str(error)}
            args.output.write_text(json.dumps(report, indent=2))
            continue
        cold_seconds = time.perf_counter() - start
        # Explicit warm-up excluded from latency measurements.
        first = next(c for c in cases if c['candidates'])
        rank(agent, first['query'], first['candidates'])
        results = []
        for index, case in enumerate(cases):
            start = time.perf_counter()
            output = rank(agent, case['query'], case['candidates'], case['plural']) if case['candidates'] else {'scores': {}, 'members': []}
            elapsed = (time.perf_counter() - start) * 1000
            # Match desktop deadline behavior: results over two seconds are discarded.
            scores = output['scores'] if elapsed <= 2000 else {}
            top = max(scores, key=scores.get) if scores else case['local_top']
            if scores and output.get('none', 0) > max(scores.values()):
                # Desktop keeps local results on none; do not claim a benefit it does not deliver.
                top = case['local_top']
            results.append({'id': case['id'], 'split': case['split'], 'expected': case['expected'], 'local_top': case['local_top'], 'top': top, 'ms': elapsed, 'members': output['members'] if elapsed <= 2000 else [], 'expected_members': case['expected_members'], 'considered': output.get('considered', 0)})
            if index % 25 == 24:
                print(f'{model}: {index + 1}/{len(cases)}', flush=True)
        summary = {'cold_seconds': cold_seconds, 'results': results}
        for split in ['tuning', 'heldout']:
            rows = [r for r in results if r['split'] == split]
            local = sum(r['local_top'] == r['expected'] for r in rows) / len(rows)
            accuracy = sum(r['top'] == r['expected'] for r in rows) / len(rows)
            emitted = sum(len(r['members']) for r in rows)
            correct = sum(len(set(r['members']) & set(r['expected_members'])) for r in rows)
            precision = correct / emitted if emitted else 0
            expected = sum(len(r['expected_members']) for r in rows)
            p95 = percentile([r['ms'] for r in rows], .95)
            summary[split] = {'local_top1': local, 'laya_top1': accuracy, 'improvement': accuracy - local, 'group_precision': precision, 'group_recall': correct / expected if expected else 0, 'p95_ms': p95, 'p50_ms': statistics.median(r['ms'] for r in rows), 'passes_gate': accuracy - local >= .05 and precision >= .95 and p95 < 1000}
        report['models'][model] = summary
        print(model + ': ' + json.dumps(summary['heldout']), flush=True)
        del agent
        gc.collect()
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
        args.output.write_text(json.dumps(report, indent=2))
    eligible = [(name, value) for name, value in report['models'].items() if value.get('heldout', {}).get('passes_gate')]
    report['recommendation'] = sorted(eligible, key=lambda x: (-x[1]['heldout']['laya_top1'], x[1]['heldout']['p95_ms']))[0][0] if eligible else 'Keep Laya experimental and opt-in'
    args.output.write_text(json.dumps(report, indent=2))
    print(report['recommendation'], flush=True)

if __name__ == '__main__':
    main()
