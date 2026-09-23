"""Real typed-inference measurements for the C# parity challenge corpus."""
import argparse
import gc
import json
from pathlib import Path
import statistics
import time
from ranking import rank
from server import load_agent


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--models', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--device', default='cuda')
    parser.add_argument('--checkpoints', nargs='+', default=['english', 'multilingual', 'typed-decisions'])
    args = parser.parse_args()
    cases = json.loads(args.fixtures.read_text(encoding='utf-8'))['cases']
    report = {'models': {}, 'note': 'Hand-authored challenge corpus, not a live comparison with Jev.'}
    for checkpoint in args.checkpoints:
        started = time.perf_counter()
        agent = load_agent(checkpoint, args.models, args.device)
        cold = time.perf_counter() - started
        first = next(c for c in cases if c['candidates'])
        rank(agent, first['query'], first['candidates'])
        values, times = {}, []
        for index, case in enumerate(cases):
            output = rank(agent, case['query'], case['candidates'], case['plural']) if case['candidates'] else {'scores': {}, 'members': []}
            if output.get('elapsed_ms', 0) > 2000:
                output['scores'], output['members'] = {}, []
            values[case['id']] = output
            if case['candidates']:
                times.append(output['elapsed_ms'])
            if index % 15 == 14:
                print(f'{checkpoint}: {index + 1}/{len(cases)}', flush=True)
        metrics = {'device': str(agent.device), 'cold_seconds': cold, 'p50_ms': statistics.median(times), 'p95_ms': sorted(times)[int(len(times) * .95)], 'max_ms': max(times)}
        report['models'][checkpoint] = {'metrics': metrics, 'answers': values}
        args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(checkpoint, metrics, flush=True)
        del agent
        gc.collect()
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()


if __name__ == '__main__':
    main()
