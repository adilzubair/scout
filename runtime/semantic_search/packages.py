"""Bounded inert Office ZIP/XML access; no formulas, links or object code execute."""
from contextlib import closing, contextmanager
from pathlib import PurePosixPath
import re
import xml.etree.ElementTree as ET
import zipfile
from urllib.parse import unquote

def member_path(name):
    path = PurePosixPath(name)
    if not name or path.is_absolute() or '\\' in name or path.as_posix() != name or any(part in ('.', '..') or ':' in part for part in path.parts):
        raise ValueError('unsafe_package_member')
    return path

def xml(archive, name):
    info = archive.getinfo(name)
    if info.file_size > 8*1024**2: raise ValueError('xml_budget')
    data = archive.read(info)
    declaration = data.replace(b'\0', b'').upper()
    if b'<!DOCTYPE' in declaration or b'<!ENTITY' in declaration: raise ValueError('xml_entities')
    return ET.fromstring(data)

def xml_chunks(archive, name, limit):
    info = archive.getinfo(name)
    if info.file_size > limit: raise ValueError('xml_budget')
    count = 0
    with archive.open(info) as stream:
        while data := stream.read(65536):
            count += len(data)
            if count > limit: raise ValueError('xml_budget')
            yield data
    if count != info.file_size: raise ValueError('xml_size_mismatch')


class XmlValidator:
    """Validate the complete stream without retaining an Element tree."""
    def __init__(self): self.depth, self.nodes = 0, 0
    def start(self, tag, attributes):
        self.depth += 1; self.nodes += 1
        if self.depth > 256: raise ValueError('xml_nesting_budget')
        if self.nodes > 4000000: raise ValueError('xml_node_budget')
    def end(self, tag): self.depth -= 1
    def data(self, value): pass
    def doctype(self, name, public_id, system_id): raise ValueError('xml_entities')
    def close(self): pass


def validate_xml_stream(archive, name):
    parser = ET.XMLParser(target=XmlValidator())
    with closing(xml_chunks(archive, name, 64*1024**2)) as chunks:
        for data in chunks: parser.feed(data)
    parser.close()


def xml_stream(archive, name):
    # Only package() may authorize a member, after full EOF/DTD/CRC validation.
    # Chunk markers let a consumer bound a live row even during one giant text node.
    if name not in getattr(archive, '_scout_xml_streams', ()):
        raise ValueError('unvalidated_xml_stream')
    parser = ET.XMLPullParser(events=('start','end'))
    count = 0
    with closing(xml_chunks(archive, name, 64*1024**2)) as chunks:
        for data in chunks:
            count += len(data); yield 'bytes', count
            parser.feed(data)
            yield from parser.read_events()
        parser.close()
        yield from parser.read_events()


@contextmanager
def package(path, *, streaming_xml=()):
    streaming_xml = frozenset(streaming_xml)
    if streaming_xml - {'content.xml'}: raise ValueError('invalid_streaming_member')
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        if len(entries) > 10000 or sum(entry.file_size for entry in entries) > 256*1024**2: raise ValueError('archive_budget')
        seen = set()
        for entry in entries:
            member_path(entry.filename.rstrip('/'))
            name = entry.filename.casefold()
            if entry.flag_bits & 1:raise ValueError('encrypted_archive_entry')
            if name in seen or entry.file_size > 64*1024**2: raise ValueError('unsafe_package_entry')
            seen.add(name)
            if any(part in name for part in ('vbaproject', '/activex/', 'scripts/', 'basic/', 'encryptedpackage')):
                raise ValueError('active_or_encrypted_package')
        # ODF encrypts individual streams, not ZIP entries. Inspect the inert
        # manifest before attempting to decode an encrypted content.xml stream.
        if 'META-INF/manifest.xml' in archive.namelist():
            manifest=xml(archive,'META-INF/manifest.xml')
            if any(node.tag=='{urn:oasis:names:tc:opendocument:xmlns:manifest:1.0}encryption-data' for node in manifest.iter()):
                raise ValueError('encrypted_odf')
        for entry in entries:
            if not entry.is_dir() and entry.filename.lower().endswith(('.xml', '.rels')):
                if entry.filename in streaming_xml: validate_xml_stream(archive, entry.filename)
                else: xml(archive, entry.filename)
        archive._scout_xml_streams = streaming_xml
        yield archive

def relationships(archive, part):
    path = member_path(part)
    relation_part = str(path.parent / '_rels' / (path.name + '.rels'))
    if relation_part not in archive.namelist(): return {}
    result = {}
    for node in xml(archive, relation_part):
        identifier, target = node.get('Id'), unquote(node.get('Target', ''), errors='strict')
        if not identifier or identifier in result: raise ValueError('duplicate_relationship')
        if node.get('TargetMode', '').casefold() == 'external' or node.get('Type', '').endswith('/hyperlink'):
            # A link is never fetched. Readers label unsupported external resources.
            result[identifier] = (None, node.get('Type', '')); continue
        if '\\' in target or ':' in target or target.startswith('//'): raise ValueError('unsafe_relationship')
        components = [] if target.startswith('/') else list(path.parent.parts)
        for component in target.split('/'):
            if component in ('', '.'): continue
            if component == '..':
                if not components: raise ValueError('escaped_relationship')
                components.pop()
            else: components.append(component)
        resolved = '/'.join(components)
        member_path(resolved)
        if resolved not in archive.namelist(): raise ValueError('missing_relationship')
        result[identifier] = (resolved, node.get('Type', ''))
    return result
