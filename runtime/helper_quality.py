"""Measure the local semantic helper against actual Scout search shortlists."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import time
from semantic_helper import SemanticHelper


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--device', default='cuda')
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit('Refusing to overwrite predictions.')
    cases = [c for c in json.loads(args.fixtures.read_text(encoding='utf-8'))['cases'] if c['needsModel']]
    start = time.perf_counter()
    helper = SemanticHelper(args.model)
    import atexit
    atexit.register(helper.close)
    cold = time.perf_counter()-start
    if cases:
        helper.rank(cases[0]['modelQuery'], cases[0]['candidates'], deadline_seconds=30)
    answers = {}
    for index, case in enumerate(cases):
        answer = helper.rank(case['modelQuery'], case['candidates'])
        if answer.get('elapsed_ms', 0) > 2000:
            answer = {**answer, 'scores': {}, 'members': [], 'reason': 'deadline'}
        answers[case['Id']] = answer
        if index % 10 == 9:
            print(f'{index+1}/{len(cases)} requests', flush=True)
    times = sorted(a.get('elapsed_ms',0) for a in answers.values())
    metrics = dict(requests=len(cases), coldSeconds=cold, p50Ms=statistics.median(times),
        p95Ms=times[int(len(times)*.95)], timeouts=sum(a.get('reason') == 'deadline' for a in answers.values()))
    report = dict(model=args.model.name, device=args.device, metrics=metrics, answers=answers,
        fixtureHash=hashlib.sha256(args.fixtures.read_bytes()).hexdigest(),
        helperHash=hashlib.sha256(Path(__file__).with_name('semantic_helper.py').read_bytes()).hexdigest())
    args.output.write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps(metrics,indent=2),flush=True)
    helper.close()


if __name__ == '__main__':
    main()
