"""Prepare unopened COCO image candidates with upstream human captions, not approved labels."""
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
import urllib.error
import uuid
import zipfile

spec = importlib.util.spec_from_file_location('media_intake', Path(__file__).with_name('multimodal-media-intake.py'))
media = importlib.util.module_from_spec(spec)
spec.loader.exec_module(media)

ARCHIVE_SHA256 = '113a836d90195ee1f884e704da6304dfaaecff1f023f49b6ca93c4aaae470268'
ARCHIVE_BYTES = 252907541
ARCHIVE_URL = 'https://s3.amazonaws.com/images.cocodataset.org/annotations/annotations_trainval2017.zip'
IMAGE_BASE = 'https://s3.amazonaws.com/images.cocodataset.org/val2017/'
TERMS_URL = 'https://raw.githubusercontent.com/cocodataset/cocodataset.github.io/master/dataset/termsofuse.htm'
TERMS_BLOB = '70b68cd7e22da5fcc90b0c32455ddf789050e53d'
SEED = 'scout-unopened-image-candidates-v1'
MAX_IMAGE = 8 * 1024**2
MAX_TOTAL = 512 * 1024**2


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('unexpected_upstream_redirect')


def fetch(url, limit, image=False):
    request = urllib.request.Request(url, headers={'User-Agent': media.USER_AGENT})
    with urllib.request.build_opener(NoRedirect()).open(request, timeout=30) as response:
        began = time.monotonic()
        length = int(response.headers.get('Content-Length', '0'))
        if not 0 < length <= limit:
            raise ValueError('upstream_size_budget')
        payload = bytearray()
        while chunk := response.read(65536):
            payload.extend(chunk)
            if len(payload) > limit or time.monotonic() - began > 120:
                raise ValueError('upstream_transfer_budget')
        if len(payload) != length:
            raise ValueError('upstream_length_mismatch')
        etag = response.headers.get('ETag', '')
        if image and (response.headers.get_content_type() != 'image/jpeg' or not payload.startswith(b'\xff\xd8\xff') or
                      not re.fullmatch('"[a-f0-9]{32}"', etag) or hashlib.md5(payload).hexdigest() != etag.strip('"')):
            raise ValueError('upstream_image_integrity')
        return bytes(payload), etag


def publish_bytes(root, relative, payload):
    target = media.owned(root, root / relative)
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


def annotations(archive):
    if archive.stat().st_size != ARCHIVE_BYTES or media.digest(archive) != ARCHIVE_SHA256:
        raise ValueError('annotation_archive_changed')
    values, hashes = {}, {}
    with zipfile.ZipFile(archive) as source:
        for key in ('captions', 'instances'):
            name = 'annotations/' + key + '_val2017.json'
            matches = [entry for entry in source.infolist() if entry.filename == name]
            if len(matches) != 1 or not 0 < matches[0].file_size <= 24 * 1024**2:
                raise ValueError('annotation_member_budget_or_duplicate')
            with source.open(matches[0]) as stream:
                payload = stream.read(24 * 1024**2 + 1)
            if len(payload) != matches[0].file_size:
                raise ValueError('annotation_member_size')
            values[key] = json.loads(payload)
            hashes[key] = hashlib.sha256(payload).hexdigest()
    return values, hashes


def candidates(data, count):
    if type(count) is not int or not 1 <= count <= 100:
        raise ValueError('candidate_count_budget')
    captions, instances = data['captions'], data['instances']
    licenses = {item['id']: item for item in captions['licenses']}
    if licenses.get(4, {}).get('url') != 'http://creativecommons.org/licenses/by/2.0/':
        raise ValueError('unexpected_image_license_mapping')
    images = {item['id']: item for item in captions['images']}
    other = {item['id']: item for item in instances['images']}
    if len(images) != 5000 or len(images) != len(captions['images']) or images != other or len(other) != len(instances['images']):
        raise ValueError('image_annotation_identity_mismatch')
    labels = {item['id']: item['name'] for item in instances['categories']}
    by_image, objects = {}, {}
    for item in captions['annotations']:
        if item['image_id'] not in images or type(item['id']) is not int or not isinstance(item['caption'], str) or not 0 < len(item['caption']) <= 2000:
            raise ValueError('invalid_caption')
        by_image.setdefault(item['image_id'], []).append(dict(annotationId=item['id'], text=item['caption']))
    for item in instances['annotations']:
        if item['image_id'] not in images or item['category_id'] not in labels:
            raise ValueError('invalid_object_annotation')
        objects.setdefault(item['image_id'], []).append(dict(annotationId=item['id'], category=labels[item['category_id']], categoryId=item['category_id'], bbox=item['bbox'], isCrowd=item['iscrowd']))
    chosen, originals = [], set()
    order = sorted(images.values(), key=lambda item: hashlib.sha256((SEED + ':' + str(item['id'])).encode()).hexdigest())
    for image in order:
        if image['license'] != 4:
            continue
        identity = image['id']
        name = image['file_name']
        flickr = urllib.parse.urlsplit(image['flickr_url'])
        photo = re.fullmatch(r'/[0-9]+/([0-9]+)_[a-f0-9]+(?:_[a-z])?\.jpg', flickr.path)
        if (type(identity) is not int or not 0 < identity < 10**12 or name != f'{identity:012}.jpg' or
                image['coco_url'] not in ('http://images.cocodataset.org/val2017/' + name, 'https://images.cocodataset.org/val2017/' + name) or
                type(image['width']) is not int or type(image['height']) is not int or not 0 < image['width'] * image['height'] <= 1000000 or min(image['width'], image['height']) < 1 or
                flickr.scheme not in ('http', 'https') or not re.fullmatch(r'(?:farm[0-9]+|live)\.staticflickr\.com', flickr.hostname or '') or
                flickr.port is not None or flickr.username or flickr.password or flickr.query or flickr.fragment or not photo):
            raise ValueError('invalid_image_identity_or_origin')
        original = photo.group(1)
        if original in originals:
            continue
        human = sorted(by_image.get(identity, []), key=lambda item: item['annotationId'])
        if len(human) < 5 or len({item['annotationId'] for item in human}) != len(human):
            raise ValueError('incomplete_or_duplicate_human_captions')
        chosen.append(dict(image=image, flickrPhotoId=original, captions=human, objects=objects.get(identity, [])))
        originals.add(original)
        if len(chosen) == count:
            return chosen
    raise ValueError('insufficient_eligible_images')


def image_file(root, candidate, remaining, offline):
    identity = candidate['image']['id']
    name = candidate['image']['file_name']
    relative = 'sources/f-' + hashlib.sha256(('coco-val2017-' + str(identity)).encode()).hexdigest()[:12] + '.jpg'
    target = media.owned(root, root / relative)
    receipt_path = media.owned(root, root / 'provenance' / ('coco-' + str(identity) + '.json'))
    if receipt_path.exists() and receipt_path.stat().st_size > 16384:
        raise ValueError('invalid_download_receipt')
    expected = json.loads(receipt_path.read_text(encoding='utf-8')) if receipt_path.exists() else None
    url = IMAGE_BASE + name
    if expected and (expected.get('id') != identity or expected.get('url') != url or expected.get('file') != relative or
                     type(expected.get('bytes')) is not int or not 0 < expected['bytes'] <= min(MAX_IMAGE, remaining) or
                     not re.fullmatch('[a-f0-9]{64}', expected.get('sha256', ''))):
        raise ValueError('invalid_download_receipt')
    if target.exists():
        if not expected or not target.is_file() or target.stat().st_size != expected['bytes'] or media.digest(target) != expected['sha256']:
            raise ValueError('cached_image_changed')
        return expected
    if offline:
        raise FileNotFoundError('missing_offline_image')
    payload, etag = fetch(url, min(MAX_IMAGE, remaining), image=True)
    actual = dict(id=identity, url=url, file=relative, bytes=len(payload), sha256=hashlib.sha256(payload).hexdigest(), publisherEtag=etag)
    if expected and actual != expected:
        raise ValueError('publisher_image_changed')
    if not expected:
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        media.exclusive_json(receipt_path, actual)
    publish_bytes(root, relative, payload)
    return actual


def intake(archive, root, count=40, offline=False):
    archive = Path(archive)
    root = Path(os.path.abspath(root))
    media.owned(root, root / 'intake.json')
    root.mkdir(parents=True, exist_ok=True)
    with media.owner_lock(root, '.coco.lock'):
        if (root / 'intake.json').exists() or (root / 'dataset.json').exists() or (root / 'freeze.json').exists():
            raise FileExistsError('refusing_to_replace_review_or_evaluation_evidence')
        data, hashes = annotations(archive)
        chosen = candidates(data, count)
        terms = media.owned(root, root / 'provenance/terms.htm')
        if terms.exists() and terms.stat().st_size > 65536:
            raise ValueError('publisher_terms_budget')
        if offline and not terms.exists():
            raise FileNotFoundError('missing_offline_terms')
        payload = terms.read_bytes() if terms.exists() else fetch(TERMS_URL, 65536)[0]
        if hashlib.sha1(b'blob ' + str(len(payload)).encode() + b'\0' + payload).hexdigest() != TERMS_BLOB:
            raise ValueError('publisher_terms_changed')
        if not terms.exists():
            publish_bytes(root, 'provenance/terms.htm', payload)
        items, remaining = [], MAX_TOTAL
        for candidate in chosen:
            image = candidate['image']
            local = image_file(root, candidate, remaining, offline)
            remaining -= local['bytes']
            items.append(dict(id='coco-' + str(image['id']), family='images', local={key: local[key] for key in ('file', 'bytes', 'sha256')},
                original=dict(url=local['url'], bytes=local['bytes'], publisherEtag=local['publisherEtag'], width=image['width'], height=image['height'], flickrUrl=image['flickr_url'], flickrPhotoId=candidate['flickrPhotoId']),
                license=dict(name='CC BY 2.0', url='https://creativecommons.org/licenses/by/2.0/', attributionRequired=True, attributionUrl='https://www.flickr.com/photo.gne?id=' + candidate['flickrPhotoId'], author=None, status='publisher snapshot; original attribution/current rights review pending'),
                humanAnnotations=dict(captions=candidate['captions'], objects=candidate['objects'], license='COCO Consortium annotations: CC BY 4.0', metadataSha256=hashes),
                relatedGroupCandidate='flickr-photo-' + candidate['flickrPhotoId'], review=dict(content=False, license=False, relatedGroups=False, split=None, labelsApproved=False)))
            print('Verified coco-' + str(image['id']) + ' (unopened image candidate)', flush=True)
        media.exclusive_json(root / 'intake.json', dict(schemaVersion=1, purpose='unreviewed candidate-media-pool', intendedUse='unopened holdout candidate pool; no predictions or approved task labels',
            createdUtc=datetime.now(timezone.utc).isoformat(), selectionSeed=SEED, annotationArchive=dict(url=ARCHIVE_URL, bytes=ARCHIVE_BYTES, sha256=ARCHIVE_SHA256), metadataSha256=hashes,
            terms=dict(url=TERMS_URL, gitBlobSha1=TERMS_BLOB, sha256=hashlib.sha256(payload).hexdigest()), items=items))
        print(f'Prepared {len(items)} images / {sum(len(item["humanAnnotations"]["captions"]) for item in items)} upstream captions. Content, attribution and related-group review remain open. No inference or label approval.')


def photo_url(url, identity):
    parsed = urllib.parse.urlsplit(url)
    if (parsed.scheme != 'https' or parsed.hostname != 'www.flickr.com' or parsed.port not in (None, 443) or parsed.username or parsed.password or
            parsed.query or parsed.fragment or not re.fullmatch(r'/photos/[^/]+/' + re.escape(identity) + '/?', parsed.path)):
        raise ValueError('invalid_canonical_photo')
    return url


class UnresolvedPhoto(ValueError):
    pass


class PhotoRedirect(urllib.request.HTTPRedirectHandler):
    def __init__(self, identity):
        super().__init__()
        self.identity = identity

    def redirect_request(self, request, fp, code, msg, headers, newurl):
        parsed = urllib.parse.urlsplit(newurl)
        normal_lookup = (parsed.scheme == 'https' and parsed.netloc == 'www.flickr.com' and parsed.path == '/photo.gne' and
                         urllib.parse.parse_qs(parsed.query, keep_blank_values=True) == {'rb': ['1'], 'id': [self.identity]} and not parsed.fragment)
        if not normal_lookup:
            try:
                photo_url(newurl, self.identity)
            except ValueError:
                raise UnresolvedPhoto('unexpected_canonical_route') from None
        return urllib.request.Request(newurl, headers={'User-Agent': media.USER_AGENT}, method='HEAD')


def attribution_lookup(identity):
    if not re.fullmatch('[0-9]{1,20}', identity):
        raise ValueError('invalid_photo_identity')
    request = urllib.request.Request('https://www.flickr.com/photo.gne?id=' + identity, headers={'User-Agent': media.USER_AGENT}, method='HEAD')
    try:
        with urllib.request.build_opener(PhotoRedirect(identity)).open(request, timeout=30) as response:
            try:
                canonical = photo_url(response.geturl(), identity)
            except ValueError:
                raise UnresolvedPhoto('unexpected_canonical_route') from None
        api = 'https://www.flickr.com/services/oembed/?format=json&url=' + urllib.parse.quote(canonical, safe='')
        with urllib.request.build_opener(NoRedirect()).open(urllib.request.Request(api, headers={'User-Agent': media.USER_AGENT}), timeout=30) as response:
            payload = response.read(65537)
        if len(payload) > 65536:
            raise ValueError('attribution_response_budget')
        data = json.loads(payload)
        if (data.get('type') != 'photo' or not isinstance(data.get('author_name'), str) or not 0 < len(data['author_name']) <= 500 or
                not isinstance(data.get('author_url'), str) or not isinstance(data.get('license'), str) or len(data['license']) > 100 or
                data.get('license_url') is not None and (not isinstance(data['license_url'], str) or len(data['license_url']) > 1000) or
                not re.fullmatch('[0-9]{1,3}', str(data.get('license_id', '')))):
            raise UnresolvedPhoto('invalid_attribution_response')
        author = urllib.parse.urlsplit(data['author_url'])
        if author.scheme != 'https' or author.hostname != 'www.flickr.com' or author.port not in (None, 443) or author.username or author.password or author.query or author.fragment or not re.fullmatch('/photos/[^/]+/?', author.path):
            raise UnresolvedPhoto('invalid_attribution_author')
        license_id = int(data['license_id'])
        license_url = data.get('license_url') or ''
        current_by = license_id == 4 and data['license'] == 'CC BY 2.0' and license_url in ('http://creativecommons.org/licenses/by/2.0/', 'https://creativecommons.org/licenses/by/2.0/')
        return dict(status='current_by_metadata_matches' if current_by else 'license_conflict', canonicalUrl=canonical, apiUrl=api,
            responseSha256=hashlib.sha256(payload).hexdigest(), author=data['author_name'], authorUrl=data['author_url'], license=data['license'], licenseId=license_id, licenseUrl=license_url)
    except UnresolvedPhoto as error:
        return dict(status='original_unresolved', reason=str(error))
    except urllib.error.HTTPError as error:
        code = error.code
        error.close()
        if code not in (404, 410):
            raise # rate limits, authorization failures and server failures stop the batch
        return dict(status='original_unavailable', httpStatus=code)


def capture_attribution(root, offline=False):
    root = Path(os.path.abspath(root))
    manifest = media.owned(root, root / 'intake.json')
    with media.owner_lock(root, '.coco-attribution.lock'):
        summary = media.owned(root, root / 'attribution.json')
        if summary.exists():
            raise FileExistsError('refusing_to_replace_attribution_evidence')
        if not 0 < manifest.stat().st_size <= 4 * 1024**2:
            raise ValueError('invalid_candidate_manifest')
        data = json.loads(manifest.read_text(encoding='utf-8'))
        if data.get('purpose') != 'unreviewed candidate-media-pool' or not 0 < len(data['items']) <= 100:
            raise ValueError('invalid_candidate_manifest')
        intake_hash = media.digest(manifest)
        results = []
        for item in data['items']:
            identity = item['original']['flickrPhotoId']
            if not re.fullmatch('coco-[0-9]{1,12}', item['id']) or not re.fullmatch('[0-9]{1,20}', identity):
                raise ValueError('invalid_candidate_identity')
            local = item['local']
            if not re.fullmatch(r'sources/f-[a-f0-9]{12}\.jpg', local['file']):
                raise ValueError('invalid_candidate_source')
            source = media.owned(root, root / local['file'])
            if source.stat().st_size != local['bytes'] or media.digest(source) != local['sha256']:
                raise ValueError('candidate_source_changed')
            receipt = media.owned(root, root / 'attribution' / (item['id'] + '.json'))
            if receipt.exists():
                if receipt.stat().st_size > 65536: raise ValueError('invalid_attribution_receipt')
                result = json.loads(receipt.read_text(encoding='utf-8'))
                if result.get('intakeSha256') != intake_hash or result.get('sourceSha256') != local['sha256'] or result.get('flickrPhotoId') != identity or result.get('id') != item['id']:
                    raise ValueError('attribution_receipt_changed')
                if result.get('approvedForHoldout') is not False or result.get('evidence', {}).get('status') not in ('current_by_metadata_matches', 'license_conflict', 'original_unresolved', 'original_unavailable'):
                    raise ValueError('invalid_attribution_receipt')
            else:
                if offline: raise FileNotFoundError('missing_offline_attribution')
                evidence = attribution_lookup(identity)
                result = dict(id=item['id'], flickrPhotoId=identity, sourceSha256=local['sha256'], intakeSha256=intake_hash,
                    capturedUtc=datetime.now(timezone.utc).isoformat(), evidence=evidence, approvedForHoldout=False)
                receipt.parent.mkdir(parents=True, exist_ok=True)
                media.exclusive_json(receipt, result)
                time.sleep(1)
            results.append(result)
            print(item['id'] + ': ' + result['evidence']['status'], flush=True)
        media.exclusive_json(summary, dict(schemaVersion=1, purpose='Current attribution evidence, not license clearance or label approval', intakeSha256=intake_hash,
            capturedUtc=datetime.now(timezone.utc).isoformat(), sourceCount=len(results), results=results, approvedForHoldout=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--annotations', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--count', type=int, default=40)
    parser.add_argument('--offline', action='store_true')
    parser.add_argument('--capture-attribution', action='store_true', help='Capture current Flickr author/license evidence for an existing candidate pool without approving it.')
    args = parser.parse_args()
    if args.capture_attribution:
        capture_attribution(args.output, args.offline)
    elif args.annotations is None:
        parser.error('--annotations is required for image intake')
    else:
        intake(args.annotations, args.output, args.count, args.offline)
