"""Offline checks for source/label provenance and constrained diagnostic intake."""
import csv
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import wave

spec = importlib.util.spec_from_file_location('esc10', Path(__file__).with_name('prepare-esc10-evaluation.py'))
esc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(esc)


class Esc10Tests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.rows, self.notices = [], ['The ESC-10 subset is licensed as a Creative Commons Attribution 3.0 Unported']
        for target, category in enumerate(esc.QUERIES):
            for fold in range(1, 6):
                for variant in range(2):
                    original = str(10000 + target * 100 + fold * 10 + variant)
                    self.rows.append(dict(filename=f'{fold}-{original}-A-{target}.wav', fold=str(fold), target=str(target), category=category, esc10='True', src_file=original, take='A'))
                    self.notices.append(f'- [{fold}-{original}-A.ogg]: clip derived from field recording (http://www.freesound.org/people/author/sounds/{original}/) by author [CC0]')
        for i in range(1900):
            self.rows.append(dict(filename='unused', fold='1', target='49', category='other', esc10='False', src_file=str(i), take='A'))
        self.license = '\n'.join(self.notices)

    def csv(self):
        stream = io.StringIO()
        writer = csv.DictWriter(stream, fieldnames=self.rows[0].keys())
        writer.writeheader()
        writer.writerows(self.rows)
        return stream.getvalue()

    def audio(self):
        stream = io.BytesIO()
        with wave.open(stream, 'wb') as file:
            file.setparams((1, 2, 44100, 0, 'NONE', 'not compressed'))
            file.writeframes(b'\0' * 441000)
        return stream.getvalue()

    def tree(self, blobs):
        entries = sorted(blobs, key=lambda item: item['path'])
        raw = b''.join(b'100644 ' + item['path'].split('/')[-1].encode() + b'\0' + bytes.fromhex(item['sha']) for item in entries)
        audio_hash = hashlib.sha1(b'tree ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
        raw = b'40000 audio\0' + bytes.fromhex(audio_hash)
        root_hash = hashlib.sha1(b'tree ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
        return dict(sha=root_hash, truncated=False, tree=[dict(path='audio', type='tree', mode='040000', sha=audio_hash)] + [dict(item, mode='100644') for item in blobs])

    def test_selection_is_class_balanced_distinct_and_prediction_free(self):
        chosen = esc.select(self.csv(), self.license, 2)
        self.assertEqual(len(chosen), 20)
        self.assertEqual(len({row['src_file'] for row in chosen}), 20)
        for category in esc.QUERIES:
            self.assertEqual([row['fold'] for row in chosen if row['category'] == category], ['1', '2'])
        self.assertEqual(chosen, esc.select(self.csv(), self.license, 2))

    def test_subset_license_restricted_clips_and_missing_attribution_fail_closed(self):
        with self.assertRaisesRegex(ValueError, 'missing_subset_license'):
            esc.select(self.csv(), '', 2)
        for suffix in ('[CC-BY-NC]', '[Sampling+]', ''):
            with self.subTest(suffix=suffix), self.assertRaisesRegex(ValueError, 'no_unique_eligible'):
                esc.select(self.csv(), self.license.replace('[CC0]', suffix), 2)
        eligible = self.license.replace('[CC0]', '[CC-BY]')
        self.assertEqual(len(esc.select(self.csv(), eligible, 2)), 20)

    def test_identity_paths_category_and_selection_bounds_rejected(self):
        for count in (0, 6, True):
            with self.assertRaises(ValueError): esc.select(self.csv(), self.license, count)
        self.rows[0]['filename'] = '../../source.wav'
        with self.assertRaisesRegex(ValueError, 'invalid_upstream_identity'):
            esc.select(self.csv(), self.license, 2)
        self.rows[0]['category'] = 'invented_label'
        with self.assertRaisesRegex(ValueError, 'unexpected_upstream_categories'):
            esc.select(self.csv(), self.license, 2)

    def test_git_hash_cached_corruption_offline_and_path_protection(self):
        payload = b'original'
        expected = (esc.git_blob(payload), len(payload))
        with patch.object(esc, 'fetch', return_value=payload) as fetch:
            self.assertEqual(esc.cached(self.root, 'sources/f.wav', esc.BASE + 'audio/a.wav', expected), payload)
            self.assertEqual(esc.cached(self.root, 'sources/f.wav', esc.BASE + 'audio/a.wav', expected, True), payload)
            self.assertEqual(fetch.call_count, 1)
        (self.root / 'sources/f.wav').write_bytes(b'changed!')
        with self.assertRaisesRegex(ValueError, 'upstream_blob_changed'):
            esc.cached(self.root, 'sources/f.wav', '', expected, True)
        with self.assertRaises(FileNotFoundError): esc.cached(self.root, 'missing', '', offline=True)
        with self.assertRaisesRegex(ValueError, 'intake_path_escape'): esc.cached(self.root, '../outside', '')
        self.assertEqual((self.root / 'sources/f.wav').read_bytes(), b'changed!')

    def test_bad_download_not_published_and_existing_evidence_not_replaced(self):
        with patch.object(esc, 'fetch', return_value=b'wrong'), self.assertRaisesRegex(ValueError, 'upstream_blob_changed'):
            esc.cached(self.root, 'source.wav', '', (esc.git_blob(b'right'), 5))
        self.assertFalse((self.root / 'source.wav').exists())
        (self.root / 'freeze.json').write_text('keep')
        with patch.object(esc, 'fetch', side_effect=AssertionError('no network')), self.assertRaises(FileExistsError):
            esc.prepare(self.root)

    def test_wave_structure_and_truncated_pcm_rejected(self):
        payload = self.audio()
        esc.validate_wave(payload)
        with self.assertRaisesRegex(ValueError, 'truncated_clip'): esc.validate_wave(payload[:-2])
        other = io.BytesIO()
        with wave.open(other, 'wb') as file:
            file.setparams((2, 2, 44100, 0, 'NONE', 'not compressed'))
            file.writeframes(b'\0' * 441000)
        with self.assertRaisesRegex(ValueError, 'unexpected_clip_format'): esc.validate_wave(other.getvalue())

    def test_redirects_and_transport_budget_rejected(self):
        with self.assertRaisesRegex(ValueError, 'unexpected_upstream_redirect'):
            esc.NoRedirect().redirect_request(None, None, 302, '', {}, 'https://other.test/audio.wav')
        opener = unittest.mock.Mock()
        opener.open.return_value = io.BytesIO(b'x' * (esc.MAX_FILE + 1))
        with patch.object(esc.urllib.request, 'build_opener', return_value=opener), self.assertRaisesRegex(ValueError, 'upstream_size_budget'):
            esc.fetch(esc.BASE + 'LICENSE')

    def test_tree_membership_rejects_modified_omitted_and_duplicate_entries(self):
        tree = self.tree([dict(path='audio/example.wav', type='blob', sha=esc.git_blob(b'original'), size=8)])
        with patch.object(esc, 'TREE', tree['sha']):
            self.assertIn('audio/example.wav', esc.verified_tree(tree))
            tree['tree'][1]['sha'] = esc.git_blob(b'changed!')
            with self.assertRaisesRegex(ValueError, 'tree_checksum_mismatch'): esc.verified_tree(tree)
            tree['tree'].pop()
            with self.assertRaisesRegex(ValueError, 'tree_checksum_mismatch'): esc.verified_tree(tree)
            tree['tree'].append(tree['tree'][0])
            with self.assertRaisesRegex(ValueError, 'invalid_tree_entry'): esc.verified_tree(tree)

    def test_prepared_dataset_keeps_all_known_class_matches_and_no_negative_or_event_time_claims(self):
        metadata = {'LICENSE': self.license.encode(), 'README.md': b'upstream readme', 'meta/esc50.csv': self.csv().encode()}
        pins = {name: (esc.git_blob(payload), len(payload)) for name, payload in metadata.items()}
        payload = self.audio()
        chosen = esc.select(self.csv(), self.license, 2)
        blobs = [dict(path='audio/' + row['filename'], type='blob', sha=esc.git_blob(payload), size=len(payload)) for row in chosen]
        tree_data = self.tree(blobs)
        tree = json.dumps(tree_data).encode()
        def upstream(url):
            if url.startswith('https://api.github.com/'): return tree
            name = url.removeprefix(esc.BASE)
            return metadata[name] if name in metadata else payload
        with patch.object(esc, 'TREE', tree_data['sha']), patch.object(esc, 'METADATA', pins), patch.object(esc, 'fetch', side_effect=upstream):
            esc.prepare(self.root)
        data = json.loads((self.root / 'dataset.json').read_text())
        provenance = json.loads((self.root / 'provenance.json').read_text())
        self.assertEqual(data['purpose'], 'development-pilot')
        self.assertEqual(len(data['sources']), 20)
        self.assertEqual(len(data['tasks']), 10)
        self.assertTrue(all(task['kind'] == 'ambiguous' and len(task['expected']) == 2 for task in data['tasks']))
        self.assertTrue(all(task['split'] == 'development' for task in data['tasks']))
        self.assertEqual(provenance['revision'], esc.REVISION)
        self.assertEqual(provenance['tree'], tree_data['sha'])
        for source in data['sources']:
            self.assertRegex(source['file'], r'^sources/f-[a-f0-9]{12}\.wav$')
            self.assertEqual(source['sha256'], hashlib.sha256(payload).hexdigest())
            self.assertIn('Whole five-second clip only', source['grounding'])
        self.assertTrue(all(expected['locator'] == dict(kind='time', startSeconds=0, endSeconds=5) for task in data['tasks'] for expected in task['expected']))


if __name__ == '__main__': unittest.main()
