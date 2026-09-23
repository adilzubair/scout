"""Development experiment: compare state wording on the disclosed tuning corpus."""
import json
from pathlib import Path
from server import load_agent
from batching import predict_many
from ranking import fits

cases = json.loads(Path('artifacts/evaluation/parity-cases.json').read_text(encoding='utf-8'))['cases'][40:54]
patterns = [
    ('minimal', lambda q, c: q, 'Which item best matches the user search?'),
    ('description', lambda q, c: 'The user wants to find: ' + q, 'Select the file whose name means what the user describes. Match by meaning, not only exact words.'),
    ('duplicate', lambda q, c: {'query': q, 'candidates': [{'id': 'c'+str(i), 'title': v['title']} for i,v in enumerate(c)]}, 'Which candidate does the user mean?'),
    ('remember', lambda q, c: q, 'The user describes an item they remember. Which filename is the best semantic match?'),
    ('retrieval', lambda q, c: 'Search query: ' + q, 'Choose the most relevant search result. Pick none if all results are unrelated.'),
]
report = {}
for model in ['english', 'multilingual']:
    agent = load_agent(model, '.runtime/models', 'cuda')
    report[model] = {}
    for name, state_func, instruction in patterns:
        jobs = []
        for case in cases:
            question = {'type': 'choice', 'instructions': instruction, 'criteria': {**{'c'+str(i): c['title'] for i,c in enumerate(case['candidates'])}, 'none':'No related item'}}
            state = state_func(case['query'], case['candidates'])
            assert fits(agent, state, question)
            jobs.append((state, question))
        outputs = predict_many(agent, jobs)
        rows = []
        for case, answer in zip(cases, outputs):
            p = answer['probabilities']; key = max(p, key=p.get)
            top = None if key == 'none' else case['candidates'][int(key[1:])]['id']
            rows.append({'query':case['query'], 'top':top, 'p':p[key], 'none':p['none'], 'expected':case['expected'], 'correct': top in case['expected'] if case['expected'] else top is None})
        report[model][name] = rows
        print(model, name, sum(r['correct'] for r in rows), '/', len(rows), flush=True)
    del agent
Path('artifacts/evaluation/prompt-probe.json').write_text(json.dumps(report, indent=2))
