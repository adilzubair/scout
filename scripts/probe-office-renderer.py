"""Check an explicitly available LibreOffice renderer on authored Scout fixtures."""

import argparse
import json
from pathlib import Path
import shutil
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'runtime'))
from semantic_search.embedding import local_file
from semantic_search.manifest import sha256
from semantic_search.office import FORMATS, OfficeRenderer


def find_renderer(explicit=None):
    if explicit:
        return explicit.resolve(strict=True)
    spec = json.loads((ROOT / 'runtime/office_renderer.json').read_text(encoding='utf-8'))
    candidate = ROOT / '.runtime/semantic-search/office' / ('libreoffice-' + spec['version']) / spec['executable']
    return candidate.resolve() if candidate.is_file() else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--soffice', type=Path)
    parser.add_argument('--model', type=Path, help='Also embed actual rendered pages offline')
    parser.add_argument('--device', choices=['cpu', 'cuda'], default='cpu')
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
        work = args.output.resolve().parent / (args.output.stem + '-office')
        work.mkdir(parents=True, exist_ok=False)
        started = time.perf_counter()
        office = OfficeRenderer(renderer.parent.parent)
        report.update(status='tested', executableSha256=sha256(renderer),
                      rendererVersion=office.spec['version'], integrityCheckMs=(time.perf_counter()-started)*1000)
        embedder = None
        if args.model:
            from semantic_search.embedding import FullEmbedder
            embedder = FullEmbedder(args.model, device=args.device, threads=4)
        expected_count = sum(Path(i['file']).suffix.lower() in FORMATS for i in fixture['items'])
        for item in fixture['items']:
            path = local_file(root / item['file'], root)
            if path.suffix.lower() not in FORMATS:
                continue
            if sha256(path) != fixture['files'][item['file']]['sha256']:
                raise ValueError('Office fixture checksum mismatch')
            start = time.perf_counter()
            row = dict(id=item['id'])
            try:
                with office.render(path, root, work) as result:
                    converted = work / (item['id'] + '.pdf')
                    shutil.copyfile(result['pdf'], converted)
                    text = ' '.join(' '.join(p.extract_text().split()) for p in PdfReader(converted).pages)
                    if any(expected not in text for expected in item.get('expectedText', [])):
                        raise ValueError('Expected authored content missing from rendered PDF')
                    if result['partial'] or result['totalPages'] != item.get('expectedPages', result['totalPages']):
                        raise ValueError('Unexpected authored page coverage')
                    conversion_ms = (time.perf_counter()-start)*1000
                    embeddings = []
                    for page in result['pages']:
                        image_path = work / (item['id'] + f'-page-{page["page"]}.png')
                        shutil.copyfile(page['image'], image_path)
                        if page['page'] == 1 and item.get('assertChartPixels'):
                            from PIL import Image
                            import numpy as np
                            with Image.open(image_path) as image:
                                pixels = np.asarray(image.convert('RGB'), dtype=np.int16)
                            # Fixture-specific: a title or OLE placeholder cannot satisfy a chart.
                            region = pixels[pixels.shape[0]//3:, :, :]
                            chromatic = region.max(axis=-1) - region.min(axis=-1) > 80
                            if float(chromatic.mean()) < 0.02:
                                raise ValueError('Authored chart is missing from rendered page')
                            row['chartPixelFraction'] = float(chromatic.mean())
                        if embedder:
                            from PIL import Image
                            import numpy as np
                            with Image.open(image_path) as image:
                                embedding_start = time.perf_counter()
                                vector, tokens = embedder.embed({'image': image.convert('RGB')})
                            embeddings.append(dict(page=page['page'], dimensions=len(vector),
                                tokens=tokens, norm=float(np.linalg.norm(vector)), embeddingMs=(time.perf_counter()-embedding_start)*1000))
                    row.update(status='ok', pages=result['totalPages'], partial=result['partial'],
                               conversionMs=conversion_ms, pdfSha256=sha256(converted), embeddings=embeddings,
                               contentAssertions=len(item.get('expectedText', [])))
            except Exception as error:
                row.update(status='failed', error=str(error))
            report['cases'].append(row)
        if any(row['status'] != 'ok' for row in report['cases']) or not expected_count or len(report['cases']) != expected_count:
            report['status'] = 'failed'
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(json.dumps(report))
    return 0 if report['status'] == 'tested' else 2


if __name__ == '__main__':
    raise SystemExit(main())
