"""Notice retention, integrity and filesystem-boundary contracts without inference."""
import base64
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from semantic_search import notices

ROOT=Path(__file__).resolve().parents[1]/'artifacts/diagnostics/multimodal/notice-contracts'

class NoticesTests(unittest.TestCase):
    def setUp(self):
        ROOT.mkdir(parents=True,exist_ok=True);self.folder=tempfile.TemporaryDirectory(dir=ROOT);self.addCleanup(self.folder.cleanup)
        self.root=Path(self.folder.name);self.generation=self.root/'generation';self.site=self.generation/'venv/Lib/site-packages';self.site.mkdir(parents=True)
        self.home=self.root/'python';self.home.mkdir();(self.home/'LICENSE.txt').write_text('Interpreter fixture license')
        (self.root/'semantic_search').mkdir();self.policy=self.root/'semantic_search/distribution.json';self.policy.write_text('{}')
        self.prefix='example-1.0.dist-info';self.package=self.site/self.prefix;self.package.mkdir()
        self.write_package()
        self.addCleanup(patch.stopall)
        patch.object(notices,'POLICY',self.policy).start()
        patch.object(notices,'native_audit',return_value=dict(distributionApproved=False,reviewFindings=['source_review_required'])).start()
    def write_package(self,*,header='LICENSE.txt',windows=False,license_text='Fixture license'):
        metadata='Metadata-Version: 2.4\nName: example\nVersion: 1.0\nLicense-Expression: MIT\nLicense-File: '+header+'\n'
        (self.package/'METADATA').write_text(metadata,encoding='utf-8')
        (self.package/'LICENSE.txt').write_text(license_text,encoding='utf-8')
        records=[]
        for name in ('METADATA','LICENSE.txt'):
            path=self.package/name;payload=path.read_bytes();relative=self.prefix+('\\' if windows else '/')+name
            records.append(relative+',sha256='+base64.urlsafe_b64encode(hashlib.sha256(payload).digest()).decode().rstrip('=')+','+str(len(payload)))
        records.append(self.prefix+'/RECORD,,');(self.package/'RECORD').write_text('\n'.join(records),encoding='utf-8')
    def collect(self):return notices.collect(self.generation,self.site,self.home)
    def pointer(self):return json.loads((self.generation/'notices/current.json').read_text())
    def test_exact_text_and_hashes_are_retained_without_approving_distribution(self):
        result=self.collect();pointer=self.pointer();manifest=self.generation/'notices'/pointer['manifest']
        self.assertEqual(notices.sha(manifest.read_bytes()),pointer['sha256']);self.assertFalse(result['distributionApproved'])
        self.assertFalse(result['native']['distributionApproved']);self.assertEqual(result['reviewFindings'],[])
        for item in result['packages'][0]['notices']:
            path=manifest.parent/item['file'];self.assertEqual(len(path.read_bytes()),item['bytes']);self.assertEqual(notices.sha(path.read_bytes()),item['sha256'])
    def test_windows_record_separators_preserve_real_license_files(self):
        self.write_package(windows=True);self.assertEqual(len(self.collect()['packages'][0]['notices']),2)
    def test_declared_parent_escape_and_links_are_rejected(self):
        self.write_package(header='..\\..\\outside.txt')
        with self.assertRaisesRegex(ValueError,'invalid_notice_path'):self.collect()
        with patch.object(Path,'is_symlink',return_value=True),self.assertRaises(ValueError):notices.owned(self.generation,'notices/data')
        for relative in ('../private','C:/private','/private','name:stream','a\0b'):
            with self.subTest(relative=relative),self.assertRaises(ValueError):notices.owned(self.generation,relative)
    def test_changed_license_keeps_prior_published_manifest(self):
        self.collect();before=self.pointer();(self.package/'LICENSE.txt').write_text('changed')
        with self.assertRaisesRegex(ValueError,'notice_record_hash_mismatch'):self.collect()
        self.assertEqual(self.pointer(),before)
    def test_file_and_total_budgets_fail_before_pointer_publication(self):
        for key in ('MAX_FILE','MAX_TOTAL'):
            with patch.object(notices,key,1),self.assertRaises(ValueError):self.collect()
            self.assertFalse((self.generation/'notices/current.json').exists())
    def test_incomplete_license_metadata_is_reported_and_never_approved(self):
        self.write_package(header='MISSING.txt');result=self.collect()
        self.assertIn('declared_notice_missing:example',result['reviewFindings']);self.assertFalse(result['distributionApproved'])
    def test_supplemental_license_is_versioned_and_hash_checked(self):
        source=self.root/'source';source.mkdir();(source/'LICENSE').write_bytes(b'Upstream fixture license')
        self.policy.write_text(json.dumps(dict(supplementalNotices={'example':dict(version='1.0',file='source/LICENSE',sha256=notices.sha(b'Upstream fixture license'),source='https://example.test/pinned/LICENSE')})))
        self.assertEqual(len(self.collect()['packages'][0]['notices']),3)
        before=self.pointer();policy=json.loads(self.policy.read_text());policy['supplementalNotices']['example']['version']='2.0';self.policy.write_text(json.dumps(policy))
        with self.assertRaisesRegex(ValueError,'supplemental_notice_version_mismatch'):self.collect()
        policy['supplementalNotices']['example']['version']='1.0';self.policy.write_text(json.dumps(policy))
        (source/'LICENSE').write_bytes(b'Changed upstream fixture')
        with self.assertRaisesRegex(ValueError,'supplemental_notice_hash_mismatch'):self.collect()
        self.assertEqual(self.pointer(),before)
    def test_another_environment_cannot_be_exported_as_this_generation(self):
        with self.assertRaisesRegex(ValueError,'notice_environment_outside_generation'):notices.collect(self.generation,self.root/'elsewhere',self.home)

if __name__=='__main__':unittest.main()
