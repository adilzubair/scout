"""Offline contracts for unopened, licensed image candidate intake."""
import copy
from email.message import Message
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request
import zipfile

spec = importlib.util.spec_from_file_location('coco_intake', Path(__file__).with_name('intake-coco-images.py'))
coco = importlib.util.module_from_spec(spec)
spec.loader.exec_module(coco)


class CocoTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        images = [dict(id=i, file_name=f'{i:012}.jpg', license=4 if i <= 10 else 1, width=640, height=480,
            flickr_url=f'http://farm1.staticflickr.com/1/{10000+i}_abc123_z.jpg', coco_url=f'http://images.cocodataset.org/val2017/{i:012}.jpg') for i in range(1, 5001)]
        annotations = [dict(id=i*10+n, image_id=i, caption=f'Human caption {n} for image {i}.') for i in range(1, 11) for n in range(5)]
        self.data = dict(captions=dict(images=images, licenses=[dict(id=4, url='http://creativecommons.org/licenses/by/2.0/')], annotations=annotations),
            instances=dict(images=copy.deepcopy(images), categories=[dict(id=1, name='person')], annotations=[]))
        self.payload = b'\xff\xd8\xfftransport fixture only'
        self.etag = '"' + hashlib.md5(self.payload).hexdigest() + '"'
        self.terms = b'pinned publisher notice'
        self.terms_blob = hashlib.sha1(b'blob ' + str(len(self.terms)).encode() + b'\0' + self.terms).hexdigest()

    def candidate(self): return coco.candidates(self.data, 1)[0]

    def test_selection_uses_only_by_images_unique_originals_and_original_human_captions(self):
        chosen = coco.candidates(self.data, 10)
        self.assertEqual({item['image']['id'] for item in chosen}, set(range(1, 11)))
        self.assertEqual(chosen, coco.candidates(self.data, 10))
        self.assertEqual(len({item['flickrPhotoId'] for item in chosen}), 10)
        self.assertTrue(all(len(item['captions']) == 5 for item in chosen))
        for count in (0, 101, True):
            with self.assertRaisesRegex(ValueError, 'candidate_count_budget'): coco.candidates(self.data, count)
        with self.assertRaisesRegex(ValueError, 'insufficient_eligible'): coco.candidates(self.data, 11)

    def test_archive_license_identity_caption_and_origin_changes_rejected(self):
        changed = copy.deepcopy(self.data)
        changed['captions']['licenses'][0]['url'] = 'http://creativecommons.org/licenses/by-nc/2.0/'
        with self.assertRaisesRegex(ValueError, 'image_license_mapping'): coco.candidates(changed, 1)
        changed = copy.deepcopy(self.data)
        changed['instances']['images'][0]['width'] = 1
        with self.assertRaisesRegex(ValueError, 'image_annotation_identity'): coco.candidates(changed, 1)
        for field, value in [('file_name', '../escape.jpg'), ('coco_url', 'https://other.test/source.jpg'), ('flickr_url', 'https://user:password@farm1.staticflickr.com/1/1_abc_z.jpg'), ('width', 1000001)]:
            changed = copy.deepcopy(self.data)
            for key in ('captions', 'instances'): changed[key]['images'][0][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'invalid_image_identity'): coco.candidates(changed, 10)
        changed = copy.deepcopy(self.data)
        changed['captions']['annotations'][0]['id'] = changed['captions']['annotations'][1]['id']
        with self.assertRaisesRegex(ValueError, 'duplicate_human_captions'): coco.candidates(changed, 10)

    def test_archive_hash_checked_and_only_two_bounded_members_read(self):
        archive = self.root / 'annotations.zip'
        with zipfile.ZipFile(archive, 'w') as file:
            for key in ('captions', 'instances'): file.writestr('annotations/' + key + '_val2017.json', json.dumps(self.data[key]))
            file.writestr('../../outside.txt', b'never extracted')
        with patch.object(coco, 'ARCHIVE_BYTES', archive.stat().st_size), patch.object(coco, 'ARCHIVE_SHA256', coco.media.digest(archive)):
            data, hashes = coco.annotations(archive)
        self.assertEqual(data, self.data)
        self.assertEqual(set(hashes), {'captions', 'instances'})
        self.assertEqual([file.name for file in self.root.iterdir()], ['annotations.zip'])
        with self.assertRaisesRegex(ValueError, 'annotation_archive_changed'): coco.annotations(archive)

    def test_cached_images_verified_and_offline_never_fetches(self):
        candidate = self.candidate()
        with patch.object(coco, 'fetch', return_value=(self.payload, self.etag)) as fetch:
            local = coco.image_file(self.root, candidate, coco.MAX_TOTAL, False)
            self.assertEqual(coco.image_file(self.root, candidate, coco.MAX_TOTAL, True), local)
            self.assertEqual(fetch.call_count, 1)
        self.assertRegex(local['file'], r'^sources/f-[a-f0-9]{12}\.jpg$')
        (self.root / local['file']).write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'cached_image_changed'): coco.image_file(self.root, candidate, coco.MAX_TOTAL, True)
        with self.assertRaisesRegex(ValueError, 'invalid_download_receipt'): coco.image_file(self.root, candidate, 1, True)
        receipt = next((self.root/'provenance').glob('*.json'))
        receipt.write_bytes(b' ' * 16385)
        with self.assertRaisesRegex(ValueError, 'invalid_download_receipt'): coco.image_file(self.root, candidate, coco.MAX_TOTAL, True)

    def test_interrupted_publication_reuses_receipt_and_rejects_changed_publisher_bytes(self):
        candidate = self.candidate()
        with patch.object(coco, 'fetch', return_value=(self.payload, self.etag)), patch.object(coco, 'publish_bytes', side_effect=OSError('interrupted')):
            with self.assertRaises(OSError): coco.image_file(self.root, candidate, coco.MAX_TOTAL, False)
        self.assertEqual(len(list((self.root / 'provenance').glob('*.json'))), 1)
        with patch.object(coco, 'fetch', return_value=(b'changed', self.etag)), self.assertRaisesRegex(ValueError, 'publisher_image_changed'):
            coco.image_file(self.root, candidate, coco.MAX_TOTAL, False)
        with patch.object(coco, 'fetch', return_value=(self.payload, self.etag)):
            local = coco.image_file(self.root, candidate, coco.MAX_TOTAL, False)
        self.assertEqual((self.root / local['file']).read_bytes(), self.payload)

    def test_missing_offline_terms_images_and_frozen_evidence_fail_without_network(self):
        with patch.object(coco, 'annotations', return_value=(self.data, {})), patch.object(coco, 'fetch', side_effect=AssertionError('offline network forbidden')):
            with self.assertRaisesRegex(FileNotFoundError, 'missing_offline_terms'): coco.intake(self.root / 'unused.zip', self.root, 1, True)
            with self.assertRaisesRegex(FileNotFoundError, 'missing_offline_image'): coco.image_file(self.root, self.candidate(), coco.MAX_TOTAL, True)
            (self.root / 'freeze.json').write_text('retain')
            with self.assertRaises(FileExistsError): coco.intake(self.root / 'unused.zip', self.root, 1, True)

    def test_transport_content_type_etag_length_redirect_and_size_checks(self):
        def response(payload, content_type='image/jpeg', length=None, etag=None):
            stream = io.BytesIO(payload)
            stream.headers = Message()
            stream.headers['Content-Length'] = str(len(payload) if length is None else length)
            stream.headers['Content-Type'] = content_type
            stream.headers['ETag'] = self.etag if etag is None else etag
            return stream
        opener = unittest.mock.Mock()
        with patch.object(coco.urllib.request, 'build_opener', return_value=opener):
            opener.open.return_value = response(self.payload)
            self.assertEqual(coco.fetch(coco.IMAGE_BASE + 'example.jpg', 1000, True), (self.payload, self.etag))
            for stream in (response(self.payload, 'text/html'), response(self.payload, etag='"wrong"'), response(b'not-jpeg'), response(self.payload, length=1)):
                opener.open.return_value = stream
                with self.assertRaises(ValueError): coco.fetch(coco.IMAGE_BASE + 'example.jpg', 1000, True)
            opener.open.return_value = response(self.payload, length=1001)
            with self.assertRaisesRegex(ValueError, 'upstream_size_budget'): coco.fetch(coco.IMAGE_BASE + 'example.jpg', 1000, True)
        with self.assertRaisesRegex(ValueError, 'unexpected_upstream_redirect'):
            coco.NoRedirect().redirect_request(None, None, 302, '', {}, 'https://other.test/image.jpg')

    def test_intake_keeps_unopened_state_rights_groups_and_labels_unapproved(self):
        def download(url, *args, **kwargs): return (self.terms, '') if url == coco.TERMS_URL else (self.payload, self.etag)
        with patch.object(coco, 'annotations', return_value=(self.data, dict(captions='a'*64, instances='b'*64))), patch.object(coco, 'TERMS_BLOB', self.terms_blob), patch.object(coco, 'fetch', side_effect=download):
            coco.intake(self.root / 'unused.zip', self.root, 3)
        manifest = json.loads((self.root / 'intake.json').read_text())
        self.assertEqual(manifest['purpose'], 'unreviewed candidate-media-pool')
        self.assertIn('no predictions', manifest['intendedUse'])
        self.assertEqual(len(manifest['items']), 3)
        for item in manifest['items']:
            self.assertEqual(item['review'], dict(content=False, license=False, relatedGroups=False, split=None, labelsApproved=False))
            self.assertIsNone(item['license']['author'])
            self.assertEqual(item['license']['name'], 'CC BY 2.0')
            self.assertEqual(len(item['humanAnnotations']['captions']), 5)
        self.assertFalse((self.root / 'dataset.json').exists())
        self.assertFalse((self.root / 'freeze.json').exists())
        with self.assertRaises(FileExistsError): coco.intake(self.root / 'unused.zip', self.root, 3)

    def test_photo_redirects_keep_https_host_photo_identity_and_exact_lookup_query(self):
        redirect = coco.PhotoRedirect('123')
        request = urllib.request.Request('https://www.flickr.com/photo.gne?id=123', method='HEAD')
        for url in ('https://www.flickr.com/photo.gne?rb=1&id=123', 'https://www.flickr.com/photos/author/123/'):
            self.assertEqual(redirect.redirect_request(request, None, 302, '', {}, url).get_method(), 'HEAD')
        for url in ('http://www.flickr.com/photos/author/123/', 'https://other.test/photos/author/123/',
                    'https://www.flickr.com/photos/author/456/', 'https://www.flickr.com/photo.gne?rb=1&id=123&id=456',
                    'https://user:password@www.flickr.com/photos/author/123/', 'https://www.flickr.com/photos///'):
            with self.subTest(url=url), self.assertRaises(coco.UnresolvedPhoto): redirect.redirect_request(request, None, 302, '', {}, url)

    def test_current_by_conflicting_and_null_license_urls_remain_distinct_metadata(self):
        original = dict(type='photo', author_name='Author', author_url='https://www.flickr.com/photos/author/', license='CC BY 2.0', license_id='4', license_url='https://creativecommons.org/licenses/by/2.0/')
        opener = unittest.mock.Mock()
        with patch.object(coco.urllib.request, 'build_opener', return_value=opener):
            for license_name, license_id, license_url, expected in (('CC BY 2.0', '4', original['license_url'], 'current_by_metadata_matches'),
                    ('CC BY-ND 2.0', '6', 'https://creativecommons.org/licenses/by-nd/2.0/', 'license_conflict'),
                    ('All Rights Reserved', '0', None, 'license_conflict')):
                data = dict(original, license=license_name, license_id=license_id, license_url=license_url)
                head = io.BytesIO();head.geturl = lambda: 'https://www.flickr.com/photos/author/123/'
                raw = json.dumps(data).encode()
                opener.open.side_effect = [head, io.BytesIO(raw)]
                result = coco.attribution_lookup('123')
                self.assertEqual(result['status'], expected)
                self.assertEqual(result['licenseId'], int(license_id))
                self.assertEqual(result['responseSha256'], hashlib.sha256(raw).hexdigest())

    def test_unavailable_routes_recorded_but_rate_limits_authorization_and_server_errors_stop(self):
        opener = unittest.mock.Mock()
        with patch.object(coco.urllib.request, 'build_opener', return_value=opener):
            opener.open.side_effect = coco.UnresolvedPhoto('unexpected_canonical_route')
            self.assertEqual(coco.attribution_lookup('123')['status'], 'original_unresolved')
            for code in (404, 410, 403, 429, 503):
                opener.open.side_effect = urllib.error.HTTPError('https://www.flickr.com/', code, 'unavailable', {}, None)
                if code in (404, 410):
                    self.assertEqual(coco.attribution_lookup('123'), dict(status='original_unavailable', httpStatus=code))
                else:
                    with self.assertRaises(urllib.error.HTTPError): coco.attribution_lookup('123')

    def test_attribution_response_budget_and_invalid_author_are_unapproved(self):
        opener = unittest.mock.Mock()
        with patch.object(coco.urllib.request, 'build_opener', return_value=opener):
            head = io.BytesIO();head.geturl = lambda: 'https://www.flickr.com/photos/author/123/'
            opener.open.side_effect = [head, io.BytesIO(b'x'*65537)]
            with self.assertRaisesRegex(ValueError, 'attribution_response_budget'): coco.attribution_lookup('123')
            head = io.BytesIO();head.geturl = lambda: 'https://www.flickr.com/photos/author/123/'
            data = dict(type='photo', author_name='Author', author_url='https://other.test/photos/author/', license='CC BY 2.0', license_id='4', license_url='https://creativecommons.org/licenses/by/2.0/')
            opener.open.side_effect = [head, io.BytesIO(json.dumps(data).encode())]
            self.assertEqual(coco.attribution_lookup('123')['status'], 'original_unresolved')

    def test_attribution_receipts_resume_offline_without_approval_or_silent_source_changes(self):
        def download(url, *args, **kwargs): return (self.terms, '') if url == coco.TERMS_URL else (self.payload, self.etag)
        with patch.object(coco, 'annotations', return_value=(self.data, {})), patch.object(coco, 'TERMS_BLOB', self.terms_blob), patch.object(coco, 'fetch', side_effect=download):
            coco.intake(self.root/'unused.zip', self.root, 1)
        with patch.object(coco, 'attribution_lookup', return_value=dict(status='license_conflict')), patch.object(coco.time, 'sleep'):
            coco.capture_attribution(self.root)
        manifest = json.loads((self.root/'intake.json').read_text())
        item = manifest['items'][0]
        receipt_path = self.root/'attribution'/(item['id']+'.json')
        receipt = json.loads(receipt_path.read_text())
        self.assertFalse(receipt['approvedForHoldout'])
        # Deliberately remove only this test's summary to exercise checkpoint replay.
        (self.root/'attribution.json').unlink()
        with patch.object(coco, 'attribution_lookup', side_effect=AssertionError('offline network forbidden')):
            coco.capture_attribution(self.root, True)
            (self.root/'attribution.json').unlink()
            receipt['approvedForHoldout'] = True
            receipt_path.write_text(json.dumps(receipt))
            with self.assertRaisesRegex(ValueError, 'invalid_attribution_receipt'): coco.capture_attribution(self.root, True)
            receipt['approvedForHoldout'] = False
            receipt_path.write_text(json.dumps(receipt))
            (self.root/item['local']['file']).write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'candidate_source_changed'): coco.capture_attribution(self.root, True)


if __name__ == '__main__': unittest.main()
