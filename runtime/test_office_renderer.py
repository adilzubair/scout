"""Offline Office extraction, content-boundary and lifecycle contracts."""

import json
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
import zipfile

from semantic_search.manifest import sha256
from semantic_search.msi import file_layout, safe_component, target_name
from semantic_search.office import OfficeRenderer, package_path, preflight, run_conversion

ROOT = Path(__file__).resolve().parents[1]
TEST_ROOT = ROOT / 'artifacts/diagnostics/multimodal/office-contracts'


def package(path, entries=None):
    with zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        for name, value in (entries or {'[Content_Types].xml': '<Types/>',
                                      'word/document.xml': '<document/>'}).items():
            archive.writestr(name, value)
    return path


class OfficeContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        TEST_ROOT.mkdir(parents=True, exist_ok=True)

    def test_msi_layout_reconstructs_long_names_and_excludes_system_files(self):
        layout = file_layout([('ROOT', '', '.'), ('APP', 'ROOT', 'Office'),
                             ('PROG', 'APP', 'PROGRA~1|program:source')],
                             [('C1', 'PROG'), ('C2', 'ROOT')],
                             [('F1', 'C1', 'SOFFIC~1.COM|soffice.com', '3'),
                              ('F2', 'C2', 'system.dll', '1')], 'APP')
        self.assertEqual(layout, {'F1': (Path('program/soffice.com'), 3)})
        self.assertEqual(target_name('.:source'), '')

    def test_msi_layout_rejects_cycles_collisions_and_unknown_references(self):
        dirs = [('APP', '', '.'), ('A', 'B', 'a'), ('B', 'A', 'b')]
        with self.assertRaises(ValueError):
            file_layout(dirs, [('C', 'A')], [('F', 'C', 'file', '1')], 'APP')
        for files in [[('F1', 'C', 'a', '1'), ('F2', 'C', 'A', '1')],
                      [('F1', 'UNKNOWN', 'a', '1')], [('F1', 'C', 'a', str(3*1024**3))]]:
            with self.assertRaises(ValueError):
                file_layout([('APP', '', '.')], [('C', 'APP')], files, 'APP')

    def test_package_paths_cannot_escape_or_address_windows_devices(self):
        for path in ('.', '../private', 'C:/private', '/private', 'a\\b', 'a//b',
                     'a/CON.txt', 'a/secret:stream', 'a/trailing.', 'a/./b'):
            with self.assertRaises(ValueError, msg=path):
                package_path(path)
        with self.assertRaises(ValueError):
            safe_component('NUL')

    def test_preflight_rejects_active_external_and_entity_content(self):
        cases = [
            {'word/vbaProject.bin': b'macro'},
            {'word/embeddings/object.bin': b'object'},
            {'xl/externalLinks/link.xml': '<link/>'},
            {'xl/connections.xml': '<connections/>'},
            {'word/document.xml': '<!DOCTYPE a [<!ENTITY b "test">]><a>&b;</a>'},
            {'word/document.xml': '<!DOCTYPE a><a/>'.encode('utf-16')},
            {'word/document.xml': '<document><instrText>DDEAUTO spreadsheet</instrText></document>'},
            {'word/_rels/document.xml.rels': '<Relationships><Relationship TargetMode="External" Type="image" Target="https://example.com/a.png"/></Relationships>'}]
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            path = Path(directory) / 'bad.docx'
            for extra in cases:
                package(path, {'[Content_Types].xml': '<Types/>', **extra})
                with self.assertRaises(ValueError):
                    preflight(path)
            odt = package(Path(directory) / 'bad.odt', {'content.xml':
                '<document xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href="file:///C:/private.png"/></document>'})
            with self.assertRaises(ValueError):
                preflight(odt)

    def test_preflight_rejects_nested_chart_workbook_external_links(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            nested = package(Path(directory) / 'data.xlsx', {'[Content_Types].xml': '<Types/>',
                        'xl/externalLinks/link.xml': '<link/>'})
            outer = package(Path(directory) / 'chart.pptx', {'[Content_Types].xml': '<Types/>',
                        'ppt/embeddings/data.xlsx': nested.read_bytes()})
            with self.assertRaises(ValueError):
                preflight(outer)

    def test_formula_rendering_cannot_recalculate_or_fetch_data(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            for suffix, entries in [('.xlsx', {'[Content_Types].xml': '<Types/>',
                'xl/worksheets/sheet1.xml': '<worksheet><f>WEBSERVICE("https://example.com")</f></worksheet>'}),
                ('.ods', {'content.xml': '<document xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"><cell table:formula="of:=1+1"/></document>'})]:
                path = package(Path(directory) / ('formula' + suffix), entries)
                with self.assertRaisesRegex(ValueError, 'Formula-bearing'):
                    preflight(path)

    def test_preflight_preserves_normal_hyperlinks_and_embedded_chart_data(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            nested = package(Path(directory) / 'data.xlsx')
            path = package(Path(directory) / 'chart.pptx', {'[Content_Types].xml': '<Types/>',
                'ppt/embeddings/data.xlsx': nested.read_bytes(), 'ppt/_rels/slide.xml.rels':
                '<Relationships><Relationship TargetMode="External" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.com/"/></Relationships>'})
            preflight(path)

    def test_preflight_rejects_expansion_duplicates_and_missing_content(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            path = package(Path(directory) / 'large.docx', {'[Content_Types].xml': '<Types/>',
                'word/document.xml': b' ' * (8*1024**2+1)})
            with self.assertRaisesRegex(ValueError, 'XML budget'):
                preflight(path)
            for entries in ({'a.xml': '<a/>'}, {'[Content_Types].xml': '<Types/>', 'A.xml': '<a/>', 'a.xml': '<a/>'},
                            {'[Content_Types].xml': '<Types/>', '../escape': 'x'}):
                package(path, entries)
                with self.assertRaises(ValueError):
                    preflight(path)

    def make_renderer(self, directory):
        root = Path(directory) / 'renderer'
        (root / 'program').mkdir(parents=True)
        executable = root / 'program/soffice.com'
        executable.write_bytes(b'test-renderer')
        spec = json.loads((ROOT / 'runtime/office_renderer.json').read_text(encoding='utf-8'))
        ready = dict(schemaVersion=1, version=spec['version'], packageSha256=spec['sha256'],
            files={'program/soffice.com': dict(bytes=executable.stat().st_size, sha256=sha256(executable))})
        marker = root / 'scout-renderer-ready.json'
        marker.write_text(json.dumps(ready), encoding='utf-8')
        return root, marker, ready

    def test_renderer_integrity_rejects_missing_changed_and_escaped_files(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            root, marker, ready = self.make_renderer(directory)
            OfficeRenderer(root)
            for changed in [dict(version='old'), dict(packageSha256='0'*64),
                            dict(files={'../outside': dict(bytes=1, sha256='0'*64)}),
                            dict(files={'program/soffice.com': dict(bytes=13, sha256='0'*64)})]:
                marker.write_text(json.dumps({**ready, **changed}), encoding='utf-8')
                with self.assertRaises(ValueError):
                    OfficeRenderer(root)
            marker.unlink()
            with self.assertRaises(FileNotFoundError):
                OfficeRenderer(root)

    def test_render_bounds_pages_and_cleans_snapshot_without_changing_source(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            root, _, _ = self.make_renderer(directory)
            source = package(Path(directory) / '--convert-to.docx')
            original = source.read_bytes()
            work = Path(directory) / 'work'
            work.mkdir()
            def convert(command, job, *_):
                self.assertEqual(Path(command[-1]).name, 'source.docx')
                from pypdf import PdfWriter
                writer = PdfWriter()
                writer.add_blank_page(width=595, height=842)
                writer.add_blank_page(width=595, height=842)
                writer.write(job / 'output/source.pdf')
            with patch('semantic_search.office.run_conversion', side_effect=convert):
                with OfficeRenderer(root).render(source, Path(directory), work, max_pages=1, max_pixels=10000) as result:
                    self.assertTrue(result['partial'])
                    self.assertEqual(result['totalPages'], 2)
                    self.assertEqual([p['page'] for p in result['pages']], [1])
                    from PIL import Image
                    with Image.open(result['pages'][0]['image']) as image:
                        self.assertLessEqual(image.width*image.height, 10000)
            self.assertEqual(source.read_bytes(), original)
            self.assertEqual(list(work.iterdir()), [])

    def test_failed_and_cancelled_render_remove_only_owned_job(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            root, _, _ = self.make_renderer(directory)
            source = package(Path(directory) / 'source.docx')
            work = Path(directory) / 'work'
            work.mkdir()
            unrelated = work / 'keep'
            unrelated.write_text('keep')
            for failure in (TimeoutError('timeout'), InterruptedError('cancelled'), ValueError('bad PDF')):
                with patch('semantic_search.office.run_conversion', side_effect=failure):
                    with self.assertRaises(type(failure)):
                        with OfficeRenderer(root).render(source, Path(directory), work):
                            self.fail('Failed rendering cannot yield content')
                self.assertEqual(list(work.iterdir()), [unrelated])

    def test_changed_source_during_conversion_is_not_published(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            root, _, _ = self.make_renderer(directory)
            source = package(Path(directory) / 'source.docx')
            work = Path(directory) / 'work'
            work.mkdir()
            def convert(command, job, *_):
                from pypdf import PdfWriter
                writer = PdfWriter()
                writer.add_blank_page(width=595, height=842)
                writer.write(job / 'output/source.pdf')
                source.write_bytes(b'changed source')
            with patch('semantic_search.office.run_conversion', side_effect=convert):
                with self.assertRaisesRegex(ValueError, 'source changed'):
                    with OfficeRenderer(root).render(source, Path(directory), work):
                        self.fail('Changed source cannot publish pages')
            self.assertEqual(list(work.iterdir()), [])

    def test_conversion_timeout_and_cancel_kill_owned_child_tree(self):
        import psutil
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            work = Path(directory)
            script = work / 'hang.py'
            script.write_text('import subprocess, sys, time\nfrom pathlib import Path\n'
                'child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])\n'
                'Path("child.pid").write_text(str(child.pid))\ntime.sleep(60)\n')
            for cancel in (None, threading.Event()):
                timer = threading.Timer(1, cancel.set) if cancel else None
                if timer:
                    timer.start()
                try:
                    with self.assertRaises(InterruptedError if cancel else TimeoutError):
                        run_conversion([sys.executable, str(script)], work, 2, cancel)
                    pid = int((work / 'child.pid').read_text())
                    self.assertFalse(psutil.pid_exists(pid), f'Owned child {pid} survived')
                finally:
                    if timer:
                        timer.cancel()

    def test_conversion_failure_and_precancel_are_explicit(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as directory:
            work = Path(directory)
            with self.assertRaisesRegex(ValueError, 'failed'):
                run_conversion([sys.executable, '-c', 'raise SystemExit(2)'], work, 5)
            event = threading.Event()
            event.set()
            with patch('subprocess.Popen') as start:
                with self.assertRaises(InterruptedError):
                    run_conversion(['never-start'], work, 5, event)
                start.assert_not_called()


if __name__ == '__main__':
    unittest.main()
