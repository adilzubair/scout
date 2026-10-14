"""Extract a verified Windows Office renderer into Scout's local runtime, without installation."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'runtime'))
from semantic_search.manifest import sha256
from semantic_search.msi import MsiDatabase, file_layout, safe_component

MANIFEST = ROOT / 'runtime/office_renderer.json'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--download-only', action='store_true')
    args = parser.parse_args()
    spec = json.loads(MANIFEST.read_text(encoding='utf-8'))
    base = ROOT / '.runtime/semantic-search/office'
    base.mkdir(parents=True, exist_ok=True)
    package = base / spec['package']
    if not package.exists():
        partial = package.with_suffix('.partial')
        started = time.perf_counter()
        with urllib.request.urlopen(spec['url'], timeout=60) as response:
            if not response.url.startswith('https://'):
                raise ValueError('Renderer download must use HTTPS')
            with partial.open('wb') as stream:
                received = 0
                while chunk := response.read(1024 * 1024):
                    received += len(chunk)
                    if received > spec['bytes']:
                        raise ValueError('Renderer download exceeded pinned length')
                    stream.write(chunk)
        if partial.stat().st_size != spec['bytes'] or sha256(partial) != spec['sha256']:
            raise ValueError('Office package length/checksum mismatch')
        partial.replace(package)
        print(f'Office package downloaded and verified in {time.perf_counter()-started:.1f}s', flush=True)
    if package.stat().st_size != spec['bytes'] or sha256(package) != spec['sha256']:
        raise ValueError('Office package length/checksum mismatch')
    if args.download_only:
        return
    target = base / ('libreoffice-' + spec['version'])
    if target.exists():
        raise FileExistsError('Refusing to overwrite an extracted renderer')
    stage = Path(tempfile.mkdtemp(prefix='.extract-', dir=base))
    payload = stage / 'payload'
    payload.mkdir()
    started = time.perf_counter()
    with MsiDatabase(package) as database:
        layout = file_layout(database.rows('SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`', 3),
            database.rows('SELECT `Component`,`Directory_` FROM `Component`', 2),
            database.rows('SELECT `File`,`Component_`,`FileName`,`FileSize` FROM `File`', 4), spec['directoryRoot'])
        cabinets = [cabinet for (cabinet,) in database.rows('SELECT `Cabinet` FROM `Media`', 1) if cabinet]
        for ordinal, cabinet in enumerate(cabinets):
            if not cabinet.startswith('#'):
                raise ValueError('This extractor requires embedded cabinets')
            name = safe_component(cabinet[1:])
            cab = stage / (str(ordinal) + '.cab')
            flat = stage / ('cab-' + str(ordinal))
            flat.mkdir()
            database.export_cabinet(name, cab)
            subprocess.run([str(Path(os.environ['SystemRoot']) / 'System32/expand.exe'), '-F:*', str(cab), str(flat)],
                check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=180,
                creationflags=subprocess.CREATE_NO_WINDOW)
            for key, (relative, size) in layout.items():
                source = flat / key
                if not source.is_file():
                    continue  # File can belong to another cabinet.
                destination = payload / relative
                if not destination.resolve().is_relative_to(payload.resolve()) or source.stat().st_size != size:
                    raise ValueError('Extracted payload escaped its root or has invalid length')
                destination.parent.mkdir(parents=True, exist_ok=True)
                if destination.exists():
                    raise ValueError('Duplicate cabinet payload')
                shutil.copyfile(source, destination)
        inventory = {}
        for _, (relative, size) in layout.items():
            path = payload / relative
            if not path.is_file() or path.stat().st_size != size:
                raise ValueError(f'Missing Office payload file: {relative}')
            inventory[relative.as_posix()] = dict(bytes=size, sha256=sha256(path))
    if not (payload / spec['executable']).is_file():
        raise ValueError('Extracted renderer executable missing')
    (payload / 'scout-renderer-ready.json').write_text(json.dumps(dict(schemaVersion=1,
        version=spec['version'], packageSha256=spec['sha256'], files=inventory), indent=2)+'\n', encoding='utf-8')
    # Both source and target must stay beneath the explicitly owned runtime root.
    if not payload.resolve().is_relative_to(base.resolve()) or not target.resolve().is_relative_to(base.resolve()):
        raise ValueError('Renderer publication escaped its runtime')
    payload.rename(target)
    if not stage.resolve().is_relative_to(base.resolve()) or not stage.name.startswith('.extract-'):
        raise ValueError('Unsafe extraction cleanup target')
    shutil.rmtree(stage)
    print(f'Extracted {len(inventory)} application files in {time.perf_counter()-started:.1f}s; no installation actions executed', flush=True)
    print(target / spec['executable'])


if __name__ == '__main__':
    main()
