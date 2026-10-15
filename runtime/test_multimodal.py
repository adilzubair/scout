"""Offline contracts for model integrity, media boundaries and output validation."""

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import os
from unittest.mock import patch

import numpy as np
from semantic_search.embedding import FullEmbedder, local_file, normalize_vector, validate_content
from semantic_search.manifest import load_manifest, verify_model

TEST_ROOT = Path(__file__).resolve().parents[1] / 'artifacts/diagnostics/multimodal/contracts'


class MultimodalContracts(unittest.TestCase):
    def test_native_pools_are_capped_before_first_torch_import(self):
        class FirstTorchImport(Exception): pass
        original_import = __import__
        names = ('OMP_NUM_THREADS', 'MKL_NUM_THREADS', 'OPENBLAS_NUM_THREADS')
        def import_probe(name, *args, **kwargs):
            if name == 'torch':
                self.assertEqual([os.environ[key] for key in names], ['3', '3', '1'])
                if os.name == 'nt':
                    self.assertEqual(os.environ['MIMALLOC_PURGE_DELAY'], '0')
                    self.assertEqual(os.environ['MIMALLOC_PURGE_DECOMMITS'], '1')
                raise FirstTorchImport()
            return original_import(name, *args, **kwargs)
        with patch.dict(os.environ, {key: '32' for key in names}), patch('semantic_search.embedding.verify_model', return_value={}), patch('builtins.__import__', side_effect=import_probe):
            with self.assertRaises(FirstTorchImport):
                FullEmbedder('unused-test-model', threads=3)

    @classmethod
    def setUpClass(cls):
        TEST_ROOT.mkdir(parents=True, exist_ok=True)

    def test_manifest_requires_immutable_full_model(self):
        original = load_manifest()
        for field, value in [('revision', 'main'), ('fullModalities', ['text']),
                             ('repository', 'other/model'), ('schemaVersion', 2),
                             ('contextTokens', 999999), ('dimensions', [128])]:
            modified = copy.deepcopy(original)
            modified[field] = value
            with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
                path = Path(directory) / 'manifest.json'
                path.write_text(json.dumps(modified), encoding='utf-8')
                with self.assertRaises(ValueError):
                    load_manifest(path)

    def test_manifest_rejects_artifact_escape(self):
        for name in ('../outside', 'C:/outside', '/outside', 'a\\outside'):
            modified = load_manifest()
            modified['files'][name] = dict(bytes=1, sha256='a'*64)
            with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
                path = Path(directory) / 'manifest.json'
                path.write_text(json.dumps(modified), encoding='utf-8')
                with self.assertRaises(ValueError):
                    load_manifest(path)

    def test_integrity_checks_same_size_corruption_and_full_config(self):
        manifest = load_manifest()
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            root = Path(directory)
            for name in manifest['files']:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                data = b'fixture'
                if name == 'config.json':
                    data = json.dumps(dict(model_type='embedding_gemma2', audio_config={'ok': True},
                                           vision_config={'ok': True})).encode()
                path.write_bytes(data)
                manifest['files'][name] = dict(bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
            verify_model(root, manifest)
            (root / 'model.safetensors').write_bytes(b'corrupt')
            with self.assertRaisesRegex(ValueError, 'checksum'):
                verify_model(root, manifest)
            (root / 'model.safetensors').write_bytes(b'fixture')
            config = root / 'config.json'
            data = json.dumps(dict(model_type='embedding_gemma2', audio_config=None, vision_config={})).encode()
            config.write_bytes(data)
            manifest['files']['config.json'] = dict(bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
            with self.assertRaisesRegex(ValueError, 'encoders'):
                verify_model(root, manifest)

    def test_paths_cannot_escape_selected_root(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            root = Path(directory) / 'selected'
            root.mkdir()
            inside = root / 'a.png'
            outside = Path(directory) / 'private.png'
            inside.write_bytes(b'image')
            outside.write_bytes(b'private')
            self.assertEqual(local_file(inside, root), inside.resolve())
            with self.assertRaises(ValueError):
                local_file(outside, root)
            with self.assertRaises(ValueError):
                local_file(root, root)

    def test_truncation_renormalizes_and_rejects_invalid_vectors(self):
        vector = np.arange(1, 769, dtype=np.float32)
        for dimensions in (128, 256, 512, 768):
            actual = normalize_vector(vector, dimensions)
            self.assertEqual(actual.shape, (dimensions,))
            self.assertAlmostEqual(float(np.dot(actual, actual)), 1.0, places=5)
        for invalid in (np.zeros(768), np.full(768, np.nan), np.full(768, np.inf), np.zeros(767)):
            with self.assertRaises(ValueError):
                normalize_vector(invalid, 768)
        with self.assertRaises(ValueError):
            normalize_vector(vector, 64)

    def test_media_cannot_fetch_urls_or_unbounded_inputs(self):
        for kind in ('image', 'audio', 'video'):
            for payload in ('https://example.com/private', Path('local-media-file'), ['https://example.com/media']):
                with self.assertRaises(ValueError):
                    validate_content({kind: payload})
        with self.assertRaises(ValueError):
            validate_content({'audio': np.full(100, np.nan, dtype=np.float32)})
        with self.assertRaises(ValueError):
            validate_content({'video': np.zeros((17, 8, 8, 3), dtype=np.uint8)})
        with self.assertRaises(ValueError):
            validate_content(' ')
        validate_content({'audio': np.zeros(100, dtype=np.float32)})
        validate_content({'video': np.zeros((3, 8, 8, 3), dtype=np.uint8)})

    def test_missing_office_renderer_is_not_a_success(self):
        path = Path(__file__).resolve().parents[1] / 'scripts/probe-office-renderer.py'
        spec = importlib.util.spec_from_file_location('office_probe', path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with self.assertRaises(FileNotFoundError):
            module.find_renderer(Path('a-renderer-that-does-not-exist.exe'))


if __name__ == '__main__':
    unittest.main()
