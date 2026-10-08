"""Paired frozen-corpus runner. Raw reports and filenames stay in ignored artifacts."""
import argparse
import hashlib
import json
import statistics
import subprocess
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--redesigned', type=Path, required=True)
    parser.add_argument('--corpus', type=Path, action='append', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--rounds', type=int, default=2)
    parser.add_argument('--aggregate-only', action='store_true', help='Recheck and summarize retained reports without rerunning engines')
    args = parser.parse_args()
    if args.rounds < 2 or args.rounds % 2:
        parser.error('--rounds must be a positive even number of at least two')
    args.output.mkdir(parents=True, exist_ok=True)
    engines = {'baseline': args.baseline.resolve(), 'redesigned': args.redesigned.resolve()}
    summary = {'warmupPasses': 10, 'measuredPasses': 30, 'rounds': args.rounds, 'corpora': []}
    for corpus in args.corpus:
        reports = {key: [] for key in engines}
        for round_number in range(args.rounds):
            order = list(engines) if round_number % 2 == 0 else list(reversed(engines))
            for key in order:
                output = args.output / f'{corpus.stem}-{key}-{round_number}.json'
                if not args.aggregate_only:
                    subprocess.run(['dotnet', str(engines[key]), '--index-benchmark', str(corpus.resolve()), str(output.resolve())], check=True)
                reports[key].append(json.loads(output.read_text(encoding='utf-8')))
                print(f'Completed {corpus.stem}: {key}, round {round_number + 1}', flush=True)
        differences = []
        reference = reports['baseline'][0]['queries']
        for key, rows in reports.items():
            for round_number, report in enumerate(rows):
                if report['warmupPasses'] != 10 or report['measuredPasses'] != 30:
                    raise ValueError('Unexpected measurement protocol')
                for old, new in zip(reference, report['queries'], strict=True):
                    for field in ('query', 'hits', 'recall', 'Notice'):
                        if old[field] != new[field]:
                            differences.append({'engine': key, 'round': round_number, 'query': old['query'], 'field': field})
        metrics = {}
        for key, rows in reports.items():
            metrics[key] = {field: statistics.median(row[field] for row in rows) for field in ('count', 'prepareMs', 'managedBytes', 'privateBytes', 'p50Ms', 'p95Ms')}
            metrics[key]['meanAllocatedBytesPerSearch'] = statistics.mean(query['allocatedBytes'] for row in rows for query in row['queries'])
        summary['corpora'].append({'fixture': corpus.name, 'sha256': hashlib.sha256(corpus.read_bytes()).hexdigest(), 'metrics': metrics, 'orderedResultDifferences': differences})
        (args.output / 'comparison.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
