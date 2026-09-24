"""Run only model requests Scout would actually make, then assess with C# policy.

Inputs and outputs contain private metadata; keep them in artifacts/diagnostics.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import statistics
import time
from server import load_agent


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--models', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--ranker', type=Path, default=Path(__file__).with_name('ranking.py'))
    parser.add_argument('--device', default='cuda')
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit('Refusing to overwrite predictions.')
    spec = importlib.util.spec_from_file_location('measured_ranker', args.ranker)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    cases = [c for c in json.loads(args.fixtures.read_text(encoding='utf-8'))['cases'] if c['needsModel']]
    started = time.perf_counter()
    agent = load_agent('english', args.models, args.device)
    cold = time.perf_counter() - started
    if cases:
        module.rank(agent, cases[0].get('modelQuery', cases[0]['Query']), cases[0]['candidates'])
    values, times = {}, []
    for index, case in enumerate(cases):
        answer = module.rank(agent, case.get('modelQuery', case['Query']), case['candidates'], case['Intent']['Plural'], context=case.get('context', {}))
        if answer.get('elapsed_ms', 0) > 2000:
            answer = {**answer, 'scores': {}, 'members': [], 'reason': 'deadline'}
        values[case['Id']] = answer
        times.append(answer.get('elapsed_ms', 0))
        if index % 10 == 9:
            print(f'{index + 1}/{len(cases)} model requests', flush=True)
    metrics = dict(requests=len(cases), coldSeconds=cold, p50Ms=statistics.median(times) if times else 0,
                   p95Ms=sorted(times)[min(len(times)-1, int(len(times)*.95))] if times else 0,
                   timeouts=sum(a.get('reason') == 'deadline' for a in values.values()))
    report = dict(rankerHash=hashlib.sha256(args.ranker.read_bytes()).hexdigest(), fixtureHash=hashlib.sha256(args.fixtures.read_bytes()).hexdigest(), device=str(agent.device), metrics=metrics, answers=values)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(metrics, indent=2), flush=True)


if __name__ == '__main__':
    main()
