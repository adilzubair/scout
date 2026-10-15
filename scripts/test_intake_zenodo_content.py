"""Offline contracts for pinned, unopened Office-content candidate intake."""
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('zenodo_intake', Path(__file__).with_name('intake-zenodo-content.py'))
zenodo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(zenodo)


class ZenodoTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.metadata = self.root/'metadata';self.metadata.mkdir()
        self.output = self.root/'pool'
        self.payloads = {'report.docx': b'transport fixture docx', 'slides.pptx': b'transport fixture pptx', 'cells.xlsx': b'transport fixture xlsx'}
        self.record = dict(id=123, conceptrecid='100', doi='10.5281/zenodo.123', conceptdoi='10.5281/zenodo.100',
            metadata=dict(access_right='open', license=dict(id='cc-by-4.0'), title='Natural research deposit', creators=[dict(name='Example Author')]),
            files=[dict(key=name,size=len(payload),checksum='md5:'+hashlib.md5(payload).hexdigest(),links=dict(self='https://other.test/arbitrary')) for name,payload in self.payloads.items()])
        self.definition = self.root/'selection.json'
        self.write()

    def write(self):
        self.path = self.metadata/'123.json'
        self.path.write_text(json.dumps(self.record), encoding='utf-8')
        self.definition.write_text(json.dumps(dict(schemaVersion=1,records=[dict(id='123',metadataSha256=zenodo.media.digest(self.path),files=list(self.payloads))])),encoding='utf-8')

    def chosen(self): return zenodo.select(self.definition,self.metadata)[0]

    def opener(self, payload, declared=None):
        stream=io.BytesIO(payload);stream.headers={'Content-Length':str(len(payload) if declared is None else declared)}
        opener=unittest.mock.Mock();opener.open.return_value=stream
        return opener

    def test_selection_uses_pinned_bytes_opaque_paths_and_concept_groups(self):
        items,snapshots=zenodo.select(self.definition,self.metadata)
        self.assertEqual({item['family'] for item in items},{'documents','presentations','spreadsheets'})
        self.assertEqual({item['relatedGroupCandidate'] for item in items},{'zenodo-concept-100'})
        self.assertEqual(snapshots,[(self.path,'123.json')])
        for item in items:
            self.assertRegex(item['id'],r'^zenodo-123-[a-f0-9]{12}$')
            self.assertRegex(item['localFile'],r'^sources/f-[a-f0-9]{12}\.[a-z]+$')
            self.assertTrue(item['original']['url'].startswith('https://zenodo.org/api/records/123/files/'))
            self.assertFalse(item['review']['labelsApproved'])
            self.assertIsNone(item['review']['split'])

    def test_changed_snapshot_unpinned_record_and_duplicate_definition_rejected(self):
        self.path.write_text('{}')
        with self.assertRaisesRegex(ValueError,'publisher_metadata_changed'):zenodo.select(self.definition,self.metadata)
        self.write()
        definition=json.loads(self.definition.read_text());definition['records']*=2;self.definition.write_text(json.dumps(definition))
        with self.assertRaisesRegex(ValueError,'invalid_record_identity'):zenodo.select(self.definition,self.metadata)

    def test_restricted_access_other_license_missing_authors_and_group_rejected(self):
        original=copy.deepcopy(self.record)
        for field,value in [('access_right','restricted'),('license',dict(id='cc-by-nc-4.0')),('creators',[])]:
            self.record=copy.deepcopy(original);self.record['metadata'][field]=value;self.write()
            with self.subTest(field=field),self.assertRaises(ValueError):zenodo.select(self.definition,self.metadata)
        self.record=copy.deepcopy(original);self.record['conceptrecid']='../escape';self.write()
        with self.assertRaisesRegex(ValueError,'record_access_license_or_group'):zenodo.select(self.definition,self.metadata)

    def test_invalid_names_macro_files_size_checksums_and_batch_budget_rejected(self):
        original=copy.deepcopy(self.record)
        for field,value in [('size',True),('size',zenodo.MAX_FILE+1),('checksum','md5:unknown')]:
            self.record=copy.deepcopy(original);self.record['files'][0][field]=value;self.write()
            with self.subTest(field=field),self.assertRaisesRegex(ValueError,'source_size_or_checksum'):zenodo.select(self.definition,self.metadata)
        self.record=copy.deepcopy(original);self.write()
        for name in ('../escape.docx','macro.docm','C:\\private.docx','unknown.exe'):
            selection=json.loads(self.definition.read_text());selection['records'][0]['files']=[name];self.definition.write_text(json.dumps(selection))
            with self.subTest(name=name),self.assertRaisesRegex(ValueError,'unsupported_or_invalid'):zenodo.select(self.definition,self.metadata)
        self.write()
        with patch.object(zenodo,'MAX_TOTAL',1),self.assertRaisesRegex(ValueError,'batch_source_budget'):zenodo.select(self.definition,self.metadata)

    def test_download_checks_size_md5_and_reuses_only_verified_existing_bytes(self):
        self.output.mkdir();item=self.chosen()[0];payload=self.payloads[item['originalName']]
        with patch.object(zenodo.urllib.request,'build_opener',return_value=self.opener(payload)):
            local=zenodo.download(self.output,item,False)
        with patch.object(zenodo.urllib.request,'build_opener',side_effect=AssertionError('offline network forbidden')):
            self.assertEqual(zenodo.download(self.output,item,True),local)
        self.assertEqual(local['sha256'],hashlib.sha256(payload).hexdigest())
        (self.output/local['file']).write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError,'existing_source_changed'):zenodo.download(self.output,item,True)

    def test_bad_transfer_never_published_and_unrelated_file_preserved(self):
        self.output.mkdir();item=self.chosen()[0];payload=self.payloads[item['originalName']]
        keep=self.output/'keep.part';keep.write_bytes(b'keep')
        for incoming,declared in [(b'x'*len(payload),len(payload)),(payload[:-1],len(payload)),(payload+b'extra',len(payload)),(payload,1)]:
            with self.subTest(declared=declared),patch.object(zenodo.urllib.request,'build_opener',return_value=self.opener(incoming,declared)),self.assertRaises(ValueError):
                zenodo.download(self.output,item,False)
            self.assertFalse((self.output/item['localFile']).exists())
            self.assertEqual(list((self.output/'sources').iterdir()),[])
            self.assertEqual(keep.read_bytes(),b'keep')

    def test_offline_missing_sources_and_redirects_rejected_without_fallback(self):
        self.output.mkdir()
        with patch.object(zenodo.urllib.request,'build_opener',side_effect=AssertionError('offline network forbidden')),self.assertRaisesRegex(FileNotFoundError,'missing_offline_source'):
            zenodo.download(self.output,self.chosen()[0],True)
        with self.assertRaisesRegex(ValueError,'unexpected_zenodo_redirect'):
            zenodo.NoRedirect().redirect_request(None,None,302,'',{},'https://other.test/source.docx')

    def test_slow_transfer_still_has_a_finite_deadline_and_cleans_its_partial(self):
        self.output.mkdir();item=self.chosen()[0];payload=self.payloads[item['originalName']]
        with patch.object(zenodo.urllib.request,'build_opener',return_value=self.opener(payload)),patch.object(zenodo.time,'monotonic',side_effect=[0,zenodo.TRANSFER_SECONDS+1]),self.assertRaisesRegex(ValueError,'source_transfer_time_budget'):
            zenodo.download(self.output,item,False)
        self.assertEqual(list((self.output/'sources').iterdir()),[])

    def test_provenance_and_completed_or_frozen_evidence_never_overwritten(self):
        self.output.mkdir();zenodo.publish_bytes(self.output,'provenance/record.json',b'first')
        zenodo.publish_bytes(self.output,'provenance/record.json',b'first')
        with self.assertRaisesRegex(ValueError,'existing_provenance_changed'):zenodo.publish_bytes(self.output,'provenance/record.json',b'changed')
        with self.assertRaisesRegex(ValueError,'intake_path_escape'):zenodo.publish_bytes(self.output,'../outside',b'changed')
        (self.output/'freeze.json').write_text('preserve')
        with self.assertRaises(FileExistsError):zenodo.intake(self.definition,self.metadata,self.output)

    def test_all_formats_remain_unopened_and_original_snapshot_bytes_preserved(self):
        self.output.mkdir()
        for item in self.chosen():
            target=self.output/item['localFile'];target.parent.mkdir(exist_ok=True);target.write_bytes(self.payloads[item['originalName']])
        with patch.object(zenodo.urllib.request,'build_opener',side_effect=AssertionError('offline network forbidden')):
            zenodo.intake(self.definition,self.metadata,self.output,True)
        data=json.loads((self.output/'intake.json').read_text())
        self.assertEqual(data['purpose'],'unreviewed candidate-content-pool')
        self.assertEqual(len(data['items']),3)
        self.assertEqual((self.output/'provenance/123.json').read_bytes(),self.path.read_bytes())
        self.assertEqual((self.output/'provenance/selection.json').read_bytes(),self.definition.read_bytes())
        self.assertTrue(all(item['review']==dict(content=False,license=False,relatedGroups=False,split=None,labelsApproved=False) for item in data['items']))
        self.assertFalse((self.output/'dataset.json').exists())
        with self.assertRaises(FileExistsError):zenodo.intake(self.definition,self.metadata,self.output,True)


if __name__=='__main__':unittest.main()
