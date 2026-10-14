"""Bounded inert Office ZIP/XML access; no formulas, links or object code execute."""
from contextlib import contextmanager
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

@contextmanager
def package(path):
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
            if not entry.is_dir() and entry.filename.lower().endswith(('.xml', '.rels')): xml(archive, entry.filename)
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
