"""Installation publication and interrupted-download contracts, without model weights."""

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from semantic_search import setup

ROOT = Path(__file__).resolve().parents[1]
TEST_ROOT = ROOT / 'artifacts/diagnostics/multimodal/setup-contracts'
spec = importlib.util.spec_from_file_location('office_download', ROOT / 'scripts/fetch-office-renderer.py')
download = importlib.util.module_from_spec(spec)
spec.loader.exec_module(download)


class Response(io.BytesIO):
    url = 'https://publisher.example/package'
    def __init__(self, content, status=200, headers=None):
        super().__init__(content)
        self.status, self.headers = status, headers or {}


class SetupContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        TEST_ROOT.mkdir(parents=True, exist_ok=True)

    def test_office_download_resumes_and_checks_the_full_artifact(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            file = Path(directory) / 'office.msi'
            file.with_suffix('.partial').write_bytes(b'first')
            content = b'firstsecond'
            pinned = dict(url='https://publisher.example/package', bytes=len(content), sha256=hashlib.sha256(content).hexdigest())
            def fetch(request, **kwargs):
                self.assertEqual(request.get_header('Range'), 'bytes=5-')
                return Response(b'second', 206, {'Content-Range': 'bytes 5-10/11'})
            with patch('urllib.request.urlopen', side_effect=fetch):
                download.download_package(pinned, file)
            self.assertEqual(file.read_bytes(), content)
            self.assertFalse(file.with_suffix('.partial').exists())

    def test_ignored_ranges_restart_and_bad_ranges_or_hashes_never_publish(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            file = Path(directory) / 'office.msi'
            content = b'complete'
            pinned = dict(url='https://publisher.example/package', bytes=len(content), sha256=hashlib.sha256(content).hexdigest())
            file.with_suffix('.partial').write_bytes(b'com')
            with patch('urllib.request.urlopen', return_value=Response(content)):
                download.download_package(pinned, file)
            self.assertEqual(file.read_bytes(), content)
            file.unlink()
            for response in [Response(b'bad', 206, {'Content-Range': 'bytes 2-7/8'}), Response(b'corrupt!')]:
                file.with_suffix('.partial').write_bytes(b'com')
                with patch('urllib.request.urlopen', return_value=response):
                    with self.assertRaises(ValueError):
                        download.download_package(pinned, file)
                self.assertFalse(file.exists())

    def test_failed_dependency_setup_preserves_previous_marker_and_retry_generation(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            root = Path(directory)
            previous = {'previous': 'installation'}
            setup.atomic_json(root / 'installed.json', previous)
            argv = ['setup.py', '--root', str(root), '--uv', 'uv.exe', '--offline']
            def fail(command, env):
                if command[1] == 'venv':
                    python = root / 'generations' / setup.generation_id('cpu') / 'venv/Scripts/python.exe'
                    python.parent.mkdir(parents=True)
                    python.write_bytes(b'partial-environment')
                    return
                raise subprocess.CalledProcessError(1, command)
            for _ in range(2):
                with patch.object(sys, 'argv', argv), patch.object(setup, 'run', side_effect=fail):
                    with self.assertRaises(subprocess.CalledProcessError):
                        setup.main()
                self.assertEqual(json.loads((root / 'installed.json').read_text()), previous)
                self.assertFalse((root / 'generations' / setup.generation_id('cpu') / 'installed.json').exists())

    def test_existing_generation_is_reverified_without_modifying_its_environment(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            root = Path(directory)
            generation = root / 'generations' / setup.generation_id('cpu')
            generation.mkdir(parents=True)
            marker = {'generation': generation.name, 'schemaVersion': 1}
            setup.atomic_json(generation / 'installed.json', marker)
            commands = []
            with patch.object(sys, 'argv', ['setup.py', '--root', str(root), '--uv', 'uv.exe']), patch.object(setup, 'run', side_effect=lambda command, env: commands.append(command)):
                setup.main()
            self.assertEqual(len(commands), 1)
            self.assertIn('--verify', commands[0])
            self.assertEqual(json.loads((root / 'installed.json').read_text()), marker)


if __name__ == '__main__':
    unittest.main()
