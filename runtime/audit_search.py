"""Explain final-search misses from saved metadata and model predictions."""
import argparse
from collections import Counter
import json
from pathlib import Path


def audit(report, predictions):
    rows = []
    for case in report['cases']:
        if case['Family'] != 'metadata-description':
            continue
        answer = predictions['answers'].get(case['Id'], {})
        scores = answer.get('scores', {})
        expected = set(case['Expected'])
        winner = max(scores, key=scores.get, default=None)
        desired = max((key for key in scores if key in expected), key=scores.get, default=None)
        titles = {c['id']: c['title'] for c in case['candidates']}
        if case['finalCorrect']:
            reason = 'correct'
        elif not any(key in expected for key in titles):
            reason = 'retrieval'
        elif not desired:
            reason = 'context-or-deadline'
        elif winner not in expected:
            reason = 'wrong-choice'
        elif answer.get('none', 0) >= scores[winner]:
            reason = 'abstention'
        else:
            reason = 'admission-or-blending'
        rows.append(dict(id=case['Id'], query=case.get('modelQuery', case['Query']), reason=reason,
                         winner=titles.get(winner), expected=titles.get(desired),
                         score=scores.get(winner), expectedScore=scores.get(desired),
                         relevance=answer.get('relevance', {}).get(desired), none=answer.get('none'),
                         considered=answer.get('considered'), shortlisted=len(titles)))
    return dict(summary=dict(Counter(row['reason'] for row in rows)), cases=rows)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--predictions', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = audit(json.loads(args.report.read_text(encoding='utf-8')), json.loads(args.predictions.read_text(encoding='utf-8')))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding='utf-8')
    print(json.dumps(result, indent=2, ensure_ascii=True))


if __name__ == '__main__':
    main()
