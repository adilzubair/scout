"""Offline contracts for source provenance, bounds, retries and interrupted intake."""
import copy
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

spec=importlib.util.spec_from_file_location('media_intake',Path(__file__).with_name('multimodal-media-intake.py'))
media=importlib.util.module_from_spec(spec);spec.loader.exec_module(media)

class IntakeTests(unittest.TestCase):
    def setUp(self):
        self.directory=tempfile.TemporaryDirectory();self.addCleanup(self.directory.cleanup)
        self.root=Path(self.directory.name);self.payload=b'fixture bytes for transport checks only'
        self.item=dict(id='commons-12',family='images',eligibleForIntake=True,
            original=dict(url='https://upload.wikimedia.org/example.jpg',bytes=len(self.payload),sha1=hashlib.sha1(self.payload).hexdigest()),
            license=dict(name='CC0'),review=dict(content=False,license=False,relatedGroups=False,split=None))

    def manifest(self,items=None):
        path=self.root/'discovery.json'
        path.write_text(json.dumps(dict(schemaVersion=1,purpose='unreviewed candidate-media-pool',items=items or [self.item])),encoding='utf-8')
        return path

    def test_host_credentials_port_and_redirect_rejected(self):
        for url in ('http://upload.wikimedia.org/a.jpg','https://upload.wikimedia.org.evil.test/a.jpg',
                    'https://user:secret@upload.wikimedia.org/a.jpg','https://upload.wikimedia.org:80/a.jpg',
                    'https://localhost/a.jpg','file:///C:/private.jpg'):
            with self.subTest(url=url),self.assertRaises(ValueError):media.safe_url(url)
        request=urllib.request.Request('https://upload.wikimedia.org/a.jpg')
        with self.assertRaises(ValueError):media.Redirects().redirect_request(request,None,302,'',{},'https://example.com/private')

    def test_download_proves_original_checksum_and_preserves_unreviewed_state(self):
        with patch.object(media,'open_response',return_value=io.BytesIO(self.payload)):
            local=media.download(self.item,self.root)
        self.assertEqual(local['sha256'],hashlib.sha256(self.payload).hexdigest())
        self.assertEqual((self.root/local['file']).read_bytes(),self.payload)
        self.assertRegex(local['file'],r'^sources/f-[0-9a-f]{12}\.jpg$')
        self.assertFalse(self.item['review']['content'])
        with patch.object(media,'open_response',side_effect=AssertionError('must reuse verified source')):
            self.assertEqual(media.download(self.item,self.root),local)

    def test_bad_length_hash_and_budget_remove_only_owned_partial(self):
        unrelated=self.root/'keep.part';unrelated.write_bytes(b'preserve')
        for payload in (self.payload[:-1],b'x'*len(self.payload),self.payload+b'excess'):
            with self.subTest(payload=payload),patch.object(media,'open_response',return_value=io.BytesIO(payload)),self.assertRaises(ValueError):
                media.download(self.item,self.root)
            self.assertEqual(list((self.root/'sources').iterdir()),[])
            self.assertEqual(unrelated.read_bytes(),b'preserve')

    def test_changed_existing_source_is_never_overwritten(self):
        with patch.object(media,'open_response',return_value=io.BytesIO(self.payload)):
            local=media.download(self.item,self.root)
        target=self.root/local['file'];target.write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError,'existing_source_changed'):media.download(self.item,self.root)
        self.assertEqual(target.read_bytes(),b'changed')

    def test_license_container_size_checksum_and_identity_rechecked(self):
        mutations=[('license','name','GFDL 1.2'),('original','bytes',media.MAX_FILE+1),
                   ('original','bytes',True),('original','sha1','unknown'),('original','url','https://upload.wikimedia.org/a.exe')]
        for section,key,value in mutations:
            item=copy.deepcopy(self.item);item[section][key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):media.download(item,self.root)
        for key,value in [('id','../../escape'),('family','unknown'),('eligibleForIntake',False)]:
            item=copy.deepcopy(self.item);item[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):media.download(item,self.root)

    def test_path_escape_and_link_denied(self):
        with self.assertRaises(ValueError):media.owned(self.root,self.root/'..'/'outside')
        with self.assertRaises(ValueError):media.owned(self.root,self.root)
        # Link detection is exercised without requiring Windows symlink privileges.
        with patch.object(Path,'is_symlink',return_value=True),self.assertRaises(ValueError):media.owned(self.root,self.root/'source')

    def test_single_owner_lock_releases_after_exception(self):
        with self.assertRaisesRegex(RuntimeError,'intentional'):
            with media.owner_lock(self.root,'.lock'):
                with self.assertRaisesRegex(RuntimeError,'intake_owner_busy'):
                    with media.owner_lock(self.root,'.lock'):self.fail('second owner entered')
                raise RuntimeError('intentional')
        with media.owner_lock(self.root,'.lock'):pass

    def test_evidence_publication_is_atomic_and_never_overwritten(self):
        path=self.root/'evidence.json';media.exclusive_json(path,{'first':True})
        with self.assertRaises(FileExistsError):media.exclusive_json(path,{'first':False})
        self.assertEqual(json.loads(path.read_text()),{'first':True})
        self.assertEqual([p.name for p in self.root.iterdir()],['evidence.json'])

    def test_rate_limit_honors_retry_after_and_caps_attempts(self):
        request=urllib.request.Request(media.API)
        error=lambda delay:urllib.error.HTTPError(media.API,429,'rate limited',{'Retry-After':delay},io.BytesIO())
        opener=unittest.mock.Mock();opener.open.side_effect=[error('2'),io.BytesIO(b'ok')]
        with patch.object(media.urllib.request,'build_opener',return_value=opener),patch.object(media.time,'sleep') as sleep:
            with media.open_response(request) as stream:self.assertEqual(stream.read(),b'ok')
            sleep.assert_called_once_with(2)
        opener.open.side_effect=[error('0'),error('0'),error('0')]
        with patch.object(media.urllib.request,'build_opener',return_value=opener),patch.object(media.time,'sleep') as sleep,self.assertRaises(urllib.error.HTTPError):
            media.open_response(request)
        self.assertEqual(sleep.call_count,2)
        opener.open.side_effect=[error('3600')]
        with patch.object(media.urllib.request,'build_opener',return_value=opener),patch.object(media.time,'sleep') as sleep,self.assertRaises(urllib.error.HTTPError):media.open_response(request)
        sleep.assert_not_called()

    def test_interrupted_batch_reuses_verified_sources_and_preserves_provenance(self):
        second=copy.deepcopy(self.item);second['id']='commons-13'
        discovery=self.manifest([self.item,second]);root=self.root/'batch'
        with patch.object(media,'open_response',side_effect=[io.BytesIO(self.payload),OSError('interrupted')]),patch.object(media.time,'sleep'),self.assertRaises(OSError):
            media.intake(discovery,root,['commons-12','commons-13'])
        self.assertFalse((root/'intake.json').exists())
        with patch.object(media,'open_response',return_value=io.BytesIO(self.payload)) as fetch,patch.object(media.time,'sleep'):
            media.intake(discovery,root,['commons-12','commons-13'])
        self.assertEqual(fetch.call_count,1)
        result=json.loads((root/'intake.json').read_text())
        self.assertEqual(result['discoverySha256'],media.digest(discovery))
        self.assertEqual(result['items'][0]['review'],self.item['review'])
        with self.assertRaises(FileExistsError):media.intake(discovery,root,['commons-12'])

    def test_invalid_selections_do_not_start_download(self):
        discovery=self.manifest()
        for identifiers in ([],['commons-99'],['commons-12','commons-12']):
            with self.subTest(ids=identifiers),self.assertRaises(ValueError):media.intake(discovery,self.root/'batch',identifiers)

    def test_existing_only_never_attempts_network_even_for_missing_file(self):
        discovery=self.manifest();root=self.root/'batch';root.mkdir()
        with patch.object(media,'open_response',side_effect=AssertionError('offline network access')):
            with self.assertRaises(FileNotFoundError):media.intake(discovery,root,['commons-12'],existing_only=True)
        with patch.object(media,'open_response',return_value=io.BytesIO(self.payload)):media.download(self.item,root)
        with patch.object(media,'open_response',side_effect=AssertionError('offline network access')):media.intake(discovery,root,['commons-12'],existing_only=True)
        self.assertEqual(len(json.loads((root/'intake.json').read_text())['items']),1)

    def test_discovery_license_filter_does_not_approve_content_or_rights(self):
        page=dict(pageid=1,title='File:sample.jpg',revisions=[{'revid':42}],imageinfo=[dict(
            url=self.item['original']['url'],size=len(self.payload),sha1=self.item['original']['sha1'],mime='image/jpeg',
            extmetadata=dict(LicenseShortName={'value':'CC BY-SA 4.0'},Artist={'value':'<a>Author</a>'}))])
        result=media.record(page,'images','topic')
        self.assertTrue(result['eligibleForIntake']);self.assertFalse(result['review']['license'])
        self.assertFalse(result['review']['content']);self.assertEqual(result['revision'],42)
        self.assertEqual(result['license']['artist'],'Author')
        page['imageinfo'][0]['extmetadata']['LicenseShortName']['value']='GFDL 1.2'
        self.assertFalse(media.record(page,'images','topic')['eligibleForIntake'])

    def test_discovery_resumes_only_completed_topics_and_rejects_changed_definition(self):
        definition=self.root/'queries.json';output=self.root/'pool.json'
        value=dict(schemaVersion=1,queries=[dict(id=name,family='images',query=name,limit=1) for name in ('one','two')])
        definition.write_text(json.dumps(value))
        with patch.object(media,'fetch_json',side_effect=[[],OSError('interrupted')]),patch.object(media.time,'sleep'),self.assertRaises(OSError):media.discover(definition,output)
        self.assertFalse(output.exists())
        checkpoint=output.with_suffix('.partial.json');self.assertEqual(json.loads(checkpoint.read_text())['completedTopics'],['one'])
        definition.write_text(json.dumps(dict(value,extra='changed')))
        with self.assertRaisesRegex(ValueError,'checkpoint_definition_changed'):media.discover(definition,output)
        definition.write_text(json.dumps(value))
        with patch.object(media,'fetch_json',return_value=[]) as fetch,patch.object(media.time,'sleep'):media.discover(definition,output)
        self.assertEqual(fetch.call_count,1);self.assertFalse(checkpoint.exists())
        with self.assertRaises(FileExistsError):media.discover(definition,output)

if __name__=='__main__':unittest.main()
