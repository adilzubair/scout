"""Author small inert unsupported/encrypted/corrupt fixtures; never a quality holdout."""
import hashlib
import json
from pathlib import Path
import struct
import zipfile
import zlib
from pypdf import PdfWriter

ROOT=Path(__file__).resolve().parents[1]/'artifacts/diagnostics/multimodal/format-fixtures-v1'
def author():
    ROOT.mkdir(parents=True,exist_ok=True);marker=ROOT/'fixtures.json'
    if marker.is_file():
        data=json.loads(marker.read_text())
        if data.get('schemaVersion')==1 and all((ROOT/item['file']).is_file() and hashlib.sha256((ROOT/item['file']).read_bytes()).hexdigest()==item['sha256'] for item in data['items']):return ROOT
    def package(name,entries):
        with zipfile.ZipFile(ROOT/name,'w') as archive:
            for part,value in entries.items():archive.writestr(part,value)
    (ROOT/'old.doc').write_bytes(b'authored-legacy-body-not-read')
    package('bundle.zip',{'private.txt':'archive member body must not be read'})
    package('macro.xlsm',{'xl/vbaProject.bin':b'inert macro fixture'})
    (ROOT/'drawing.svg').write_text('<svg xmlns="http://www.w3.org/2000/svg"><image href="https://example.invalid/private"/></svg>')
    package('locked.odt',{'META-INF/manifest.xml':'<m:manifest xmlns:m="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"><m:file-entry m:full-path="content.xml"><m:encryption-data/></m:file-entry></m:manifest>','content.xml':b'\x00inert ciphertext\xff'})
    (ROOT/'binary.docx').write_bytes(b'\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1'+b'inert binary container')
    pdf=PdfWriter();pdf.add_blank_page(300,400);pdf.encrypt('authored-password')
    with (ROOT/'locked.pdf').open('wb') as output:pdf.write(output)
    (ROOT/'broken.png').write_bytes(b'not an image')
    def chunk(kind,data):return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data))
    (ROOT/'huge.png').write_bytes(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',30000,30000,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(b'\0'))+chunk(b'IEND',b''))
    values=[('old.doc','legacy_office_format'),('bundle.zip','archive_contents_unavailable'),('macro.xlsm','macro_enabled_office_format'),('drawing.svg','image_decoder_unavailable'),
            ('locked.odt','encrypted_odf'),('binary.docx','binary_office_container'),('locked.pdf','encrypted_pdf'),('broken.png','malformed_image'),('huge.png','image_pixel_budget')]
    marker.write_text(json.dumps(dict(schemaVersion=1,items=[dict(file=name,reason=reason,sha256=hashlib.sha256((ROOT/name).read_bytes()).hexdigest()) for name,reason in values])),encoding='utf-8')
    return ROOT
if __name__=='__main__':print(author())
