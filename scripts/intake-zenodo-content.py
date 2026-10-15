"""Intake explicitly selected public-content candidates from pinned Zenodo metadata.

Originals are not opened or executed. Source/rights/group review and holdout labels
remain unapproved. Run the production no-model review exporter separately.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import time
import urllib.parse
import urllib.request
import uuid

spec = importlib.util.spec_from_file_location('media_intake', Path(__file__).with_name('multimodal-media-intake.py'))
media = importlib.util.module_from_spec(spec)
spec.loader.exec_module(media)

MAX_FILE = 32 * 1024**2
MAX_TOTAL = 512 * 1024**2
TRANSFER_SECONDS = 600
FORMATS = {**{suffix: 'documents' for suffix in ('.pdf', '.docx', '.odt', '.txt', '.md')},
           **{suffix: 'presentations' for suffix in ('.pptx', '.odp')},
           **{suffix: 'spreadsheets' for suffix in ('.xlsx', '.ods', '.csv')}}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('unexpected_zenodo_redirect')


def bounded_json(path, limit):
    if not 0 < path.stat().st_size <= limit:
        raise ValueError('metadata_size_budget')
    return json.loads(path.read_text(encoding='utf-8-sig'))


def select(definition, metadata_root):
    selection = bounded_json(definition, 256 * 1024)
    if selection.get('schemaVersion') != 1 or not 0 < len(selection['records']) <= 100:
        raise ValueError('invalid_selection_definition')
    items, snapshots, identities, records = [], [], set(), set()
    for chosen in selection['records']:
        identity = chosen['id']
        if not isinstance(identity, str) or not re.fullmatch('[0-9]{1,20}', identity) or identity in records or not re.fullmatch('[a-f0-9]{64}', chosen['metadataSha256']):
            raise ValueError('invalid_record_identity')
        records.add(identity)
        path = media.owned(metadata_root, metadata_root / (identity + '.json'))
        record = bounded_json(path, 4 * 1024**2)
        if media.digest(path) != chosen['metadataSha256'] or str(record['id']) != identity:
            raise ValueError('publisher_metadata_changed')
        metadata = record['metadata']
        concept = str(record['conceptrecid'])
        if not re.fullmatch('[0-9]{1,20}', concept) or metadata.get('access_right') != 'open' or metadata.get('license', {}).get('id') != 'cc-by-4.0':
            raise ValueError('record_access_license_or_group_requires_review')
        creators = metadata.get('creators', [])
        if not isinstance(metadata.get('title'), str) or not 0 < len(metadata['title']) <= 4000 or not 0 < len(creators) <= 100 or any(not isinstance(c.get('name'), str) or not 0 < len(c['name']) <= 500 for c in creators):
            raise ValueError('missing_title_or_attribution')
        files = {file['key']: file for file in record['files']}
        if len(files) != len(record['files']) or not 0 < len(chosen['files']) <= 100 or len(set(chosen['files'])) != len(chosen['files']):
            raise ValueError('duplicate_or_missing_file_selection')
        for name in chosen['files']:
            if not isinstance(name, str) or not 0 < len(name) <= 240 or any(ord(c) < 32 or c in '/\\:' for c in name) or Path(name).suffix.lower() not in FORMATS or name not in files:
                raise ValueError('unsupported_or_invalid_selected_file')
            file = files[name]
            checksum = file.get('checksum', '')
            size = file.get('size')
            if type(size) is not int or not 0 < size <= MAX_FILE or not re.fullmatch('md5:[a-f0-9]{32}', checksum):
                raise ValueError('source_size_or_checksum_budget')
            source_id = 'zenodo-' + identity + '-' + checksum[4:16]
            if source_id in identities:
                raise ValueError('duplicate_selected_source')
            identities.add(source_id)
            url = 'https://zenodo.org/api/records/' + identity + '/files/' + urllib.parse.quote(name, safe='') + '/content'
            # Ignore arbitrary linked URLs in metadata; fetch only the selected record/key.
            local = 'sources/f-' + hashlib.sha256(source_id.encode()).hexdigest()[:12] + Path(name).suffix.lower()
            items.append(dict(id=source_id, recordId=identity, conceptRecordId=concept, originalName=name, family=FORMATS[Path(name).suffix.lower()],
                original=dict(url=url, bytes=size, md5=checksum[4:]), localFile=local,
                attribution=dict(title=metadata['title'], creators=creators, doi=record.get('doi'), conceptDoi=record.get('conceptdoi'),
                    recordUrl='https://zenodo.org/records/' + identity, license='CC BY 4.0', licenseUrl='https://creativecommons.org/licenses/by/4.0/'),
                relatedGroupCandidate='zenodo-concept-' + concept, relatedIdentifiers=metadata.get('related_identifiers', []),
                metadataSha256=chosen['metadataSha256'], review=dict(content=False, license=False, relatedGroups=False, split=None, labelsApproved=False)))
        snapshots.append((path, identity + '.json'))
    if not 0 < len(items) <= 100 or sum(item['original']['bytes'] for item in items) > MAX_TOTAL:
        raise ValueError('batch_source_budget')
    return items, snapshots


def publish_bytes(root, relative, payload):
    path = media.owned(root, root / relative)
    if path.exists():
        if path.stat().st_size != len(payload) or media.digest(path) != hashlib.sha256(payload).hexdigest():
            raise ValueError('existing_provenance_changed')
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = media.owned(root, path.with_suffix('.' + uuid.uuid4().hex + '.part'))
    try:
        with temporary.open('xb') as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.link(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def download(root, item, offline):
    original = item['original']
    target = media.owned(root, root / item['localFile'])
    if target.exists():
        if target.stat().st_size != original['bytes'] or media.digest(target, 'md5') != original['md5']:
            raise ValueError('existing_source_changed')
    else:
        if offline:
            raise FileNotFoundError('missing_offline_source')
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = media.owned(root, target.with_suffix('.' + uuid.uuid4().hex + '.part'))
        request = urllib.request.Request(original['url'], headers={'User-Agent': media.USER_AGENT})
        began = time.monotonic()
        count = 0
        try:
            with urllib.request.build_opener(NoRedirect()).open(request, timeout=30) as response, temporary.open('xb') as output:
                declared = response.headers.get('Content-Length')
                if declared is not None and int(declared) != original['bytes']:
                    raise ValueError('publisher_size_changed')
                while chunk := response.read(65536):
                    count += len(chunk)
                    if count > original['bytes']:
                        raise ValueError('source_transfer_size_budget')
                    if time.monotonic() - began > TRANSFER_SECONDS:
                        raise ValueError('source_transfer_time_budget')
                    output.write(chunk)
                output.flush()
                os.fsync(output.fileno())
            if count != original['bytes'] or media.digest(temporary, 'md5') != original['md5']:
                raise ValueError('publisher_source_checksum_mismatch')
            os.link(temporary, target)
        finally:
            temporary.unlink(missing_ok=True)
    return dict(file=item['localFile'], bytes=target.stat().st_size, sha256=media.digest(target))


def intake(definition, metadata_root, root, offline=False):
    definition, metadata_root = Path(definition), Path(os.path.abspath(metadata_root))
    root = Path(os.path.abspath(root))
    media.owned(root, root / 'intake.json')
    root.mkdir(parents=True, exist_ok=True)
    with media.owner_lock(root, '.zenodo.lock'):
        if any((root / name).exists() for name in ('intake.json', 'dataset.json', 'freeze.json')):
            raise FileExistsError('refusing_to_replace_candidate_or_frozen_evidence')
        definition_hash = media.digest(definition)
        items, snapshots = select(definition, metadata_root)
        # Preserve the actual publisher metadata/selection bytes, not reconstructed subsets.
        publish_bytes(root, 'provenance/selection.json', definition.read_bytes())
        for path, name in snapshots:
            publish_bytes(root, 'provenance/' + name, path.read_bytes())
        for item in items:
            item['local'] = download(root, item, offline)
            del item['localFile']
            print('Verified ' + item['id'] + ' (' + item['family'] + '); review pending.', flush=True)
        if media.digest(definition) != definition_hash or any(media.digest(path) != next(record['metadataSha256'] for record in items if record['recordId'] + '.json' == name) for path, name in snapshots):
            raise ValueError('selection_or_metadata_changed_during_intake')
        media.exclusive_json(root / 'intake.json', dict(schemaVersion=1, purpose='unreviewed candidate-content-pool',
            intendedUse='Unopened independent corpus candidates; no inference or approved task labels', createdUtc=datetime.now(timezone.utc).isoformat(),
            selectionSha256=definition_hash, items=items))
        print(f'Prepared {len(items)} original files. Related versions, alternate formats and derivatives must stay in one split; rights/content/group review remains open.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--selection', type=Path, required=True)
    parser.add_argument('--metadata', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--offline', action='store_true')
    args = parser.parse_args()
    intake(args.selection, args.metadata, args.output, args.offline)
