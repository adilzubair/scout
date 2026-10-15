"""Prepare a pinned, human-labelled natural-audio DEVELOPMENT diagnostic, without inference.

Only the CC BY ESC-10 subset is eligible. All source bytes and upstream notices stay
in the caller's ignored output directory. Class labels support clip-level presence;
they do not justify negative queries, event timestamps or release-holdout claims.
"""
import argparse
import csv
from datetime import datetime, timezone
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import urllib.request
import uuid
import wave

spec = importlib.util.spec_from_file_location('media_intake', Path(__file__).with_name('multimodal-media-intake.py'))
media = importlib.util.module_from_spec(spec)
spec.loader.exec_module(media)

REVISION = '33c8ce9eb2cf0b1c2f8bcf322eb349b6be34dbb6'
TREE = '9ea9d1ecbba679ed67e0ddd4b0c2008d01e578df'
BASE = 'https://raw.githubusercontent.com/karolpiczak/ESC-50/' + REVISION + '/'
UPSTREAM = 'https://github.com/karolpiczak/ESC-50/tree/' + REVISION
METADATA = {
    'LICENSE': ('7b7f93640c5d9c62c67bdd68a509991b4db61fbd', 284523),
    'README.md': ('2683497ab2846704afd8f5270d014afe359d2ecd', 30513),
    'meta/esc50.csv': ('a5e6b65627d6a8a9fe937a112836230bd54b380d', 93742),
}
QUERIES = {
    'dog': 'Find a recording of a dog.',
    'rooster': 'Find a recording of a rooster.',
    'rain': 'Find a recording of rain.',
    'sea_waves': 'Find a recording of sea waves.',
    'crackling_fire': 'Find a recording of a crackling fire.',
    'crying_baby': 'Find a recording of a baby crying.',
    'sneezing': 'Find a recording of someone sneezing.',
    'clock_tick': 'Find a recording of a ticking clock.',
    'helicopter': 'Find a recording of a helicopter.',
    'chainsaw': 'Find a recording of a chainsaw.',
}
MAX_FILE = 2 * 1024**2


def git_blob(payload):
    return hashlib.sha1(b'blob ' + str(len(payload)).encode() + b'\0' + payload).hexdigest()


def verified_tree(data):
    if data.get('sha') != TREE or data.get('truncated') is not False or not 0 < len(data['tree']) <= 5000:
        raise ValueError('invalid_pinned_tree')
    items, children = {}, {'': []}
    for item in data['tree']:
        path = item['path']
        if (path in items or not isinstance(path, str) or any(part in ('', '.', '..') for part in path.split('/')) or
                any(ord(c) < 32 or c in '\\:' for c in path) or not re.fullmatch('[0-9a-f]{40}', item['sha']) or
                (item['mode'], item['type']) not in {('040000', 'tree'), ('100644', 'blob'), ('100755', 'blob')}):
            raise ValueError('invalid_tree_entry')
        items[path] = item
        parent = path.rpartition('/')[0]
        children.setdefault(parent, []).append(item)
        if item['type'] == 'tree': children.setdefault(path, [])
    # Reconstruct Git tree objects: cached JSON must prove membership in the pinned
    # root, rather than merely repeat its SHA while changing an audio checksum.
    for directory in sorted(children, key=lambda value: value.count('/') + bool(value), reverse=True):
        if directory and (directory not in items or items[directory]['type'] != 'tree'):
            raise ValueError('missing_tree_parent')
        entries = sorted(children[directory], key=lambda item: (item['path'].rsplit('/', 1)[-1] + ('/' if item['type'] == 'tree' else '')).encode('utf-8'))
        payload = b''.join(item['mode'].lstrip('0').encode() + b' ' + item['path'].rsplit('/', 1)[-1].encode('utf-8') + b'\0' + bytes.fromhex(item['sha']) for item in entries)
        actual = hashlib.sha1(b'tree ' + str(len(payload)).encode() + b'\0' + payload).hexdigest()
        if actual != (items[directory]['sha'] if directory else TREE):
            raise ValueError('tree_checksum_mismatch')
    return items


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('unexpected_upstream_redirect')


def fetch(url):
    # URLs are constructed exclusively from fixed hosts, revision and validated paths.
    request = urllib.request.Request(url, headers={'User-Agent': media.USER_AGENT})
    with urllib.request.build_opener(NoRedirect()).open(request, timeout=30) as response:
        payload = response.read(MAX_FILE + 1)
    if len(payload) > MAX_FILE:
        raise ValueError('upstream_size_budget')
    return payload


def cached(root, relative, url, expected=None, offline=False):
    target = media.owned(root, root / relative)
    if target.exists():
        if not target.is_file() or target.stat().st_size > MAX_FILE:
            raise ValueError('invalid_cached_source')
        payload = target.read_bytes()
    else:
        if offline:
            raise FileNotFoundError('missing_offline_source')
        payload = fetch(url)
    if not payload or len(payload) > MAX_FILE or (expected and (len(payload) != expected[1] or git_blob(payload) != expected[0])):
        raise ValueError('upstream_blob_changed')
    if not target.exists():
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = media.owned(root, target.with_suffix('.' + uuid.uuid4().hex + '.part'))
        try:
            with temporary.open('xb') as stream:
                stream.write(payload)
                stream.flush()
                os.fsync(stream.fileno())
            os.link(temporary, target)
        finally:
            temporary.unlink(missing_ok=True)
    return payload


def select(csv_text, license_text, per_class):
    if type(per_class) is not int or not 1 <= per_class <= 5:
        raise ValueError('selection_budget')
    if 'The ESC-10 subset is licensed as a Creative Commons Attribution 3.0 Unported' not in license_text:
        raise ValueError('missing_subset_license')
    rows = list(csv.DictReader(io.StringIO(csv_text)))
    if len(rows) != 2000 or {row['category'] for row in rows if row['esc10'] == 'True'} != set(QUERIES):
        raise ValueError('unexpected_upstream_categories')
    selected, originals = [], set()
    for category in sorted(QUERIES):
        for fold in range(1, per_class + 1):
            choices = sorted((row for row in rows if row['category'] == category and row['esc10'] == 'True' and row['fold'] == str(fold)), key=lambda row: row['filename'])
            for row in choices:
                name = row['filename']
                match = re.fullmatch(r'([1-5])-([0-9]+)-([A-Z])-([0-9]{1,2})\.wav', name)
                if not match or match.group(1) != row['fold'] or match.group(2) != row['src_file'] or match.group(3) != row['take'] or match.group(4) != row['target']:
                    raise ValueError('invalid_upstream_identity')
                if row['src_file'] in originals:
                    continue
                # The upstream attribution list uses the earlier OGG clip names.
                prefix = '-'.join(name.split('-')[:3])
                notices = [line for line in license_text.splitlines() if line.startswith('- [' + prefix + '.ogg]:')]
                if len(notices) != 1 or not notices[0].endswith(('[CC0]', '[CC-BY]')):
                    continue
                originals.add(row['src_file'])
                selected.append(dict(row, attribution=notices[0]))
                break
            else:
                raise ValueError('no_unique_eligible_source_in_fold')
    return selected


def validate_wave(payload):
    with wave.open(io.BytesIO(payload), 'rb') as source:
        if (source.getnchannels(), source.getsampwidth(), source.getframerate(), source.getnframes(), source.getcomptype()) != (1, 2, 44100, 220500, 'NONE'):
            raise ValueError('unexpected_clip_format')
        if len(source.readframes(220501)) != 441000:
            raise ValueError('truncated_clip')


def prepare(root, per_class=2, offline=False):
    root = Path(os.path.abspath(root))
    media.owned(root, root / 'dataset.json')
    root.mkdir(parents=True, exist_ok=True)
    with media.owner_lock(root, '.esc10.lock'):
        if (root / 'dataset.json').exists() or (root / 'freeze.json').exists() or (root / 'provenance.json').exists():
            raise FileExistsError('refusing_to_replace_evaluation_evidence')
        metadata = {name: cached(root, 'provenance/' + name.replace('/', '-'), BASE + name, expected, offline) for name, expected in METADATA.items()}
        tree_bytes = cached(root, 'provenance/tree-' + TREE + '.json', 'https://api.github.com/repos/karolpiczak/ESC-50/git/trees/' + TREE + '?recursive=1', offline=offline)
        blobs = verified_tree(json.loads(tree_bytes))
        chosen = select(metadata['meta/esc50.csv'].decode('utf-8'), metadata['LICENSE'].decode('utf-8'), per_class)
        sources, tasks, provenance = [], [], []
        for row in chosen:
            path = 'audio/' + row['filename']
            blob = blobs[path]
            if blob['type'] != 'blob' or not re.fullmatch('[0-9a-f]{40}', blob['sha']) or type(blob['size']) is not int or not 0 < blob['size'] <= MAX_FILE:
                raise ValueError('invalid_audio_blob')
            opaque = 'f-' + hashlib.sha256(('esc50-' + row['filename']).encode()).hexdigest()[:12]
            relative = 'sources/' + opaque + '.wav'
            payload = cached(root, relative, BASE + path, (blob['sha'], blob['size']), offline)
            validate_wave(payload)
            group = 'esc10-' + row['category'].replace('_', '-')
            sources.append(dict(id=opaque, group=group, split='development', family='audio', file=relative, bytes=len(payload), sha256=hashlib.sha256(payload).hexdigest(),
                grounding='Upstream human-authored ESC-10 clip-level class: ' + row['category'] + '. Whole five-second clip only; no event boundary or exhaustive absence labels. Metadata CSV and attribution are retained in provenance/.',
                license='ESC-10 subset: CC BY 3.0; Karol J. Piczak (2015), ESC: Dataset for Environmental Sound Classification. ' + row['attribution']))
            provenance.append(dict(source=opaque, filename=row['filename'], originalFreesoundId=row['src_file'], fold=int(row['fold']), category=row['category'], gitBlobSha1=blob['sha'], attribution=row['attribution']))
            print('Verified ' + opaque + ' (audio)', flush=True)
        for category, query in QUERIES.items():
            group = 'esc10-' + category.replace('_', '-')
            expected = [dict(source=source['id'], locator=dict(kind='time', startSeconds=0, endSeconds=5)) for source in sources if source['group'] == group]
            tasks.append(dict(id=group, group=group, split='development', family='audio', query=query, kind='ambiguous' if len(expected) > 1 else 'positive', expected=expected))
        provenance = dict(schemaVersion=1, upstream=UPSTREAM, revision=REVISION, tree=TREE, createdUtc=datetime.now(timezone.utc).isoformat(),
            metadata={name: dict(gitBlobSha1=expected[0], sha256=hashlib.sha256(metadata[name]).hexdigest()) for name, expected in METADATA.items()}, treeJsonSha256=hashlib.sha256(tree_bytes).hexdigest(),
            selection='First eligible unique original per class in each of folds 1..N, before inference; all classes kept in development. Class fragments share a group. No training-overlap guarantee.',
            limits='Weak positive labels only. No negative tasks, precise timing labels, independent holdout or general quality claim. All known matching class clips are acceptable.', sources=provenance)
        media.exclusive_json(root / 'provenance.json', provenance)
        media.exclusive_json(root / 'dataset.json', dict(schemaVersion=1, id='esc10-natural-audio-development-v1', purpose='development-pilot', now='2026-10-15T00:00:00Z', languages=['en'], sources=sources, tasks=tasks))
        print(f'Prepared {len(sources)} clips / {len(tasks)} class tasks. Freeze separately before evaluation. No inference performed.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--per-class', type=int, default=2, choices=range(1, 6))
    parser.add_argument('--offline', action='store_true')
    args = parser.parse_args()
    prepare(args.output, args.per_class, args.offline)
