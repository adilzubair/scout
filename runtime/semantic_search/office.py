"""Bounded local Office rendering for the development worker.

This is process isolation with package preflight, not an OS security sandbox.
Network denial and a Windows job resource limit remain host integration work.
"""

from contextlib import contextmanager
import json
import io
import math
import os
import re
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET
import zipfile

from .embedding import local_file
from .manifest import sha256
from .msi import safe_component

FORMATS = {'.docx', '.pptx', '.xlsx', '.odt', '.odp', '.ods'}
MAX_SOURCE_BYTES = 64 * 1024**2


def package_path(name):
    path = PurePosixPath(name)
    if not name or not path.parts or path.is_absolute() or '\\' in name or path.as_posix() != name:
        raise ValueError('Unsafe package path')
    for part in path.parts:
        safe_component(part)
    return Path(*path.parts)


def preflight(path):
    """Reject unsupported active/link content and excessive ZIP/XML expansion."""
    if path.suffix.lower() not in FORMATS or path.stat().st_size > MAX_SOURCE_BYTES:
        raise ValueError('Unsupported or oversized Office input')
    with zipfile.ZipFile(path) as archive:
        _preflight_archive(archive, path.suffix.lower(), [0], 0)


def _preflight_archive(archive, suffix, expanded, depth):
    if depth > 2:
        raise ValueError('Office embedded package depth exceeded')
    entries = archive.infolist()
    expanded[0] += sum(e.file_size for e in entries)
    if len(entries) > 10000 or expanded[0] > 256 * 1024**2:
        raise ValueError('Office archive expansion budget exceeded')
    seen = set()
    for entry in entries:
        name = entry.filename.rstrip('/')
        package_path(name)
        lower = name.casefold()
        if lower in seen or entry.flag_bits & 1:
            raise ValueError('Duplicate or encrypted Office member')
        seen.add(lower)
        if entry.is_dir():
            continue
        if (entry.file_size > 64 * 1024**2 or any(part in lower for part in
                ('vbaproject', 'activex/', 'scripts/', 'basic/',
                 'externallinks/', 'connections.xml', 'encryptedpackage'))):
            raise ValueError('Unsupported active or oversized Office member')
        if '/embeddings/' in lower:
            # Native presentation charts contain an embedded XLSX data package.
            if not lower.endswith('.xlsx'):
                raise ValueError('Unsupported embedded Office object')
            with zipfile.ZipFile(io.BytesIO(archive.read(entry))) as nested:
                _preflight_archive(nested, '.xlsx', expanded, depth+1)
        if not lower.endswith(('.xml', '.rels')):
            continue
        if entry.file_size > 8 * 1024**2:
            raise ValueError('Office XML budget exceeded')
        data = archive.read(entry)
        # Reject DTD declarations, including UTF-16/32 byte representations.
        declaration = data.replace(b'\0', b'').upper()
        if b'<!DOCTYPE' in declaration or b'<!ENTITY' in declaration:
            raise ValueError('Office XML entities are unsupported')
        tree = ET.fromstring(data)
        for node in tree.iter():
            tag = node.tag.rsplit('}', 1)[-1]
            if (suffix == '.xlsx' and tag == 'f') or any(
                    key.rsplit('}', 1)[-1] == 'formula' for key in node.attrib):
                # A later structured reader will produce cached-value-only render inputs.
                raise ValueError('Formula-bearing Office rendering is not implemented')
            if tag in ('ddeLink', 'dde-links', 'scripts', 'script', 'encryption-data'):
                raise ValueError('Unsupported active Office content')
            instruction = (node.text or '') if tag == 'instrText' else ' '.join(
                value for key, value in node.attrib.items() if key.rsplit('}', 1)[-1] == 'instr')
            if re.search(r'\b(DDEAUTO|DDE|INCLUDEPICTURE|INCLUDETEXT|LINK|DATABASE)\b', instruction, re.I):
                raise ValueError('Unsupported linked Office field')
            if tag == 'Relationship' and node.get('TargetMode', '').casefold() == 'external':
                if not node.get('Type', '').endswith('/hyperlink'):
                    raise ValueError('External Office resource is unsupported')
            href = node.get('{http://www.w3.org/1999/xlink}href')
            if node.tag.rsplit('}', 1)[-1] in ('object', 'object-ole', 'applet', 'plugin'):
                raise ValueError('Unsupported embedded OpenDocument object')
            if href and node.tag != '{urn:oasis:names:tc:opendocument:xmlns:text:1.0}a':
                if href.startswith('#'):
                    continue
                target = href.removeprefix('./')
                package_path(target)
                if target not in archive.namelist():
                    raise ValueError('External OpenDocument resource is unsupported')
    required = 'content.xml' if suffix in {'.odt', '.odp', '.ods'} else '[Content_Types].xml'
    if required.casefold() not in seen:
        raise ValueError('Missing Office package content')


def stop_tree(process):
    import psutil
    try:
        children = psutil.Process(process.pid).children(recursive=True)
    except psutil.NoSuchProcess:
        children = []
    for child in reversed(children):
        try:
            child.kill()
        except psutil.NoSuchProcess:
            pass
    if process.poll() is None:
        process.kill()
    process.wait(timeout=10)
    psutil.wait_procs(children, timeout=10)


def run_conversion(command, work, timeout, cancel=None):
    """Run an owned hidden process; cancel/timeout kills its current child tree."""
    if not math.isfinite(timeout) or not 0 < timeout <= 300:
        raise ValueError('Invalid Office timeout')
    if cancel is not None and cancel.is_set():
        raise InterruptedError('Office conversion cancelled')
    start = time.monotonic()
    with (work / 'conversion.log').open('wb') as log:
        process = subprocess.Popen(command, cwd=work, stdout=log, stderr=log,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        try:
            while process.poll() is None:
                if cancel is not None and cancel.is_set():
                    raise InterruptedError('Office conversion cancelled')
                if time.monotonic() - start >= timeout:
                    raise TimeoutError('Office conversion timed out')
                time.sleep(0.05)
            if process.returncode != 0:
                raise ValueError(f'Office conversion failed ({process.returncode})')
        except BaseException:
            stop_tree(process)
            raise


def write_profile(profile):
    user = profile / 'user'
    user.mkdir(parents=True)
    (user / 'registrymodifications.xcu').write_text('''<?xml version="1.0" encoding="UTF-8"?>
<oor:items xmlns:oor="http://openoffice.org/2001/registry">
<item oor:path="/org.openoffice.Office.Common/Security/Scripting">
<prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop>
<prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop>
<prop oor:name="DisablePythonRuntime" oor:op="fuse"><value>true</value></prop>
</item></oor:items>
''', encoding='utf-8')


class OfficeRenderer:
    def __init__(self, root, manifest=None):
        manifest = manifest or Path(__file__).resolve().parents[1] / 'office_renderer.json'
        self.spec = json.loads(Path(manifest).read_text(encoding='utf-8'))
        self.root = Path(root).resolve(strict=True)
        marker = self.root / 'scout-renderer-ready.json'
        if marker.stat().st_size > 16 * 1024**2:
            raise ValueError('Office readiness inventory exceeded budget')
        ready = json.loads(marker.read_text(encoding='utf-8'))
        if (ready.get('schemaVersion') != 1 or ready.get('version') != self.spec['version']
                or ready.get('packageSha256') != self.spec['sha256']):
            raise ValueError('Office readiness identity mismatch')
        files = ready.get('files', {})
        if not files or len(files) > 40000 or self.spec['executable'] not in files:
            raise ValueError('Invalid Office readiness inventory')
        # Verify once when starting the worker, rather than hashing for each page.
        for name, expected in files.items():
            file = local_file(self.root / package_path(name), self.root)
            if file.stat().st_size != expected['bytes'] or sha256(file) != expected['sha256']:
                raise ValueError(f'Office renderer integrity mismatch: {name}')
        self.executable = self.root / package_path(self.spec['executable'])

    @contextmanager
    def render(self, source, selected_root, work_root, *, max_pages=32, max_pixels=4_000_000,
               timeout=60, cancel=None):
        """Yield page rasters and PDF locators; remove the owned job on exit.

        Spreadsheet PDF page numbers are layout locators, not sheet/cell locators.
        Dedicated structured readers will supply the latter in a later slice.
        """
        if not isinstance(max_pages, int) or not 1 <= max_pages <= 256 or not 4096 <= max_pixels <= 8_000_000:
            raise ValueError('Invalid Office page budget')
        source = local_file(source, selected_root)
        if source.stat().st_size > MAX_SOURCE_BYTES or source.suffix.lower() not in FORMATS:
            raise ValueError('Unsupported or oversized Office input')
        source_hash = sha256(source)
        root = Path(work_root).resolve(strict=True)
        job = Path(tempfile.mkdtemp(prefix='office-', dir=root)).resolve(strict=True)
        try:
            snapshot = job / ('source' + source.suffix.lower())
            shutil.copyfile(source, snapshot)
            if snapshot.stat().st_size > MAX_SOURCE_BYTES or sha256(snapshot) != source_hash:
                raise ValueError('Office source changed during snapshot')
            preflight(snapshot)
            profile = job / 'profile'
            write_profile(profile)
            output = job / 'output'
            output.mkdir()
            run_conversion([str(self.executable), '-env:UserInstallation=' + profile.as_uri(),
                '--headless', '--nologo', '--norestore', '--nodefault', '--unaccept=all',
                '--convert-to', 'pdf', '--outdir', str(output), str(snapshot)], job, timeout, cancel)
            pdf = output / 'source.pdf'
            if not pdf.is_file() or not 0 < pdf.stat().st_size <= 128 * 1024**2:
                raise ValueError('Office renderer produced no bounded PDF')
            import pypdfium2 as pdfium
            pages = []
            with pdfium.PdfDocument(pdf) as document:
                total = len(document)
                if not 0 < total <= 10000:
                    raise ValueError('Office PDF page count exceeded budget')
                for number in range(min(total, max_pages)):
                    if cancel is not None and cancel.is_set():
                        raise InterruptedError('Office rendering cancelled')
                    page = document[number]
                    try:
                        width, height = page.get_size()
                        if not math.isfinite(width * height) or min(width, height) <= 0:
                            raise ValueError('Invalid Office page dimensions')
                        scale = min(1.5, math.sqrt(max_pixels / (width * height)))
                        # Round-up raster dimensions must stay within the pixel budget too.
                        while math.ceil(width * scale) * math.ceil(height * scale) > max_pixels:
                            scale *= 0.99
                        bitmap = page.render(scale=scale)
                        try:
                            path = output / f'page-{number+1}.png'
                            bitmap.to_pil().save(path)
                        finally:
                            bitmap.close()
                    finally:
                        page.close()
                    pages.append(dict(page=number+1, image=path))
            if sha256(source) != source_hash:
                raise ValueError('Office source changed during conversion')
            yield dict(pdf=pdf, pages=pages, totalPages=total, partial=len(pages) < total,
                       sourceSha256=source_hash, rendererVersion=self.spec['version'])
        finally:
            cleanup = job.resolve(strict=True)
            if cleanup != job or not cleanup.is_relative_to(root.resolve(strict=True)) or not job.name.startswith('office-'):
                raise ValueError('Unsafe Office cleanup target')
            shutil.rmtree(cleanup)
