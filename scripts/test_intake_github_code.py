"""Offline provenance/poisoning contracts for inert development-code intake."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('github_intake',Path(__file__).with_name('intake-github-code.py'))
code=importlib.util.module_from_spec(spec);spec.loader.exec_module(code)


def tree(rows):
    ordered=sorted(rows,key=lambda r:(r['path']+('/' if r['type']=='tree' else '')).encode())
    payload=b''.join(r['mode'].lstrip('0').encode()+b' '+r['path'].encode()+b'\0'+bytes.fromhex(r['sha']) for r in ordered)
    identity=hashlib.sha1(b'tree '+str(len(payload)).encode()+b'\0'+payload).hexdigest()
    return dict(sha=identity,truncated=False,tree=rows)


def blob(name,payload,mode='100644'):
    return dict(path=name,mode=mode,type='blob',sha=code.esc.git_blob(payload),size=len(payload))


class IntakeTests(unittest.TestCase):
    def setUp(self):
        self.directory=tempfile.TemporaryDirectory();self.addCleanup(self.directory.cleanup)
        self.root=Path(self.directory.name);self.metadata=self.root/'metadata';self.metadata.mkdir()
        self.output=self.root/'pool';self.definition=self.root/'selection.json'
        # A valid source must stay inert even though executing it would fail.
        self.source=b'raise AssertionError("intake executed downloaded code")\n'
        self.license=b'Fixture license notice, retained verbatim.\n'
        self.child=tree([blob('algorithm.py',self.source)])
        self.parent=tree([blob('LICENSE',self.license),dict(path='src',type='tree',mode='040000',sha=self.child['sha'])])
        self.commit=self.metadata/'example--project-commit.json'
        self.commit.write_text(json.dumps(dict(sha='a'*40,tree=dict(sha=self.parent['sha']))),encoding='utf-8')
        self.selection=dict(schemaVersion=1,intendedSplit='development',repositories=[dict(repo='example/project',revision='a'*40,rootTree=self.parent['sha'],commitMetadataSha256=code.media.digest(self.commit),files=['src/algorithm.py'],licenseFiles=['LICENSE'])])
        self.write()
        self.payloads={'/git/trees/'+self.parent['sha']:json.dumps(self.parent).encode(),'/git/trees/'+self.child['sha']:json.dumps(self.child).encode(),'/LICENSE':self.license,'/src/algorithm.py':self.source}

    def write(self):self.definition.write_text(json.dumps(self.selection),encoding='utf-8')

    def fetch(self,url):
        matches=[payload for suffix,payload in self.payloads.items() if url.endswith(suffix)]
        self.assertEqual(len(matches),1,url)
        self.assertTrue(url.startswith(('https://api.github.com/repos/example/project/git/trees/','https://raw.githubusercontent.com/example/project/'+'a'*40+'/')))
        return matches[0]

    def run_intake(self,offline=False):
        with patch.object(code.esc,'fetch',side_effect=AssertionError('offline network') if offline else self.fetch):
            code.intake(self.definition,self.metadata,self.output,offline)
        return json.loads((self.output/'intake.json').read_text())

    def test_nested_tree_download_inert_source_and_exact_offline_replay(self):
        result=self.run_intake();before=(self.output/'intake.json').read_bytes()
        self.assertEqual(self.run_intake(True),result);self.assertEqual((self.output/'intake.json').read_bytes(),before)
        item=result['items'][0]
        self.assertEqual((self.output/item['localFile']).read_bytes(),self.source)
        self.assertRegex(item['localFile'],r'^sources/f-[a-f0-9]{12}\.py$')
        self.assertEqual((self.output/item['licenses'][0]['file']).read_bytes(),self.license)
        self.assertEqual((self.output/'provenance/selection.json').read_bytes(),self.definition.read_bytes())
        self.assertFalse(result['labelsApproved']);self.assertEqual(result['embeddingInference'],'not_run')
        self.assertFalse(any(item['review'].values()));self.assertEqual(item['intendedSplit'],'development')
        self.assertEqual(len(result['treeSnapshots']),2)

    def test_tree_membership_tampering_truncation_duplicate_path_and_mode_rejected(self):
        for mutation in ('sha','truncated','duplicate','mode'):
            altered=copy.deepcopy(self.parent)
            if mutation=='sha':altered['tree'][0]['sha']='b'*40
            if mutation=='truncated':altered['truncated']=True
            if mutation=='duplicate':altered['tree'].append(altered['tree'][0])
            if mutation=='mode':altered['tree'][0]['mode']='100666'
            with self.subTest(mutation=mutation),self.assertRaises(ValueError):code.verified_tree(altered,self.parent['sha'])

    def test_git_directory_sorting_and_unselected_links_are_hash_verified(self):
        fixture=tree([dict(path='src',type='tree',mode='040000',sha='a'*40),blob('src.py',b'x'),blob('shortcut',b'src',mode='120000'),dict(path='module',type='commit',mode='160000',sha='c'*40)])
        self.assertEqual(len(code.verified_tree(fixture,fixture['sha'])),4)

    def test_selected_symlink_never_fetched_as_code(self):
        self.child=tree([blob('algorithm.py',self.source,mode='120000')])
        self.parent=tree([blob('LICENSE',self.license),dict(path='src',type='tree',mode='040000',sha=self.child['sha'])])
        self.commit.write_text(json.dumps(dict(sha='a'*40,tree=dict(sha=self.parent['sha']))))
        self.selection['repositories'][0].update(rootTree=self.parent['sha'],commitMetadataSha256=code.media.digest(self.commit));self.write()
        self.payloads.update({'/git/trees/'+self.parent['sha']:json.dumps(self.parent).encode(),'/git/trees/'+self.child['sha']:json.dumps(self.child).encode()})
        with self.assertRaisesRegex(ValueError,'source_blob_missing_linked'):self.run_intake()
        self.assertFalse((self.output/'intake.json').exists())

    def test_pinned_commit_snapshot_and_published_manifest_cannot_be_changed(self):
        result=self.run_intake();self.commit.write_text('{}')
        with self.assertRaisesRegex(ValueError,'commit_metadata_changed'):self.run_intake(True)
        self.setUp_commit_from_published()
        result['labelsApproved']=True;(self.output/'intake.json').write_text(json.dumps(result))
        with self.assertRaisesRegex(ValueError,'published_intake_changed'):self.run_intake(True)
        self.assertTrue(json.loads((self.output/'intake.json').read_text())['labelsApproved'])

    def setUp_commit_from_published(self):
        self.commit.write_bytes(next((self.output/'provenance').glob('*-commit.json')).read_bytes())

    def test_changed_source_and_tree_cache_rejected_without_redownload(self):
        result=self.run_intake();target=self.output/result['items'][0]['localFile'];target.write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError,'upstream_blob_changed'):self.run_intake(True)
        target.write_bytes(self.source)
        path=self.output/('provenance/trees/'+self.child['sha']+'.json');changed=json.loads(path.read_text());changed['tree'][0]['sha']='b'*40;path.write_text(json.dumps(changed))
        with self.assertRaisesRegex(ValueError,'tree_checksum_mismatch'):self.run_intake(True)

    def test_unknown_offline_source_no_partial_or_final_manifest(self):
        with self.assertRaisesRegex(FileNotFoundError,'missing_offline_source'):self.run_intake(True)
        self.assertFalse((self.output/'intake.json').exists());self.assertEqual(list(self.output.rglob('*.part')),[])

    def test_paths_duplicate_repositories_and_wrong_split_rejected(self):
        original=copy.deepcopy(self.selection)
        for path in ('../secret.py','C:/secret.py','src\\secret.py','a//b.py','https://other.test/a.py'):
            self.selection=copy.deepcopy(original);self.selection['repositories'][0]['files']=[path];self.write()
            with self.subTest(path=path),self.assertRaises(ValueError):code.selected(self.definition,self.metadata)
        self.selection=copy.deepcopy(original);self.selection['repositories']*=2;self.write()
        with self.assertRaisesRegex(ValueError,'invalid_pinned_repository'):code.selected(self.definition,self.metadata)
        self.selection=copy.deepcopy(original);self.selection['intendedSplit']='release-holdout';self.write()
        with self.assertRaisesRegex(ValueError,'invalid_development_selection'):code.selected(self.definition,self.metadata)

    def test_source_size_and_batch_limits_fail_without_manifest(self):
        for field,value in [('MAX_SOURCE',1),('MAX_TOTAL',1)]:
            with self.subTest(field=field),patch.object(code,field,value),self.assertRaises(ValueError):self.run_intake()
        self.assertFalse((self.output/'intake.json').exists())

    def test_reviewed_pool_and_existing_provenance_preserved(self):
        self.output.mkdir();code.publish(self.output,'provenance/a.json',b'original')
        with self.assertRaisesRegex(ValueError,'existing_provenance_changed'):code.publish(self.output,'provenance/a.json',b'changed')
        with self.assertRaisesRegex(ValueError,'intake_path_escape'):code.publish(self.output,'../outside',b'changed')
        (self.output/'freeze.json').write_text('preserve')
        with self.assertRaisesRegex(FileExistsError,'reviewed_or_frozen_pool'):self.run_intake()
        self.assertEqual((self.output/'freeze.json').read_text(),'preserve')


if __name__=='__main__':unittest.main()
