"""Inert Office content extraction without Office, LibreOffice or formula evaluation."""
import io
from pathlib import Path, PurePosixPath
import re
import xml.etree.ElementTree as ET

from .packages import package, relationships, member_path, xml
from .readers import XML_NS, column

NS = dict(XML_NS, c='http://schemas.openxmlformats.org/drawingml/2006/chart',
          p='http://schemas.openxmlformats.org/presentationml/2006/main',
          xlink='http://www.w3.org/1999/xlink')
R = '{' + NS['r'] + '}'

def embedded_image(archive, name, writer, locator):
    from PIL import Image, UnidentifiedImageError
    entry = archive.getinfo(name)
    if entry.file_size > 32*1024**2:
        writer.reasons.add('embedded_image_size'); return True
    try:
        with Image.open(io.BytesIO(archive.read(entry))) as image:
            if image.format not in ('PNG', 'JPEG', 'WEBP', 'BMP', 'TIFF', 'GIF'):
                writer.reasons.add('embedded_image_format'); return True
            frames = getattr(image, 'n_frames', 1)
            for frame in range(frames):
                image.seek(frame)
                if not writer.image(image, locator, 'Embedded image'): return False
            return True
    except UnidentifiedImageError:
        writer.reasons.add('embedded_image_format'); return True

def chart(archive, name, writer, locator):
    tree = xml(archive, name)
    kinds = [node.tag.rsplit('}', 1)[-1] for node in tree.iter() if node.tag.startswith('{'+NS['c']+'}') and node.tag.endswith('Chart')]
    title = ' '.join(node.text or '' for node in tree.findall('.//c:title//a:t', NS))
    lines = ['Chart: ' + (title or 'Untitled'), 'Chart types: ' + ', '.join(kinds)]
    for number, series in enumerate(tree.findall('.//c:ser', NS), 1):
        label = ' '.join(node.text or '' for node in series.findall('./c:tx//c:v', NS)) or f'Series {number}'
        lines.append('Series: ' + label)
        for role in ('cat', 'val', 'xVal', 'yVal', 'bubbleSize'):
            points = []
            for point in series.findall('./c:'+role+'//c:pt', NS):
                value = point.find('c:v', NS)
                if value is not None: points.append((point.get('idx', ''), value.text or ''))
            if len(points) > 10000: raise ValueError('chart_point_budget')
            if points: lines.append(role + ': ' + ' | '.join(index+'='+value for index, value in points))
            refs = [node.text or '' for node in series.findall('./c:'+role+'//c:f', NS)]
            if refs: lines.append('Source range: ' + ', '.join(refs))
            if refs and not points: writer.reasons.add('chart_cache_unavailable')
    writer.reasons.add('office_visual_layout_unavailable')
    return writer.text('\n'.join(lines), locator)

def assets(archive, part, tree, writer, locator):
    related = relationships(archive, part)
    seen = set()
    for node in tree.iter():
        identifier = node.get(R+'embed') or (node.get(R+'id') if node.tag == '{'+NS['c']+'}chart' else None)
        if not identifier or identifier in seen: continue
        seen.add(identifier)
        if identifier not in related: raise ValueError('missing_asset_relationship')
        target, kind = related[identifier]
        if target is None:
            writer.reasons.add('external_resource_unavailable'); continue
        if kind.endswith('/image'):
            if not embedded_image(archive, target, writer, locator): return False
        elif kind.endswith('/chart'):
            if not chart(archive, target, writer, locator): return False
        else: writer.reasons.add('embedded_object_unavailable')
    if any(node.tag.rsplit('}', 1)[-1] in ('oleObj', 'object', 'pict', 'graphicFrame') for node in tree.iter()):
        # graphicFrame may be a chart/table: content is extracted, layout is not.
        writer.reasons.add('office_visual_layout_unavailable')
    return True

def docx(archive, writer):
    parts = ['word/document.xml'] + sorted(name for name in archive.namelist() if re.fullmatch(r'word/(header[0-9]+|footer[0-9]+|footnotes|endnotes|comments)\.xml', name))
    for part in parts:
        tree = xml(archive, part)
        block, first, last = [], 1, 1
        def flush():
            return writer.text('\n'.join(block), dict(kind='paragraphs', startLine=first, endLine=last, section=PurePosixPath(part).stem))
        for index, paragraph in enumerate(tree.findall('.//w:p', NS), 1):
            text = ''.join((node.text or '') if node.tag == '{'+NS['w']+'}t' else '\t' if node.tag == '{'+NS['w']+'}tab' else '\n' if node.tag == '{'+NS['w']+'}br' else '' for node in paragraph.iter())
            locator = dict(kind='paragraphs', startLine=index, endLine=index, section=PurePosixPath(part).stem)
            if text.strip():
                if not block: first = index
                last = index; block.append(text)
                if len(block) >= 40 or sum(map(len, block)) >= 12000:
                    if not flush(): return
                    block = []
            if not assets(archive, part, paragraph, writer, locator): return
        if block and not flush(): return
    writer.reasons.add('office_visual_layout_unavailable')

def pptx(archive, writer):
    part = 'ppt/presentation.xml'
    tree, related = xml(archive, part), relationships(archive, part)
    for index, slide in enumerate(tree.findall('.//p:sldId', NS), 1):
        target, kind = related[slide.get(R+'id')]
        if target is None or not kind.endswith('/slide'): raise ValueError('invalid_slide_relationship')
        content = xml(archive, target)
        paragraphs = [''.join(node.text or '' for node in paragraph.findall('.//a:t', NS)) for paragraph in content.findall('.//a:p', NS)]
        locator = dict(kind='slide', page=index)
        if not writer.text('\n'.join(paragraphs), locator) or not assets(archive, target, content, writer, locator): return
        notes = [name for name, relation in relationships(archive, target).values() if name and relation.endswith('/notesSlide')]
        for name in notes:
            text = '\n'.join(node.text or '' for node in xml(archive, name).findall('.//a:t', NS))
            if text and not writer.text('Slide notes:\n'+text, locator): return
    writer.reasons.add('office_visual_layout_unavailable')

def cell_position(value):
    match = re.fullmatch(r'([A-Z]{1,3})([1-9][0-9]{0,6})', value or '')
    if not match: raise ValueError('invalid_cell_address')
    col = 0
    for letter in match[1]: col = col*26+ord(letter)-64
    row = int(match[2])
    if col > 16384 or row > 1048576: raise ValueError('sheet_cell_budget')
    return col, row

def xlsx(archive, writer):
    strings = []
    if 'xl/sharedStrings.xml' in archive.namelist():
        strings = [''.join(node.text or '' for node in item.findall('.//s:t', NS)) for item in xml(archive, 'xl/sharedStrings.xml').findall('s:si', NS)]
    part = 'xl/workbook.xml'
    workbook, related = xml(archive, part), relationships(archive, part)
    for sheet in workbook.findall('./s:sheets/s:sheet', NS):
        name = sheet.get('name', '')
        if not 1 <= len(name) <= 200: raise ValueError('sheet_name_budget')
        target, kind = related[sheet.get(R+'id')]
        if target is None or not kind.endswith('/worksheet'): raise ValueError('invalid_sheet_relationship')
        content = xml(archive, target)
        merges = [node.get('ref', '') for node in content.findall('./s:mergeCells/s:mergeCell', NS)]
        for merged in merges:
            if ':' not in merged: raise ValueError('invalid_merged_range')
            for endpoint in merged.split(':'): cell_position(endpoint)
        lines, bounds, heading = [], None, []
        def flush():
            if not lines: return True
            context = 'Sheet: '+name+'\nHeaders: '+' | '.join(heading)+'\nMerged ranges: '+', '.join(merges[:256])+'\n'
            left,top,right,bottom=bounds
            return writer.text(context+'\n'.join(lines), dict(kind='sheet', sheet=name, startCell=column(left)+str(top), endCell=column(right)+str(bottom)))
        count = 0
        for row in content.findall('./s:sheetData/s:row', NS):
            row_lines = []
            for cell in row.findall('s:c', NS):
                count += 1
                if count > 1000000: writer.reasons.add('sheet_cell_limit'); flush(); return
                address = cell.get('r', '')
                col_number,row_number=cell_position(address)
                value = cell.find('s:v', NS)
                cached = value.text or '' if value is not None else ''
                kind = cell.get('t', 'n')
                if kind == 's':
                    if not cached.isdigit() or int(cached) >= len(strings): raise ValueError('invalid_shared_string')
                    cached = strings[int(cached)]
                elif kind == 'inlineStr': cached = ''.join(node.text or '' for node in cell.findall('.//s:t', NS))
                elif kind == 'b':
                    if cached not in ('0', '1'): raise ValueError('invalid_boolean_cell')
                    cached = 'TRUE' if cached == '1' else 'FALSE'
                if cell.find('s:f', NS) is not None and value is None:
                    cached = '[Formula result unavailable]'; writer.reasons.add('formula_cache_unavailable')
                if not cached: continue
                bounds=(col_number,row_number,col_number,row_number) if bounds is None else (min(bounds[0],col_number),min(bounds[1],row_number),max(bounds[2],col_number),max(bounds[3],row_number))
                row_lines.append(address+': '+cached)
            if not heading and row_lines: heading = row_lines.copy()
            lines.extend(row_lines)
            if len(lines) >= 128:
                if not flush(): return
                lines, bounds = [], None
        if not flush(): return
        # Drawing anchors supply sheet/cell evidence for embedded images and charts.
        for drawing in content.findall('./s:drawing', NS):
            drawing_part, relation = relationships(archive, target)[drawing.get(R+'id')]
            if drawing_part is None: writer.reasons.add('external_resource_unavailable'); continue
            drawing_tree = xml(archive, drawing_part)
            for anchor in drawing_tree:
                locator = dict(kind='sheet', sheet=name, startCell='A1', endCell='A1')
                starts = [node for node in anchor if node.tag.rsplit('}', 1)[-1] == 'from']
                if starts:
                    vals = {node.tag.rsplit('}', 1)[-1]: node.text for node in starts[0]}
                    start = column(int(vals.get('col', 0))+1)+str(int(vals.get('row', 0))+1)
                    cell_position(start); locator = dict(locator, startCell=start, endCell=start)
                else:writer.reasons.add('drawing_position_unavailable')
                if not assets(archive, drawing_part, anchor, writer, locator): return
    writer.reasons.add('office_visual_layout_unavailable')

def office(source, writer):
    suffix = Path(source).suffix.lower()
    with Path(source).open('rb') as stream:
        if stream.read(8)==b'\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1':raise ValueError('binary_office_container')
    with package(source) as archive:
        if suffix == '.docx': docx(archive, writer)
        elif suffix == '.pptx': pptx(archive, writer)
        elif suffix == '.xlsx': xlsx(archive, writer)
        elif suffix in ('.odt', '.odp', '.ods'): odf(archive, writer, suffix)
        else: raise ValueError('office_format_not_implemented')

def odf_text(node):
    values = []
    def walk(item):
        if item.text: values.append(item.text)
        for child in item:
            tag = child.tag.rsplit('}', 1)[-1]
            if tag == 's':
                repeat = int(child.get('{'+NS['text']+'}c', '1'))
                if not 1 <= repeat <= 10000: raise ValueError('odf_space_budget')
                values.append(' '*repeat)
            elif tag == 'tab': values.append('\t')
            elif tag == 'line-break': values.append('\n')
            else: walk(child)
            if child.tail: values.append(child.tail)
    walk(node)
    return ''.join(values)

def odf_assets(archive, tree, writer, locator):
    seen = set()
    for node in tree.iter():
        tag = node.tag.rsplit('}', 1)[-1]
        if tag not in ('image', 'object', 'object-ole', 'plugin', 'applet'): continue
        href = node.get('{'+NS['xlink']+'}href', '')
        if not href or href in seen: continue
        seen.add(href)
        if ':' in href or href.startswith(('/', '#')):
            writer.reasons.add('external_resource_unavailable'); continue
        target = href.removeprefix('./')
        member_path(target)
        if tag == 'image' and target in archive.namelist():
            if not embedded_image(archive, target, writer, locator): return False
        elif tag == 'object' and target.rstrip('/')+'/content.xml' in archive.namelist():
            obj = xml(archive, target.rstrip('/')+'/content.xml')
            # Inert embedded chart data, including cached local table values.
            texts = [odf_text(paragraph) for paragraph in obj.findall('.//text:p', NS)]
            values = [value for item in obj.iter() for key,value in item.attrib.items() if key.rsplit('}', 1)[-1] in ('value', 'string-value', 'boolean-value', 'class', 'values-cell-range-address', 'label-cell-address')]
            if not writer.text('Embedded chart/object data:\n'+'\n'.join(texts+values), locator): return False
            writer.reasons.add('office_visual_layout_unavailable')
        else: writer.reasons.add('embedded_object_unavailable')
    return True

def odf(archive, writer, suffix):
    content = xml(archive, 'content.xml')
    if suffix == '.odp':
        for index, page in enumerate(content.findall('.//draw:page', NS), 1):
            locator = dict(kind='slide', page=index)
            text = '\n'.join(odf_text(node) for node in page.iter() if node.tag in ('{'+NS['text']+'}p', '{'+NS['text']+'}h'))
            if not writer.text(text, locator) or not odf_assets(archive, page, writer, locator): return
    elif suffix == '.odt':
        block, first, last = [], 1, 1
        def flush(): return writer.text('\n'.join(block), dict(kind='paragraphs',startLine=first,endLine=last,section='Document'))
        for index, paragraph in enumerate((node for node in content.iter() if node.tag in ('{'+NS['text']+'}p', '{'+NS['text']+'}h')), 1):
            locator = dict(kind='paragraphs', startLine=index, endLine=index, section='Document')
            value = odf_text(paragraph)
            if value.strip():
                if not block: first = index
                last = index; block.append(value)
                if len(block) >= 40 or sum(map(len, block)) >= 12000:
                    if not flush(): return
                    block = []
            if not odf_assets(archive, paragraph, writer, locator): return
        if block and not flush(): return
    else:
        T, O = '{'+NS['table']+'}', '{'+NS['office']+'}'
        for table in content.findall('.//table:table', NS):
            name = table.get(T+'name', '')
            if not 1 <= len(name) <= 200: raise ValueError('sheet_name_budget')
            row_number, nonempty, heading = 1, 0, []
            for row in table.findall('table:table-row', NS):
                repeat = int(row.get(T+'number-rows-repeated', '1'))
                if not 1 <= repeat <= 1048576 or row_number+repeat-1 > 1048576: raise ValueError('sheet_row_budget')
                values, col = [], 1
                for cell in row:
                    if cell.tag not in (T+'table-cell', T+'covered-table-cell'): continue
                    repetitions = int(cell.get(T+'number-columns-repeated', '1'))
                    if not 1 <= repetitions <= 16384 or col+repetitions-1 > 16384: raise ValueError('sheet_column_budget')
                    cached = cell.get(O+'value') or cell.get(O+'boolean-value') or cell.get(O+'date-value') or cell.get(O+'time-value') or cell.get(O+'string-value')
                    text = '\n'.join(odf_text(node) for node in cell.findall('text:p', NS))
                    value = text or cached or ''
                    if cell.get(T+'formula') and cached is None and not text:
                        value = '[Formula result unavailable]'; writer.reasons.add('formula_cache_unavailable')
                    span_columns, span_rows = int(cell.get(T+'number-columns-spanned', '1')), int(cell.get(T+'number-rows-spanned', '1'))
                    if not 1 <= span_columns <= 16384 or not 1 <= span_rows <= 1048576 or col+repetitions+span_columns-2>16384 or row_number+repeat+span_rows-2>1048576: raise ValueError('merged_cell_budget')
                    if value:
                        for offset in range(repetitions):
                            values.append((col+offset, value, span_columns, span_rows))
                    col += repetitions
                if not values:
                    row_number += repeat; continue
                if len(values)*repeat+nonempty > 100000:
                    writer.reasons.add('sheet_cell_limit'); return
                for row_offset in range(repeat):
                    current = row_number+row_offset
                    lines = [f'{column(c)}{current}: {v}' + (f' [merged {wide} columns × {high} rows]' if wide > 1 or high > 1 else '') for c,v,wide,high in values]
                    if not heading: heading = lines.copy()
                    locator = dict(kind='sheet', sheet=name, startCell=column(values[0][0])+str(current), endCell=column(values[-1][0])+str(current))
                    if not writer.text('Sheet: '+name+'\nHeaders: '+' | '.join(heading)+'\n'+'\n'.join(lines), locator): return
                    nonempty += len(values)
                row_number += repeat
            locator = dict(kind='sheet', sheet=name, startCell='A1', endCell='A1')
            if not odf_assets(archive, table, writer, locator): return
    writer.reasons.add('office_visual_layout_unavailable')
