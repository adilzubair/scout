"""Freeze a metadata-only evaluation; expected answers never use Scout's parser/ranker.

Files are sampled by deterministic hash, grouped by directory into development and
validation splits. Query templates also differ by split. Counts of unique targets,
folders and families are reported separately from the number of query variants.
All output contains private metadata and belongs under artifacts/diagnostics.
"""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone, timedelta
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3

def digest(value):
    return hashlib.sha256(value.lower().encode()).hexdigest()

def within(path, root):
    return path.lower() == root.lower() or path.lower().startswith(root.rstrip('\\/') .lower() + '\\')

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit('Refusing to replace a frozen evaluation. Choose a new output path.')
    db = sqlite3.connect(args.snapshot.resolve().as_uri() + '?mode=ro', uri=True)
    raw = [json.loads(row[0]) for row in db.execute('select json from candidates')]
    settings = json.loads(db.execute('select json from settings').fetchone()[0])
    db.close()
    now = datetime.now().astimezone()
    files, stats = [], {}
    for candidate in raw:
        if candidate['Kind'] != 0:
            continue
        try:
            p = Path(candidate['Target'])
            stat = p.stat()
            if not p.is_file() or stat.st_file_attributes & (2 | 1024 | 4096):
                continue
            candidate['ModifiedAt'] = datetime.fromtimestamp(stat.st_mtime, timezone.utc).isoformat()
            candidate['CreatedAt'] = datetime.fromtimestamp(stat.st_birthtime, timezone.utc).isoformat()
            stats[candidate['Id']] = stat
            files.append(candidate)
        except OSError:
            pass
    roots = [str(Path(os.environ['USERPROFILE']) / name) for name in ('Downloads', 'Desktop', 'Documents', 'Pictures')]
    roots += [str(Path(os.environ['USERPROFILE']) / 'Pictures/Screenshots'), *settings.get('ExtraFolders', [])]
    roots = [r for r in roots if Path(r).is_dir()]
    by_folder = defaultdict(list)
    for c in files:
        by_folder[str(Path(c['Target']).parent)].append(c)
    # Prefer user documents over source-code boilerplate for sampled title tasks.
    extensions = {'.pdf', '.doc', '.docx', '.ppt', '.pptx', '.xlsx', '.xls', '.csv', '.md', '.txt', '.png', '.jpg', '.zip'}
    boring = {'readme', 'license', 'package', 'index', 'changelog', 'requirements', 'favicon', 'icon', 'logo', 'test'}
    folders = [p for p, group in by_folder.items() if len(group) >= 3 and 3 <= len(Path(p).name) <= 42 and any(Path(c['Target']).suffix.lower() in extensions for c in group)]
    folders.sort(key=digest)
    selected = []
    # Add original reported locations to development only; validation is independent.
    telegram = next((p for p in folders if Path(p).name.lower() == 'telegram desktop'), None)
    if telegram:
        selected.append(telegram)
    project_counts = Counter()
    def project(folder):
        root = max((r for r in roots if within(folder, r)), key=len, default=str(Path(folder).anchor))
        relative = folder[len(root):].strip('\\/')
        return root.lower() + '/' + (relative.split('\\')[0].lower() if relative else '')
    for folder in selected:
        project_counts[project(folder)] += 1
    for folder in folders:
        if len(selected) >= 36:
            break
        if project_counts[project(folder)] < 3 and not any(within(folder, other) or within(other, folder) for other in selected):
            selected.append(folder)
            project_counts[project(folder)] += 1
    cases = []
    pdfs = {'.pdf'}
    def add(query, family, split, pool, *, folder=None, direct=False, suffixes=None, basis=None, oldest=False, count=None, since=None, until=None, mode='one', action=0, literal=False, constraint=True):
        eligible = [c for c in pool if (not folder or within(c['Target'], folder)) and (not direct or str(Path(c['Target']).parent).lower() == folder.lower()) and (not suffixes or Path(c['Target']).suffix.lower() in suffixes)]
        if basis:
            def date(c):
                text = c.get(basis)
                return datetime.fromisoformat(text).timestamp() if text else None
            eligible = [c for c in eligible if date(c) is not None and date(c) <= now.timestamp() + 2 and (since is None or date(c) >= since.timestamp()) and (until is None or date(c) < until.timestamp())]
            eligible.sort(key=date, reverse=not oldest)
            tiers = []
            for c in eligible:
                if not tiers or date(tiers[-1][0]) != date(c):
                    tiers.append([])
                tiers[-1].append(c)
            order = [[c['Id'] for c in tier] for tier in tiers]
        else:
            order = [[c['Id'] for c in eligible]] if eligible else []
        expected = [c['Id'] for c in eligible]
        cases.append(dict(id='case-' + str(len(cases)), query=query, family=family, split=split, expected=expected, order=order, mode=mode, count=count,
                          folder=folder, direct=direct, extensions=sorted(suffixes or []), basis=basis, action=action, literal=literal, constraint=constraint))
    for i, folder in enumerate(selected):
        split = 'development' if i % 2 == 0 or folder == telegram else 'validation'
        location = '"' + folder + '"'
        group = by_folder[folder]
        templates = (
            [('latest file in {f}', 'ModifiedAt', False, None, False), ('most recently modified file in {f}', 'ModifiedAt', False, None, False), ('show me the 3 newest files in {f}', 'ModifiedAt', False, 3, False), ('oldest file in {f}', 'ModifiedAt', True, None, False), ('last file I created in {f}', 'CreatedAt', False, None, False), ('latest file directly in {f}', 'ModifiedAt', False, None, True), ('last pdf in {f}', 'ModifiedAt', False, None, False), ('last file I opened in {f}', 'OpenedAt', False, None, False)]
            if split == 'development' else
            [('which file was changed most recently within {f}?', 'ModifiedAt', False, None, False), ('give me the newest three files under {f}', 'ModifiedAt', False, 3, False), ('in {f}, find the latest file', 'ModifiedAt', False, None, False), ('first file created in {f}', 'CreatedAt', True, None, False), ('last pdf from {f}', 'ModifiedAt', False, None, False), ('what was the last file I saved inside {f}?', 'ModifiedAt', False, None, False), ('newest file directly within {f}', 'ModifiedAt', False, None, True), ('most recent file I used in {f}', 'OpenedAt', False, None, False)])
        for template, basis, oldest, count, direct in templates:
            add(template.format(f=location), 'folder-' + ('count' if count else 'direct' if direct else 'pdf' if 'pdf' in template else basis.lower()), split, files, folder=folder, suffixes=pdfs if 'pdf' in template else None, basis=basis, oldest=oldest, count=count, direct=direct)
        day = now.replace(hour=0, minute=0, second=0, microsecond=0)
        add(('files modified yesterday in {f}' if split == 'development' else 'files I edited in {f} yesterday').format(f=location), 'calendar', split, files, folder=folder, basis='ModifiedAt', since=day-timedelta(days=1), until=day, mode='set')
        choices = sorted((c for c in group if Path(c['Target']).suffix.lower() in extensions and not any(w in Path(c['Target']).stem.lower() for w in boring) and len(c['Title']) <= 90), key=lambda c: digest(c['Id']))[:2]
        for c in choices:
            same_name = [other for other in files if other['Title'].lower() == c['Title'].lower()]
            add(c['Title'], 'literal-title', split, same_name, literal=True, constraint=False)
            add(c['Target'], 'absolute-path', split, [c], literal=True, constraint=False)
            stem = Path(c['Target']).stem
            if len(stem) >= 5:
                query = stem[:max(4, len(stem)//2)]
                matching = [other for other in files if Path(other['Target']).stem.lower().startswith(query.lower())]
                add(query, 'filename-prefix', split, matching, constraint=False)
    # Negative answers are known absent tokens, not assumptions about document contents.
    descriptions = [
        ('development', r'invoice', {'.pdf'}, ['pdf for customer billing', 'pdf showing what a customer owes']),
        ('development', r'corporate[ _-]profile', {'.pdf'}, ['pdf introducing the company', 'pdf about our business profile']),
        ('development', r'calendar', {'.md'}, ['markdown with calendar instructions', 'markdown for scheduling']),
        ('development', r'resume|curriculum', {'.pdf'}, ['pdf describing professional background', 'pdf about my work experience']),
        ('validation', r'receipt', {'.pdf'}, ['pdf showing proof of payment', 'pdf recording what I paid for a purchase']),
        ('validation', r'roadmap', {'.md'}, ['markdown describing future plans', 'markdown with the project direction']),
        ('validation', r'schema', {'.json'}, ['json file defining the data structure', 'json file describing the database layout']),
        ('validation', r'guide|manual', {'.pdf'}, ['pdf explaining how to use something', 'pdf with usage instructions']),
    ]
    for split, pattern, suffixes, queries in descriptions:
        matches = [c for c in files if re.search(pattern, c['Title'], re.I) and Path(c['Target']).suffix.lower() in suffixes]
        matching_folders = sorted({str(Path(c['Target']).parent) for c in matches}, key=digest)[:3]
        for folder in matching_folders:
            expected = [c for c in matches if within(c['Target'], folder)]
            for query in queries:
                add(query + ' in "' + folder + '"', 'metadata-description', split, expected, folder=folder, suffixes=suffixes)
    # Description labels above are inferred from explicit filename categories,
    # never from uninspected content, and are reported separately from exact tasks.
    for i in range(16):
        split = 'development' if i % 2 == 0 else 'validation'
        marker = 'scoutmissing' + digest('negative-' + str(i))[:12]
        if i < 8:
            folder = selected[i]
            add(f'latest {marker} pdf in "{folder}"', 'missing-file', split, [], folder=folder, suffixes=pdfs, basis='ModifiedAt', mode='empty')
        else:
            add(f'latest file in "{marker}" folder', 'missing-folder', split, [], folder=marker, mode='empty')
    # Ground command labels directly in the Windows action contract, not parser output.
    for split, terms in [('development', [('bluetooth off','bluetooth-off'), ('wifi off','wifi-off'), ('enable bluetooth','bluetooth-on'), ('turn wifi on','wifi-on'), ('lock screen','lock'), ('dark','appearance')]), ('validation', [('turn off bluetooth','bluetooth-off'), ('disable wi fi','wifi-off'), ('switch on bluetooth','bluetooth-on'), ('wireless on','wifi-on'), ('lock computer','lock'), ('toggle theme','appearance')])]:
        for query, target in terms:
            add(query, 'system-command', split, [{'Id':'system:'+target}], constraint=False)
    unique = {}
    for case in cases:
        key = case['query'].lower()
        if key not in unique:
            unique[key] = case
        elif set(unique[key]['expected']) != set(case['expected']):
            raise ValueError('Duplicate query with conflicting labels')
    cases = list(unique.values())
    summary = dict(cases=len(cases), files=len(files), selectedFolders=len(selected), distinctQueries=len(cases), specificFirstTierTargets=len({t for c in cases for tier in c['order'][:1] if len(tier) <= 10 for t in tier}), splits=dict(Counter(c['split'] for c in cases)), families=dict(Counter(c['family'] for c in cases)), note='Metadata-grounded query variants, not user telemetry; counts of variants are not independent tasks. Sampled folder trees are disjoint and split before evaluation; duplicate filename queries may accept equivalent files elsewhere. No document contents read.')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    dataset = dict(now=now.isoformat(), roots=roots, candidates=files, cases=cases, summary=summary)
    encoded = json.dumps(dataset, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    args.output.write_bytes(encoded)
    args.output.with_suffix('.sha256').write_text(hashlib.sha256(encoded).hexdigest()+'\n')
    print(json.dumps(summary, indent=2))

if __name__ == '__main__':
    main()
