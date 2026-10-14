"""Installation publication and interrupted-download contracts, without model weights."""

import hashlib
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


class SetupContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        TEST_ROOT.mkdir(parents=True, exist_ok=True)

    def test_fresh_generation_publishes_without_any_renderer(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            root = Path(directory)
            commands = []
            def run(command, env):
                commands.append(command)
                if command[1] == 'venv':
                    python = root / 'generations' / setup.generation_id('cpu') / 'venv/Scripts/python.exe'
                    python.parent.mkdir(parents=True); python.write_bytes(b'fixture')
            with patch.object(sys, 'argv', ['setup.py', '--root', str(root), '--uv', 'uv.exe', '--existing-model', str(root/'model'), '--offline']), patch.object(setup, 'run', side_effect=run), patch.object(setup, 'copy_model'):
                setup.main()
            marker = json.loads((root/'installed.json').read_text())
            self.assertEqual(marker['schemaVersion'], 2)
            self.assertNotIn('office', marker)
            self.assertNotIn('officeManifestSha256', marker)
            self.assertFalse(any('office' in str(command).casefold() for command in commands))
            self.assertEqual(marker, json.loads((root/'generations'/marker['generation']/'installed.json').read_text()))

    def test_readiness_checks_every_encoder_without_a_renderer(self):
        with patch('semantic_search.embedding.FullEmbedder') as embedder:
            setup.verify(TEST_ROOT/'model-only-generation', 'cpu')
            calls = embedder.return_value.embed.call_args_list
            self.assertEqual(len(calls), 5)
            self.assertTrue(calls[0].kwargs['query'])
            self.assertEqual([set(call.args[0]) for call in calls[1:]], [{'image'}, {'audio'}, {'video'},{'video','audio'}])

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
            marker = {'generation': generation.name, 'schemaVersion': 2}
            setup.atomic_json(generation / 'installed.json', marker)
            commands = []
            with patch.object(sys, 'argv', ['setup.py', '--root', str(root), '--uv', 'uv.exe']), patch.object(setup, 'run', side_effect=lambda command, env: commands.append(command)):
                setup.main()
            self.assertEqual(len(commands), 2)
            self.assertIn('--verify', commands[0])
            self.assertTrue(str(commands[1][1]).endswith('notices.py'))
            self.assertEqual(json.loads((root / 'installed.json').read_text()), marker)

    def test_notice_failure_cannot_publish_a_new_ready_generation(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            root=Path(directory);previous={'previous':'installation'};setup.atomic_json(root/'installed.json',previous)
            def run(command,env):
                if command[1]=='venv':
                    python=root/'generations'/setup.generation_id('cpu')/'venv/Scripts/python.exe'
                    python.parent.mkdir(parents=True);python.write_bytes(b'fixture')
                elif str(command[1]).endswith('notices.py'):raise subprocess.CalledProcessError(1,command)
            with patch.object(sys,'argv',['setup.py','--root',str(root),'--uv','uv.exe','--existing-model',str(root/'model'),'--offline']),patch.object(setup,'run',side_effect=run),patch.object(setup,'copy_model'):
                with self.assertRaises(subprocess.CalledProcessError):setup.main()
            self.assertEqual(json.loads((root/'installed.json').read_text()),previous)
            self.assertFalse((root/'generations'/setup.generation_id('cpu')/'installed.json').exists())


if __name__ == '__main__':
    unittest.main()
