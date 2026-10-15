"""Offline contracts for current Commons evidence, immutability and refusal to approve labels."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('commons_attribution',Path(__file__).with_name('capture-commons-attribution.py'))
capture=importlib.util.module_from_spec(spec);spec.loader.exec_module(capture)

class AttributionTests(unittest.TestCase):
    def setUp(self):
        test_root=Path('artifacts/diagnostics/multimodal/commons-attribution-contracts').resolve();test_root.mkdir(parents=True,exist_ok=True)
        self.directory=tempfile.TemporaryDirectory(dir=test_root);self.root=Path(self.directory.name).resolve()
        assert self.root.parent==test_root
        self.addCleanup(self.directory.cleanup)
        self.pool=self.root/'pool';(self.pool/'sources').mkdir(parents=True);self.intake=self.pool/'intake.json';self.output=self.root/'attribution'
        self.items=[];self.pages=[]
        for identity in (12,13):
            payload=f'transport fixture {identity}'.encode();name=f'sources/f-{identity:012x}.webm';(self.pool/name).write_bytes(payload)
            item=dict(id=f'commons-{identity}',pageId=identity,revision=40,family='video',
                local=dict(file=name,bytes=len(payload),sha256=hashlib.sha256(payload).hexdigest()),
                original=dict(bytes=len(payload),sha1=hashlib.sha1(payload).hexdigest()),license=dict(name='CC BY-SA 4.0',artist='Example Author'))
            self.items.append(item)
            self.pages.append(dict(pageid=identity,title=f'File:Example {identity}.webm',revisions=[dict(revid=42,timestamp='2026-10-15T00:00:00Z',
                slots=dict(main={'*':'{{Information|Source=Own work}}\n{{CC-BY-SA-4.0}}'}))],
                imageinfo=[dict(size=len(payload),sha1=item['original']['sha1'],extmetadata=dict(
                    LicenseShortName=dict(value='CC BY-SA 4.0'),Artist=dict(value='<a href="/wiki/User:Example">Example Author</a>'),
                    Credit=dict(value='Own work'),LicenseUrl=dict(value='https://creativecommons.org/licenses/by-sa/4.0/')))]))
        self.write_intake()
    def write_intake(self):self.intake.write_text(json.dumps(dict(schemaVersion=1,purpose='unreviewed candidate-media-pool',items=self.items)),encoding='utf-8')
    def run_capture(self):
        with patch.object(capture.media,'fetch_json',return_value=copy.deepcopy(self.pages)) as fetch:
            capture.capture(self.intake,self.output)
        return fetch
    def summary(self):return json.loads((self.output/'attribution.json').read_text())

    def test_one_bounded_api_batch_retains_publisher_revision_raw_fields_and_creator_urls(self):
        fetch=self.run_capture();self.assertEqual(fetch.call_count,1);parameters=fetch.call_args.args[0]
        self.assertEqual(parameters['pageids'],'12|13');self.assertEqual(parameters['rvslots'],'main')
        summary=self.summary();self.assertFalse(summary['approvedForHoldout']);self.assertFalse(summary['labelsApproved']);self.assertIsNone(summary['split'])
        self.assertEqual(summary['intakeSha256'],capture.media.digest(self.intake))
        for row in summary['items']:
            self.assertEqual(row['evidence']['status'],'metadata_match');self.assertTrue(row['evidence']['sourceWikitextPresent'])
            self.assertEqual(row['evidence']['artistUrls'],['https://commons.wikimedia.org/wiki/User:Example'])
            snapshot=json.loads((self.output/row['snapshot']).read_text());self.assertEqual(snapshot['page'],self.pages[int(row['id'][8:])-12])
            self.assertEqual(row['snapshotSha256'],capture.media.digest(self.output/row['snapshot']))

    def test_offline_verification_never_fetches_or_rewrites_published_evidence(self):
        self.run_capture();before={p.name:p.read_bytes() for p in self.output.glob('*.json')}
        with patch.object(capture.media,'fetch_json',side_effect=AssertionError('offline network forbidden')):
            capture.capture(self.intake,self.output,True)
        self.assertEqual(before,{p.name:p.read_bytes() for p in self.output.glob('*.json')})
        with self.assertRaises(FileExistsError):capture.capture(self.intake,self.output)

    def test_missing_offline_snapshot_stops_without_network(self):
        with patch.object(capture.media,'fetch_json',side_effect=AssertionError('offline network forbidden')),self.assertRaises(FileNotFoundError):
            capture.capture(self.intake,self.output,True)
        self.assertFalse((self.output/'attribution.json').exists())

    def test_changed_original_or_intake_prevents_capture(self):
        (self.pool/self.items[0]['local']['file']).write_bytes(b'changed')
        with patch.object(capture.media,'fetch_json',side_effect=AssertionError('unverified input fetched')),self.assertRaisesRegex(ValueError,'source_size_changed'):
            capture.capture(self.intake,self.output)

    def test_changed_snapshot_and_summary_cannot_pass_offline_verification(self):
        self.run_capture();path=self.output/'commons-12.json';original=path.read_bytes()
        snapshot=json.loads(original);snapshot['page']['imageinfo'][0]['extmetadata']['Credit']['value']='changed';path.write_text(json.dumps(snapshot))
        with self.assertRaisesRegex(ValueError,'snapshot_or_summary_changed'):capture.capture(self.intake,self.output,True)
        path.write_bytes(original);summary=self.summary();summary['approvedForHoldout']=True
        (self.output/'attribution.json').write_text(json.dumps(summary))
        with self.assertRaisesRegex(ValueError,'summary_changed'):capture.capture(self.intake,self.output,True)
        for invalid in ({},None):
            (self.output/'attribution.json').write_text(json.dumps(invalid))
            with patch.object(capture.media,'fetch_json',side_effect=AssertionError('changed summary fetched')),self.assertRaisesRegex(ValueError,'summary_changed'):
                capture.capture(self.intake,self.output,True)

    def test_duplicate_summary_or_source_mutation_during_capture_cannot_publish_approval(self):
        self.run_capture();summary=self.summary();summary['items'].append(copy.deepcopy(summary['items'][0]))
        (self.output/'attribution.json').write_text(json.dumps(summary))
        with self.assertRaisesRegex(ValueError,'summary_changed'):capture.capture(self.intake,self.output,True)
        fresh=self.root/'mutated'
        def mutate(parameters):
            (self.pool/self.items[0]['local']['file']).write_bytes(b'changed during capture')
            return copy.deepcopy(self.pages)
        with patch.object(capture.media,'fetch_json',side_effect=mutate),self.assertRaisesRegex(ValueError,'source_size_changed'):
            capture.capture(self.intake,fresh)
        self.assertFalse((fresh/'attribution.json').exists())

    def test_conflicts_restrictions_and_unavailable_page_stay_unapproved(self):
        for label,mutation,reason in (
            ('license',lambda p:p['imageinfo'][0]['extmetadata']['LicenseShortName'].update(value='CC BY-NC 4.0'),'license_label_differs'),
            ('file',lambda p:p['imageinfo'][0].update(sha1='0'*40),'publisher_original_changed'),
            ('artist',lambda p:p['imageinfo'][0]['extmetadata']['Artist'].update(value='Other Author'),'artist_missing_or_differs'),
            ('restricted',lambda p:p['imageinfo'][0]['extmetadata'].update(Restrictions=dict(value='trademark')),'restrictions_present')):
            page=copy.deepcopy(self.pages[0]);mutation(page);result=capture.summarize(self.items[0],page)
            with self.subTest(label=label):self.assertEqual(result['status'],'requires_review');self.assertIn(reason,result['reasons']);self.assertFalse(result['approvedForHoldout'])
        self.assertIn('publisher_page_or_file_unavailable',capture.summarize(self.items[0],None)['reasons'])
        self.assertIn('publisher_page_or_file_unavailable',capture.summarize(self.items[0],dict(imageinfo=[],revisions=[]))['reasons'])

    def test_unexpected_publisher_identity_or_api_error_never_publishes_summary(self):
        page=copy.deepcopy(self.pages[0]);page['pageid']=999
        with patch.object(capture.media,'fetch_json',return_value=[page]),self.assertRaisesRegex(ValueError,'unexpected_publisher_identity'):
            capture.capture(self.intake,self.output)
        self.assertFalse((self.output/'attribution.json').exists())
        with patch.object(capture.media,'fetch_json',side_effect=OSError('provider unavailable')),self.assertRaises(OSError):capture.capture(self.intake,self.output)
        self.assertFalse((self.output/'attribution.json').exists())

    def test_interrupted_multi_batch_capture_preserves_snapshots_and_resumes_only_missing_pages(self):
        for identity in range(14,23):
            item=copy.deepcopy(self.items[0]);item.update(id=f'commons-{identity}',pageId=identity)
            item['local']['file']=f'sources/f-{identity:012x}.webm'
            (self.pool/item['local']['file']).write_bytes((self.pool/self.items[0]['local']['file']).read_bytes())
            page=copy.deepcopy(self.pages[0]);page.update(pageid=identity,title=f'File:Example {identity}.webm')
            self.items.append(item);self.pages.append(page)
        self.write_intake()
        with patch.object(capture.media,'fetch_json',side_effect=[self.pages[:10],OSError('provider unavailable')]) as fetch,patch.object(capture.time,'sleep'),self.assertRaises(OSError):
            capture.capture(self.intake,self.output)
        self.assertEqual(len(list(self.output.glob('commons-*.json'))),10);self.assertFalse((self.output/'attribution.json').exists())
        with patch.object(capture.media,'fetch_json',return_value=[self.pages[-1]]) as fetch:
            capture.capture(self.intake,self.output)
        self.assertEqual(fetch.call_count,1);self.assertEqual(fetch.call_args.args[0]['pageids'],'22')
        self.assertEqual(self.summary()['sourceCount'],11)

    def test_identity_path_budget_and_unsafe_creator_links_are_bounded(self):
        self.items[0]['pageId']=True;self.write_intake()
        with self.assertRaisesRegex(ValueError,'invalid_commons_identity'):capture.selected(self.intake)
        self.items[0]['pageId']=12;self.items[0]['local']['file']='../outside.webm';self.write_intake()
        with self.assertRaisesRegex(ValueError,'invalid_local_source'):capture.selected(self.intake)
        self.items[0]['local']['file']='sources/f-00000000000c.webm';self.write_intake()
        with patch.object(capture.media,'MAX_TOTAL',1),self.assertRaisesRegex(ValueError,'source_batch_budget'):capture.selected(self.intake)
        links=capture.Links();links.feed('<a href="javascript:bad()">bad</a><a href="file:///private">local</a><a href="https://user:secret@example.com">credential</a><a href="//example.org/author">author</a>')
        self.assertEqual(links.urls,['https://example.org/author'])

if __name__=='__main__':unittest.main()
