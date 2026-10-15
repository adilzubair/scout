"""Inert Office content extraction without Office, LibreOffice or formula evaluation."""
import io
from contextlib import closing
from collections import OrderedDict
from pathlib import Path, PurePosixPath
import re
import xml.etree.ElementTree as ET

from .packages import package, relationships, member_path, xml, xml_stream
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

def sheet_range(value, reason):
    match = re.fullmatch(r'(\$?[A-Z]{1,3}\$?[1-9][0-9]{0,6})(?::(\$?[A-Z]{1,3}\$?[1-9][0-9]{0,6}))?', value or '')
    if not match: raise ValueError(reason)
    try:
        left,top = cell_position(match[1].replace('$',''))
        right,bottom = cell_position((match[2] or match[1]).replace('$',''))
    except ValueError: raise ValueError(reason) from None
    if left>right or top>bottom: raise ValueError(reason)
    return left,top,right,bottom


def range_name(bounds):
    left,top,right,bottom = bounds
    return column(left)+str(top)+':'+column(right)+str(bottom)

def table_count(value, reason):
    value=value.strip()
    if not re.fullmatch(r'\+?[0-9]{1,10}',value) or int(value)>4294967295:raise ValueError(reason)
    return int(value)


class XlsxTable:
    def __init__(self, tree):
        if tree.tag != '{'+NS['s']+'}table': raise ValueError('invalid_table_definition')
        self.bounds = sheet_range(tree.get('ref'),'invalid_table_range')
        self.name = tree.get('displayName') or tree.get('name') or '[unnamed]'
        if not 1 <= len(self.name) <= 255: raise ValueError('table_name_budget')
        self.internal_name = tree.get('name',self.name)
        if not 1 <= len(self.internal_name) <= 255: raise ValueError('table_name_budget')
        self.columns = tree.findall('./s:tableColumns/s:tableColumn',NS)
        definition = tree.find('s:tableColumns',NS)
        if len(self.columns)!=self.bounds[2]-self.bounds[0]+1 or definition is None or table_count(definition.get('count',str(len(self.columns))),'table_column_mismatch')!=len(self.columns):
            raise ValueError('table_column_mismatch')
        if any(not node.get('name') for node in self.columns): raise ValueError('invalid_table_column')
        self.header_rows=table_count(tree.get('headerRowCount','1'),'invalid_table_row_counts')
        self.total_rows=table_count(tree.get('totalsRowCount','0'),'invalid_table_row_counts')
        if self.header_rows+self.total_rows>self.bounds[3]-self.bounds[1]+1: raise ValueError('invalid_table_row_counts')
        self.headers, self.header_chars = [], 0
    def describe(self, left=None, right=None):
        left = self.bounds[0] if left is None else left
        right = self.bounds[2] if right is None else right
        names = [column(self.bounds[0]+i)+': '+node.get('name') for i,node in enumerate(self.columns) if left<=self.bounds[0]+i<=right]
        return f'Declared table: {self.name}\nRange: {range_name(self.bounds)}\nHeader rows: {self.header_rows}\nTotals rows: {self.total_rows}\nColumns: '+' | '.join(names)+'\n'
    def schema(self):
        text = self.describe()
        if self.internal_name!=self.name: text+='Internal table name: '+self.internal_name+'\n'
        for i,node in enumerate(self.columns):
            for tag,label in (('calculatedColumnFormula','Calculated formula (not evaluated)'),('totalsRowFormula','Totals formula (not evaluated)')):
                formula = node.find('s:'+tag,NS)
                if formula is not None and formula.text: text+=f'{column(self.bounds[0]+i)} {label}: '+formula.text+'\n'
            if node.get('totalsRowFunction'): text+=column(self.bounds[0]+i)+' Totals function: '+node.get('totalsRowFunction')+'\n'
        return text


class XlsxContext:
    def __init__(self, archive, target, tree, writer):
        self.writer, self.tables, self.merges = writer, [], {}
        for node in tree.findall('./s:mergeCells/s:mergeCell',NS):
            bounds = sheet_range(node.get('ref'),'invalid_merged_range')
            anchor = column(bounds[0])+str(bounds[1])
            if anchor in self.merges: raise ValueError('duplicate_merged_anchor')
            if len(self.merges)<4096: self.merges[anchor]=bounds
            else: writer.reasons.add('sheet_merge_limit')
        related = relationships(archive,target)
        seen = set()
        for part in tree.findall('./s:tableParts/s:tablePart',NS):
            identifier = part.get(R+'id')
            if identifier not in related: raise ValueError('invalid_table_relationship')
            name,kind = related[identifier]
            if not kind.endswith('/table'): raise ValueError('invalid_table_relationship')
            if name is None: writer.reasons.add('external_resource_unavailable'); continue
            if name in seen: raise ValueError('duplicate_table_relationship')
            seen.add(name)
            if len(self.tables)>=256: writer.reasons.add('sheet_table_limit'); continue
            table = XlsxTable(xml(archive,name))
            l,t,r,b = table.bounds
            if any(not (r<old.bounds[0] or l>old.bounds[2] or b<old.bounds[1] or t>old.bounds[3]) for old in self.tables): raise ValueError('overlapping_table_ranges')
            self.tables.append(table)
        self.leading, self.leading_chars, self.leading_rows, self.leading_done = [], 0, 0, False
    def bounded(self, entries, chars, value):
        line = value[2]
        if len(entries)>=128 or chars>=8192:
            self.writer.reasons.add('sheet_context_limit'); return chars
        room = 8192-chars
        if len(line)>room: self.writer.reasons.add('sheet_context_limit')
        entries.append((value[0],value[1],line[:room],value[3])); return chars+min(len(line),room)
    def leading_row(self, values):
        if not values or self.leading_done: return
        if self.leading_rows>=8:
            self.writer.reasons.add('sheet_context_limit'); self.leading_done=True; return
        self.leading_rows+=1
        for address,col,row,line in values:
            merged = self.merges.get(address)
            self.leading_chars=self.bounded(self.leading,self.leading_chars,(col,merged[2] if merged else col,line,merged is not None))
        self.leading_done = not any(address in self.merges for address,_,_,_ in values)
    def owner(self, col, row):
        return next((i for i,table in enumerate(self.tables) if table.bounds[0]<=col<=table.bounds[2] and table.bounds[1]<=row<=table.bounds[3]),None)
    def key(self, owner, col):
        if not self.tables and not self.merges: return None,None
        start = self.tables[owner].bounds[0] if owner is not None else 1
        return owner,(col-start)//8
    def header(self, owner, address, col, row, line):
        if owner is None: return
        table = self.tables[owner]
        if row>=table.bounds[1]+table.header_rows: return
        merged = self.merges.get(address)
        table.header_chars=self.bounded(table.headers,table.header_chars,(col,merged[2] if merged else col,line,True))
    def context(self, name, key):
        owner,group = key
        if group is None: left,right=1,16384
        else:
            left=(self.tables[owner].bounds[0] if owner is not None else 1)+group*8
            right=min(left+7,self.tables[owner].bounds[2] if owner is not None else 16384)
        text='Sheet: '+name+'\n'
        if owner is not None:
            table=self.tables[owner];text+=table.describe(left,right)
            headers=[line for start,end,line,_ in table.headers if start<=right and end>=left]
            if headers:text+='Declared header cells: '+' | '.join(headers)+'\n'
            leading=[line for start,end,line,merged in self.leading if merged and start<=right and end>=left]
        else:leading=[line for start,end,line,_ in self.leading if start<=right and end>=left]
        if leading:text+='Leading sheet rows: '+' | '.join(leading)+'\n'
        return text


def xlsx_value(cell, strings, writer):
    address=cell.get('r','');col,row=cell_position(address)
    value=cell.find('s:v',NS);cached=(value.text or '') if value is not None else ''
    kind=cell.get('t','n')
    if kind=='s':
        if not cached.isdigit() or int(cached)>=len(strings): raise ValueError('invalid_shared_string')
        cached=strings[int(cached)]
    elif kind=='inlineStr':cached=''.join(node.text or '' for node in cell.findall('.//s:t',NS))
    elif kind=='b':
        if cached not in ('0','1'):raise ValueError('invalid_boolean_cell')
        cached='TRUE' if cached=='1' else 'FALSE'
    if cell.find('s:f',NS) is not None and value is None:
        cached='[Formula result unavailable]';writer.reasons.add('formula_cache_unavailable')
    return address,col,row,cached


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
        context = XlsxContext(archive,target,content,writer)
        for table in context.tables:
            l,t,r,b=table.bounds
            if not writer.text('Sheet: '+name+'\n'+table.schema(),dict(kind='sheet',sheet=name,startCell=column(l)+str(t),endCell=column(r)+str(b))):return
        buckets, pending, count = OrderedDict(), 0, 0
        def flush(key):
            nonlocal pending
            lines=buckets.pop(key);pending-=len(lines)
            return writer.table(context.context(name,key),lines,dict(kind='sheet',sheet=name))
        for row in content.findall('./s:sheetData/s:row', NS):
            values=[]
            for cell in row.findall('s:c', NS):
                count += 1
                if count>1000000:
                    writer.reasons.add('sheet_cell_limit')
                    for key in list(buckets):
                        if not flush(key):return
                    return
                address,col_number,row_number,cached=xlsx_value(cell,strings,writer)
                if not cached: continue
                merged=context.merges.get(address)
                line=address+': '+cached+((' [merged '+range_name(merged)+']') if merged else '')
                values.append((address,col_number,row_number,line))
            context.leading_row(values)
            for address,col_number,row_number,line in values:
                owner=context.owner(col_number,row_number);context.header(owner,address,col_number,row_number,line)
                key=context.key(owner,col_number);buckets.setdefault(key,[]).append(line);pending+=1
                if key!=(None,None) and len(buckets[key])>=128 and not flush(key):return
                if key!=(None,None) and pending>=4096 and not flush(next(iter(buckets))):return
            # Plain sheets retain the previous whole-row batching. Scoped column
            # groups only apply when tables or merged relationships require them.
            if (None,None) in buckets and len(buckets[(None,None)])>=128:
                if not flush((None,None)):return
        for key in list(buckets):
            if not flush(key):return
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
    with package(source, streaming_xml=('content.xml',) if suffix == '.ods' else ()) as archive:
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

def odf_assets(archive, tree, writer, locator, *, seen=None):
    if seen is None: seen = set()
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

class SheetHeadings:
    """Bound context separately from the original cells, which are still emitted."""
    def __init__(self, writer, parent=None):
        self.writer = writer
        self.lines = list(parent.lines) if parent else []
        self.chars = parent.chars if parent else 0
        self.declared = parent.declared if parent else False
    def add(self, lines, declared):
        if declared and not self.declared:
            self.lines, self.chars = [], 0
        elif not declared and self.lines:
            return
        self.declared |= declared
        for line in lines:
            room = 8192-self.chars
            if len(self.lines) >= 128 or room <= 0:
                self.writer.reasons.add('sheet_context_limit'); break
            if len(line) > room: self.writer.reasons.add('sheet_context_limit')
            self.lines.append(line[:room]); self.chars += len(self.lines[-1])
    def context(self, name):
        label = 'Declared header rows' if self.declared else 'Leading row context'
        return 'Sheet: '+name+'\n'+label+': '+' | '.join(self.lines)+'\n'


class OdsSheet:
    def __init__(self, name):
        if not 1 <= len(name) <= 200: raise ValueError('sheet_name_budget')
        self.name, self.row_number, self.nonempty, self.seen = name, 1, 0, set()
    def locator(self, row=None):
        return dict(kind='sheet', sheet=self.name, startCell='A'+str(row or 1), endCell='A'+str(row or 1))
    def row(self, archive, node, writer, header, headings):
        T, O = '{'+NS['table']+'}', '{'+NS['office']+'}'
        repeat = int(node.get(T+'number-rows-repeated', '1'))
        if not 1 <= repeat <= 1048576 or self.row_number+repeat-1 > 1048576: raise ValueError('sheet_row_budget')
        values, cells, col = [], [], 1
        for cell in node:
            if cell.tag not in (T+'table-cell', T+'covered-table-cell'): continue
            repetitions = int(cell.get(T+'number-columns-repeated', '1'))
            if not 1 <= repetitions <= 16384 or col+repetitions-1 > 16384: raise ValueError('sheet_column_budget')
            cached = cell.get(O+'value') or cell.get(O+'boolean-value') or cell.get(O+'date-value') or cell.get(O+'time-value') or cell.get(O+'string-value')
            text = '\n'.join(odf_text(paragraph) for paragraph in cell.findall('text:p', NS))
            value = text or cached or ''
            if cell.get(T+'formula') and cached is None and not text:
                value = '[Formula result unavailable]'; writer.reasons.add('formula_cache_unavailable')
            span_columns, span_rows = int(cell.get(T+'number-columns-spanned', '1')), int(cell.get(T+'number-rows-spanned', '1'))
            if not 1 <= span_columns <= 16384 or not 1 <= span_rows <= 1048576 or col+repetitions+span_columns-2>16384 or self.row_number+repeat+span_rows-2>1048576: raise ValueError('merged_cell_budget')
            cells.append((cell,col,repetitions,span_columns,span_rows))
            if value:
                for offset in range(repetitions): values.append((col+offset, value, span_columns, span_rows))
            col += repetitions
        if len(values)*repeat+self.nonempty > 100000:
            writer.reasons.add('sheet_cell_limit'); return False
        location = self.locator(self.row_number)
        if values:
            for row_offset in range(repeat):
                current = self.row_number+row_offset
                lines = [f'{column(c)}{current}: {value}' + (f' [merged {wide} columns × {high} rows]' if wide > 1 or high > 1 else '') for c,value,wide,high in values]
                headings.add(lines, header)
                location = dict(kind='sheet', sheet=self.name, startCell=column(values[0][0])+str(current), endCell=column(values[-1][0])+str(current))
                if not writer.table(headings.context(self.name), lines, location): return False
                self.nonempty += len(values)
        first, last = self.row_number, self.row_number+repeat-1
        self.row_number += repeat
        for cell,start,repetitions,wide,high in cells:
            location = dict(kind='sheet',sheet=self.name,startCell=column(start)+str(first),endCell=column(start+repetitions+wide-2)+str(last+high-1))
            if not odf_assets(archive,cell,writer,location,seen=self.seen): return False
        for child in node:
            if child.tag not in (T+'table-cell',T+'covered-table-cell'):
                if not odf_assets(archive,child,writer,self.locator(first),seen=self.seen): return False
        return True


def ods(archive, writer):
    """Keep at most a bounded row subtree; remove completed nodes from parents."""
    T = '{'+NS['table']+'}'
    stack, containers, sheet, row = [], [], None, None
    position = row_start = row_nodes = 0
    with closing(xml_stream(archive, 'content.xml')) as events:
        for event, node in events:
            if event == 'bytes':
                position = node
                if row is not None and position-row_start > 8*1024**2: raise ValueError('sheet_row_byte_budget')
                continue
            if event == 'start':
                parent = stack[-1] if stack else None; stack.append(node)
                if row is not None:
                    row_nodes += 1
                    if row_nodes > 100000: raise ValueError('sheet_row_node_budget')
                    if node.tag == T+'table': raise ValueError('nested_odf_table_unavailable')
                elif node.tag == T+'table':
                    if sheet is not None: raise ValueError('nested_odf_table_unavailable')
                    sheet = OdsSheet(node.get(T+'name', '')); containers = [(node,False,SheetHeadings(writer))]
                elif sheet is not None and parent is containers[-1][0]:
                    _, header, headings = containers[-1]
                    if node.tag == T+'table-row': row, row_start, row_nodes = node, position, 1
                    elif node.tag in (T+'table-header-rows',T+'table-rows',T+'table-row-group'):
                        if len(containers) >= 64: raise ValueError('odf_row_nesting_budget')
                        scoped = SheetHeadings(writer,headings) if node.tag == T+'table-row-group' else headings
                        containers.append((node,header or node.tag == T+'table-header-rows',scoped))
                continue
            if row is node:
                _, header, headings = containers[-1]
                if not sheet.row(archive, node, writer, header, headings): return
                row = None
            elif row is not None:
                stack.pop(); continue
            elif sheet is not None:
                if node is containers[-1][0]:
                    containers.pop()
                    if not containers: sheet = None
                elif node.tag.rsplit('}',1)[-1] in ('image','object','object-ole','plugin','applet'):
                    if not odf_assets(archive,node,writer,sheet.locator(),seen=sheet.seen): return
            node.clear()
            if len(stack)>1: stack[-2].remove(node)
            stack.pop()
    writer.reasons.add('office_visual_layout_unavailable')


def odf(archive, writer, suffix):
    if suffix == '.ods': return ods(archive,writer)
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
    writer.reasons.add('office_visual_layout_unavailable')
