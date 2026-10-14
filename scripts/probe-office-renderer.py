"""Check an explicitly available LibreOffice renderer on authored Scout fixtures."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'runtime'))
from semantic_search.embedding import local_file
from semantic_search.manifest import sha256


def find_renderer(explicit=None):
    if explicit:
        return explicit.resolve(strict=True)
    candidates = [shutil.which('soffice.exe')]
    for base in ('ProgramFiles', 'ProgramFiles(x86)'):
        if os.environ.get(base):
            candidates.append(str(Path(os.environ[base]) / 'LibreOffice/program/soffice.exe'))
    return next((Path(p) for p in candidates if p and Path(p).is_file()), None)


def stop_tree(process):
    import psutil
    try:
        descendants = psutil.Process(process.pid).children(recursive=True)
        for child in reversed(descendants):
            try:
                child.kill()
            except psutil.NoSuchProcess:
                pass
    except psutil.NoSuchProcess:
        pass
    if process.poll() is None:
        process.kill()
    process.communicate()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--soffice', type=Path)
    args = parser.parse_args()
    if args.output.exists():
        raise FileExistsError('Refusing to overwrite a renderer report')
    fixture = json.loads(args.fixtures.read_text(encoding='utf-8'))
    root = args.fixtures.resolve().parent
    renderer = find_renderer(args.soffice)
    report = dict(schemaVersion=1, fixtureSha256=sha256(args.fixtures),
                  renderer='LibreOffice', status='unavailable', cases=[])
    if renderer:
        from pypdf import PdfReader
        import pypdfium2 as pdfium
        work = args.output.resolve().parent / (args.output.stem + '-office')
        work.mkdir(parents=True, exist_ok=False)
        profile = work / 'profile'
        profile.mkdir()
        report.update(status='tested', executableSha256=sha256(renderer))
        for item in fixture['items']:
            path = local_file(root / item['file'], root)
            if path.suffix.lower() not in ('.docx', '.pptx', '.xlsx'):
                continue
            if sha256(path) != fixture['files'][item['file']]['sha256']:
                raise ValueError('Office fixture checksum mismatch')
            start = time.perf_counter()
            process = subprocess.Popen([str(renderer), '-env:UserInstallation=' + profile.as_uri(),
                '--headless', '--norestore', '--nodefault', '--nofirststartwizard',
                '--convert-to', 'pdf', '--outdir', str(work), str(path)],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            row = dict(id=item['id'])
            try:
                stdout, stderr = process.communicate(timeout=60)
                converted = work / (path.stem + '.pdf')
                if process.returncode != 0 or not converted.is_file():
                    raise ValueError('Conversion failed: ' + (stdout + stderr).decode(errors='replace')[-1000:])
                with pdfium.PdfDocument(converted) as document:
                    page = document[0]
                    bitmap = page.render(scale=1)
                    try:
                        bitmap.to_pil().save(work / (path.stem + '.png'))
                    finally:
                        bitmap.close()
                        page.close()
                row.update(status='ok', pages=len(PdfReader(converted).pages),
                           conversionMs=(time.perf_counter()-start)*1000, pdfSha256=sha256(converted))
            except Exception as error:
                stop_tree(process)
                row.update(status='failed', error=str(error))
            report['cases'].append(row)
        if any(row['status'] != 'ok' for row in report['cases']) or len(report['cases']) != 3:
            report['status'] = 'failed'
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(json.dumps(report))
    return 0 if report['status'] == 'tested' else 2


if __name__ == '__main__':
    raise SystemExit(main())
