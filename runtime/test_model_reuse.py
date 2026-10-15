"""Verified generation reuse and interrupted file-copy recovery without model weights."""
import errno
import hashlib
import json
import sys
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from semantic_search import setup
from semantic_search.manifest import load_manifest,verify_model

ROOT=Path(__file__).resolve().parents[1]/'artifacts/diagnostics/multimodal/model-reuse-contracts'

class ModelReuseContracts(unittest.TestCase):
    def setUp(self):
        ROOT.mkdir(parents=True,exist_ok=True);self.folder=tempfile.TemporaryDirectory(dir=ROOT);self.addCleanup(self.folder.cleanup)
        self.root=Path(self.folder.name);self.name='cpu-'+'a'*24;self.source=self.root/'generations'/self.name/'model'
        self.manifest=load_manifest()
        for name in self.manifest['files']:
            path=self.source/name;path.parent.mkdir(parents=True,exist_ok=True)
            data=(b'fixture '+name.encode()) if name!='config.json' else json.dumps(dict(model_type='embedding_gemma2',audio_config={'ok':True},vision_config={'ok':True})).encode()
            path.write_bytes(data);self.manifest['files'][name]=dict(bytes=len(data),sha256=hashlib.sha256(data).hexdigest())
        (self.root/'installed.json').write_text(json.dumps(dict(generation=self.name)))
        self.generation=self.root/'generations'/('cpu-'+'b'*24);self.destination=self.generation/'model'
        patch.object(setup,'load_manifest',return_value=self.manifest).start()
        patch.object(setup,'verify_model',side_effect=lambda path,manifest=None:verify_model(path,self.manifest)).start()
        self.addCleanup(patch.stopall)

    def test_only_verified_selected_prior_model_is_reused(self):
        self.assertEqual(setup.previous_model(self.root,self.generation),self.source)
        setup.copy_model(self.source,self.destination)
        verify_model(self.destination,self.manifest)
        self.assertFalse(any(self.source.rglob('*.partial')))
        self.assertIsNone(setup.previous_model(self.root,self.source.parent))

    def test_disk_full_preserves_published_files_and_retry_skips_completed_copies(self):
        names=list(self.manifest['files']);first=self.destination/names[0];first.parent.mkdir(parents=True);first.write_bytes((self.source/names[0]).read_bytes())
        before=first.stat().st_mtime_ns
        second=self.destination/names[1];second.parent.mkdir(parents=True,exist_ok=True);second.write_bytes(b'prior-incomplete-file')
        source_hashes={name:setup.sha256(self.source/name) for name in names}
        def fail(reader,writer,length):
            writer.write(reader.read(3));raise OSError(errno.ENOSPC,'simulated_disk_full')
        with patch.object(setup.shutil,'copyfileobj',side_effect=fail),self.assertRaises(OSError):setup.copy_model(self.source,self.destination)
        self.assertEqual(second.read_bytes(),b'prior-incomplete-file');self.assertEqual(first.stat().st_mtime_ns,before)
        self.assertFalse(any(self.destination.rglob('*.partial')))
        with patch.object(setup.shutil,'copyfileobj',wraps=setup.shutil.copyfileobj) as copies:setup.copy_model(self.source,self.destination)
        self.assertEqual(copies.call_count,len(names)-1);self.assertEqual(first.stat().st_mtime_ns,before)
        self.assertEqual({name:setup.sha256(self.source/name) for name in names},source_hashes)
        verify_model(self.destination,self.manifest)

    def test_corruption_is_not_imported_or_used_as_an_offline_fallback(self):
        path=self.source/'model.safetensors';raw=path.read_bytes();path.write_bytes(b'x'*len(raw))
        self.assertIsNone(setup.previous_model(self.root,self.generation))
        with self.assertRaises(ValueError):setup.copy_model(self.source,self.destination)
        self.assertFalse(self.destination.exists())

    def test_retry_reclaims_only_its_exact_abandoned_copy_temporary(self):
        name=next(iter(self.manifest['files']));target=self.destination/name;target.parent.mkdir(parents=True)
        target.write_bytes((self.source/name).read_bytes())
        abandoned=target.with_name(target.name+'.scout-copy.partial');abandoned.write_bytes(b'abandoned-copy')
        unknown=target.with_name(target.name+'.unknown.partial');unknown.write_bytes(b'keep-unknown')
        setup.copy_model(self.source,self.destination)
        self.assertFalse(abandoned.exists());self.assertEqual(unknown.read_bytes(),b'keep-unknown')
        verify_model(self.destination,self.manifest)

    def test_copy_disk_full_cannot_publish_a_new_generation_or_change_the_previous_marker(self):
        marker=(self.root/'installed.json').read_bytes()
        argv=['setup.py','--root',str(self.root),'--uv','uv.exe','--offline']
        def full(reader,writer,length):raise OSError(errno.ENOSPC,'simulated_disk_full')
        with patch.object(sys,'argv',argv),patch.object(setup,'run'),patch.object(setup,'verify_venv_home'),patch.object(setup.shutil,'copyfileobj',side_effect=full):
            with self.assertRaises(OSError):setup.main()
        self.assertEqual((self.root/'installed.json').read_bytes(),marker)
        self.assertFalse((self.root/'generations'/setup.generation_id('cpu')/'installed.json').exists())
        self.assertFalse(any((self.root/'generations'/setup.generation_id('cpu')).rglob('*.partial')))
        verify_model(self.source,self.manifest)

    def test_invalid_oversized_and_linked_prior_identity_is_ignored(self):
        marker=self.root/'installed.json'
        for raw in ['{"generation":"../outside"}','{"generation":42}','{','x'*16385]:
            marker.write_text(raw);self.assertIsNone(setup.previous_model(self.root,self.generation))
        marker.write_text(json.dumps(dict(generation=self.name)))
        with patch.object(Path,'is_junction',return_value=True):self.assertIsNone(setup.previous_model(self.root,self.generation))

    def test_linked_destination_is_rejected_before_file_copy(self):
        self.destination.mkdir(parents=True)
        with patch.object(Path,'is_junction',autospec=True,side_effect=lambda path:path==self.destination),patch.object(setup.shutil,'copyfileobj') as copy:
            with self.assertRaisesRegex(ValueError,'linked_model_reuse_path'):setup.copy_model(self.source,self.destination)
            copy.assert_not_called()

if __name__=='__main__':unittest.main()
