"""Bounded prepared segments. Invoked only on an owned snapshot by the sandbox host."""
import base64
import bisect
import io
import importlib
import json
import math
from pathlib import Path
import platform
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from contextlib import closing
from .packages import xml

from .manifest import sha256

XML_NS = dict(w='http://schemas.openxmlformats.org/wordprocessingml/2006/main',
              a='http://schemas.openxmlformats.org/drawingml/2006/main',
              s='http://schemas.openxmlformats.org/spreadsheetml/2006/main',
              r='http://schemas.openxmlformats.org/officeDocument/2006/relationships',
              table='urn:oasis:names:tc:opendocument:xmlns:table:1.0',
              text='urn:oasis:names:tc:opendocument:xmlns:text:1.0',
              office='urn:oasis:names:tc:opendocument:xmlns:office:1.0',
              draw='urn:oasis:names:tc:opendocument:xmlns:drawing:1.0')

def text_chunks(text, tokenize, limit=2048, max_chars=12000):
    """Use actual tokenizer offsets; no model-side truncation or hidden text clipping."""
    offset = 0
    while offset < len(text):
        end = min(len(text), offset + max_chars)
        encoding = tokenize(text[offset:end])
        if len(encoding.ids) > limit:
            end = offset + encoding.offsets[limit][0]
        if end <= offset:
            raise ValueError('tokenizer_offsets')
        value = text[offset:end]
        if value.strip():
            yield value, offset, end
        offset = end

def opaque_image(image):
    from PIL import Image
    if image.mode in ('RGBA','LA') or 'transparency' in image.info:
        rgba=image.convert('RGBA')
        return Image.alpha_composite(Image.new('RGBA',image.size,'white'),rgba).convert('RGB')
    return image.convert('RGB')

def image_bytes(image, pixels=1_000_000):
    from PIL import Image
    if min(image.size) <= 0 or image.width*image.height > 100_000_000:
        raise ValueError('image_pixel_budget')
    image = opaque_image(image)
    scale = min(1, math.sqrt(pixels/(image.width*image.height)))
    if scale < 1:
        image = image.resize((max(1, int(image.width*scale)), max(1, int(image.height*scale))), Image.Resampling.LANCZOS)
    output = io.BytesIO(); image.save(output, format='JPEG', quality=88)
    if output.tell() > 8*1024**2:
        raise ValueError('image_encoded_budget')
    thumbnail = image.copy(); thumbnail.thumbnail((384, 384))
    preview = io.BytesIO(); thumbnail.save(preview, format='JPEG', quality=80)
    return base64.b64encode(output.getvalue()).decode('ascii'), base64.b64encode(preview.getvalue()).decode('ascii')

class SegmentWriter:
    def __init__(self, output, tokenize, max_segments=256, max_bytes=256*1024**2, *, ocr=None):
        self.output = Path(output)
        self.output.mkdir(exist_ok=True)
        self.tokenize = tokenize
        self.max_segments, self.max_bytes = max_segments, max_bytes
        self.files, self.reasons, self.bytes, self.coverage = [], set(), 0, {}
        self.ocr=ocr
    @property
    def full(self):
        return len(self.files) >= self.max_segments or self.bytes >= self.max_bytes
    def emit(self, content, locator, evidence='', thumbnail='', *, coverage=None):
        if len(self.files) >= self.max_segments:
            self.reasons.add('segment_limit'); return False
        if len(evidence) > 32000:
            raise ValueError('evidence_budget')
        segment = dict(content=content, modality=content['type'], locator=locator,
                       evidence=evidence, thumbnail=thumbnail)
        if coverage is not None: segment['coverage'] = coverage
        data = json.dumps(segment, ensure_ascii=False, allow_nan=False).encode('utf-8')
        if len(data) > 32*1024**2 or self.bytes+len(data) > self.max_bytes:
            self.reasons.add('prepared_output_limit'); return False
        name = f'segment-{len(self.files):04}.json'
        (self.output / name).write_bytes(data)
        self.files.append(dict(name=name, bytes=len(data), sha256=sha256(self.output/name)))
        self.bytes += len(data)
        return True
    def text(self, text, locator, *, base_line=None):
        for value, start, end in text_chunks(text, self.tokenize):
            location = dict(locator)
            if base_line is not None:
                location.update(startLine=base_line+text.count('\n', 0, start), endLine=base_line+text.count('\n', 0, end-1))
            if not self.emit(dict(type='text', text=value), location, value): return False
        return True
    def table(self, context, cells, locator):
        """Repeat bounded sheet context and keep cell boundaries/addresses in chunks."""
        if not cells: return True
        first = next(text_chunks(context, self.tokenize, limit=512, max_chars=3000), None)
        prefix = first[0] if first else ''
        if first and first[2] < len(context): self.reasons.add('sheet_context_limit')
        if prefix and not prefix.endswith('\n'): prefix += '\n'
        starts, addresses, positions, length = [], [], [], 0
        for cell in cells:
            match = re.match(r'^([A-Z]{1,3})([1-9][0-9]{0,6}): ', cell)
            if not match: raise ValueError('sheet_cell_address')
            col = 0
            for char in match[1]: col = col*26+ord(char)-64
            row = int(match[2])
            if col > 16384 or row > 1048576: raise ValueError('sheet_cell_address')
            starts.append(length); addresses.append(match[1]+match[2]); positions.append((col,row))
            length += len(cell)+1
        text, offset = '\n'.join(cells), 0
        while offset < len(text):
            current = bisect.bisect_right(starts, offset)-1
            heading = prefix + (addresses[current]+' (continued):\n' if offset > starts[current] else '')
            end = min(len(text), offset+12000-len(heading))
            encoding = self.tokenize(heading+text[offset:end])
            if len(encoding.ids) > 2048:
                end = offset+encoding.offsets[2048][0]-len(heading)
            if end <= offset: raise ValueError('tokenizer_offsets')
            # A normal cell stays whole; only a cell larger than the complete
            # window is continued, with its address repeated above its value.
            boundary = bisect.bisect_right(starts, end)-1
            if end < len(text) and starts[boundary] > offset: end = starts[boundary]
            value = heading+text[offset:end]
            # Ending at a cell boundary can change BPE merges. Recheck the
            # exact emitted string, moving a whole cell to the next chunk.
            encoded = self.tokenize(value)
            while len(encoded.ids) > 2048:
                previous = bisect.bisect_left(starts, end)-1
                if starts[previous] > offset:
                    end = starts[previous]
                else:
                    end = min(end-1,offset+encoded.offsets[2048][0]-len(heading))
                if end <= offset: raise ValueError('tokenizer_offsets')
                value = heading+text[offset:end]
                encoded = self.tokenize(value)
            if len(value) > 12000: raise ValueError('sheet_text_budget')
            last = bisect.bisect_right(starts, end-1)-1
            included = positions[current:last+1]
            location = dict(locator, startCell=column(min(col for col,row in included))+str(min(row for col,row in included)),
                            endCell=column(max(col for col,row in included))+str(max(row for col,row in included)))
            if not self.emit(dict(type='text',text=value),location,value): return False
            offset = end
        return True
    def image(self, image, locator, evidence=''):
        data, thumbnail = image_bytes(image)
        if not self.emit(dict(type='image', data=data), locator, evidence, thumbnail):return False
        if self.ocr is not None:
            results=self.ocr.recognize(image)
            self.reasons.update(self.ocr.reasons)
            self.coverage['ocr']=self.ocr.coverage()
            seen=set()
            for language,text in results:
                if text in seen:continue
                seen.add(text)
                if not self.text(text,locator):return False
        return True
    def finish(self, source):
        result = dict(schemaVersion=1, sourceSha256=sha256(source), segments=self.files,
                      partial=bool(self.reasons), reasons=sorted(self.reasons), bytes=self.bytes)
        if self.coverage: result['coverage'] = self.coverage
        (self.output/'ready.json').write_text(json.dumps(result), encoding='utf-8')
        return result

def plain(source, writer):
    # Read a bounded block at a time, retaining original source line numbers.
    with Path(source).open('rb') as raw:
        prefix = raw.read(4)
    encoding = 'utf-16' if prefix.startswith((b'\xff\xfe', b'\xfe\xff')) else 'utf-8-sig'
    with Path(source).open('r', encoding=encoding, errors='strict') as reader:
        line, first, block = 1, 1, []
        while True:
            value = reader.readline(16000)
            if not value: break
            block.append(value)
            if sum(map(len, block)) >= 12000 or value.endswith('\n') and len(block) >= 40:
                if not writer.text(''.join(block), dict(kind='lines'), base_line=first): return
                block = []
                first = line + value.count('\n')
            line += value.count('\n')
            if line > 10_000_000:
                writer.reasons.add('line_limit'); return
        if block: writer.text(''.join(block), dict(kind='lines'), base_line=first)

def images(source, writer):
    from PIL import Image, ImageOps
    with Image.open(source) as image:
        count = getattr(image, 'n_frames', 1)
        for index in range(count):
            image.seek(index)
            if not writer.image(ImageOps.exif_transpose(image), dict(kind='image', page=index+1)):
                return
        # Vision coverage is independent of installed Windows OCR languages.

def pdfium_module():
    # The pinned raw binding checks platform.system() solely for a BSD loader
    # workaround. CPython 3.12 resolves that through WMI on Windows, which can
    # stall in AppContainer. sys.platform already establishes this exact answer.
    if sys.platform != 'win32': return importlib.import_module('pypdfium2')
    original = platform.system
    try:
        platform.system = lambda: 'Windows'
        return importlib.import_module('pypdfium2')
    finally:
        platform.system = original


def pdf(source, writer, *, kind='page', max_pages=128):
    from pypdf import PdfReader
    pdfium = pdfium_module()
    reader = PdfReader(source, strict=True)
    if reader.is_encrypted: raise ValueError('encrypted_pdf')
    with pdfium.PdfDocument(source) as document:
        total = len(document)
        if not 0 < total <= 100000: raise ValueError('pdf_page_budget')
        if total > max_pages: writer.reasons.add('page_limit')
        for index in range(min(total, max_pages)):
            locator = dict(kind=kind, page=index+1)
            text = reader.pages[index].extract_text() or ''
            if text and not writer.text(text, locator): return
            with closing(document[index]) as page:
                width, height = page.get_size()
                if min(width, height) <= 0 or not math.isfinite(width*height): raise ValueError('pdf_dimensions')
                scale = min(1.5, math.sqrt(1_000_000/(width*height)))
                while math.ceil(width*scale)*math.ceil(height*scale) > 1_000_000: scale *= .99
                with closing(page.render(scale=scale)) as bitmap:
                    if not writer.image(bitmap.to_pil(), locator, text[:1000]): return

def column(number):
    if not 1 <= number <= 16384: raise ValueError('sheet_column_limit')
    result = ''
    while number:
        number, digit = divmod(number-1, 26); result = chr(65+digit)+result
    return result

def csv_sheet(source, writer):
    import csv
    import itertools
    delimiter = '\t' if Path(source).suffix.lower() == '.tsv' else ','
    csv.field_size_limit(32000)
    with Path(source).open('r', encoding='utf-8-sig', newline='') as input:
        rows = csv.reader(input, delimiter=delimiter)
        header = next(rows, [])
        if not header: return
        if len(header) > 16384: raise ValueError('sheet_column_limit')
        first = 1
        for group in iter(lambda: list(itertools.islice(rows, 31)), []):
            values = [header] + group
            lines = ['Sheet: CSV', 'Headers: ' + ' | '.join(header)]
            for index, row in enumerate(values):
                if len(row) > 16384: raise ValueError('sheet_column_limit')
                lines.extend(f'{column(col+1)}{1 if index == 0 else first+index}: {value}' for col, value in enumerate(row) if value)
            end = first+len(group)
            location = dict(kind='sheet', sheet='CSV', startCell=f'A{1 if first == 1 else first+1}', endCell=f'{column(max(map(len, values)))}{end}')
            if not writer.table('\n'.join(lines[:2])+'\n', lines[2:], location): return
            first = end
            if first >= 1048576: writer.reasons.add('sheet_row_limit'); return
        if first == 1:
            writer.table('Sheet: CSV\nHeaders: '+' | '.join(header)+'\n', [f'{column(i+1)}1: {v}' for i,v in enumerate(header) if v], dict(kind='sheet', sheet='CSV', startCell='A1', endCell=f'{column(len(header))}1'))
