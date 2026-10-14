"""Content-grounded contracts for the bounded renderer-free document readers."""
import io
import json
from pathlib import Path
import tempfile
import unittest
import zipfile
from types import SimpleNamespace

from PIL import Image
from semantic_search.packages import package, relationships
from semantic_search.readers import SegmentWriter, plain, images, pdf, csv_sheet, text_chunks
from semantic_search.office_readers import office, cell_position
from semantic_search.protocol import decode_request

ROOT = Path(__file__).resolve().parents[1]
TEST_ROOT = ROOT/'artifacts/diagnostics/multimodal/reader-contracts'
def tokens(text):
    return SimpleNamespace(ids=list(range(len(text))), offsets=[(i,i+1) for i in range(len(text))])
def entries(path, values):
    with zipfile.ZipFile(path, 'w') as archive:
        for name,value in values.items(): archive.writestr(name,value)
def content(writer):
    return [json.loads((writer.output/item['name']).read_text(encoding='utf-8')) for item in writer.files]

class Readers(unittest.TestCase):
    @classmethod
    def setUpClass(cls): TEST_ROOT.mkdir(parents=True, exist_ok=True)
    def test_supported_image_formats_and_transparency_retain_visible_pixels(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            for suffix in ('png','jpg','webp','bmp','tiff','gif'):
                with self.subTest(suffix=suffix):
                    source=Path(folder)/('picture.'+suffix);Image.new('RGB',(320,180),'red').save(source)
                    writer=SegmentWriter(Path(folder)/suffix,tokens);images(source,writer)
                    prepared,_,_=decode_request(dict(schemaVersion=1,mode='document',content=content(writer)[0]['content']))
                    self.assertEqual(prepared['image'].size,(320,180));self.assertGreater(prepared['image'].getpixel((30,30))[0],200)
            source=Path(folder)/'transparent.png';image=Image.new('RGBA',(320,180),(0,0,0,0));image.putpixel((50,50),(255,0,0,255));image.save(source)
            writer=SegmentWriter(Path(folder)/'transparent',tokens);images(source,writer)
            prepared,_,_=decode_request(dict(schemaVersion=1,mode='document',content=content(writer)[0]['content']))
            self.assertTrue(all(value>245 for value in prepared['image'].getpixel((0,0))))
    def test_encrypted_odf_is_classified_before_decoding_ciphertext(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'locked.odt'
            entries(source,{'META-INF/manifest.xml':'<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"><manifest:file-entry manifest:full-path="content.xml"><manifest:encryption-data/></manifest:file-entry></manifest:manifest>',
                            'content.xml':b'\x00encrypted bytes are not XML\xff'})
            with self.assertRaisesRegex(ValueError,'encrypted_odf'):
                office(source,SegmentWriter(Path(folder)/'out',tokens))
    def test_binary_office_container_is_not_treated_as_a_zip(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'locked.docx';source.write_bytes(b'\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1'+b'authored binary container')
            with self.assertRaisesRegex(ValueError,'binary_office_container'):
                office(source,SegmentWriter(Path(folder)/'out',tokens))
    def test_sparse_sheet_locator_encloses_all_matching_cells(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'sparse.xlsx';ns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'
            entries(source,{'xl/workbook.xml':f'<workbook xmlns="{ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sparse" r:id="s"/></sheets></workbook>',
                'xl/_rels/workbook.xml.rels':'<Relationships><Relationship Id="s" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
                'xl/worksheets/sheet1.xml':f'<worksheet xmlns="{ns}"><sheetData><row r="1"><c r="Z1"><v>120</v></c></row><row r="2"><c r="A2"><v>42000</v></c></row></sheetData></worksheet>'})
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            self.assertEqual(content(writer)[0]['locator'],dict(kind='sheet',sheet='Sparse',startCell='A1',endCell='Z2'))
            self.assertIn('Z1: 120',content(writer)[0]['evidence']);self.assertIn('A2: 42000',content(writer)[0]['evidence'])
    def test_token_aware_chunks_preserve_all_text(self):
        source = 'مرحبا '+'alpha\n'*1000
        chunks = list(text_chunks(source,tokens,limit=257))
        self.assertEqual(''.join(value for value,_,_ in chunks),source)
        self.assertTrue(all(len(tokens(value).ids) <=257 for value,_,_ in chunks))
    def test_source_lines_and_partial_segment_budget_are_explicit(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source = Path(folder)/'source.py'; source.write_text(''.join(f'line {i}\n' for i in range(1,101)),encoding='utf-8')
            writer = SegmentWriter(Path(folder)/'out',tokens,max_segments=1)
            plain(source,writer); result = writer.finish(source)
            self.assertTrue(result['partial']); self.assertIn('segment_limit',result['reasons'])
            segment = content(writer)[0]
            self.assertEqual(segment['locator'],dict(kind='lines',startLine=1,endLine=40))
            self.assertIn('line 40',segment['evidence']); self.assertNotIn('line 41',segment['evidence'])
    def test_multi_page_images_are_prepared_as_separate_frames(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source = Path(folder)/'scan.tiff'
            Image.new('RGB',(400,200),'red').save(source,save_all=True,append_images=[Image.new('RGB',(400,200),'blue')])
            writer = SegmentWriter(Path(folder)/'out',tokens); images(source,writer)
            self.assertEqual([item['locator']['page'] for item in content(writer)],[1,2])
            for item in content(writer): decode_request(dict(schemaVersion=1,mode='document',content=item['content']))
    def test_csv_preserves_numeric_boolean_headers_and_addresses(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source = Path(folder)/'unknown.csv'; source.write_text('Region,Revenue,Active\nNorth,42000,TRUE\nSouth,17000,FALSE\n',encoding='utf-8')
            writer = SegmentWriter(Path(folder)/'out',tokens); csv_sheet(source,writer)
            text = '\n'.join(item['evidence'] for item in content(writer))
            for expected in ('Headers: Region | Revenue | Active','B2: 42000','C3: FALSE','A3: South'): self.assertIn(expected,text)
            self.assertEqual(content(writer)[0]['locator']['endCell'],'C3')
    def test_prepared_output_quota_does_not_publish_a_truncated_segment(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            writer = SegmentWriter(Path(folder)/'out',tokens,max_bytes=10)
            self.assertFalse(writer.text('a paragraph',dict(kind='paragraphs',startLine=1,endLine=1)))
            self.assertEqual(writer.files,[]); self.assertEqual(writer.reasons,{'prepared_output_limit'})
    def test_packages_reject_entities_duplicate_members_and_traversal(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source = Path(folder)/'bad.docx'
            for members in ({'../private':'data'},{'a.xml':'<!DOCTYPE a [<!ENTITY x "secret">]><a>&x;</a>'},{'a.xml':'<a/>','A.xml':'<a/>'}):
                entries(source,members)
                with self.assertRaises(ValueError):
                    with package(source): pass
    def test_relationships_are_canonical_and_never_fetch_external_targets(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source = Path(folder)/'links.docx'
            entries(source,{'word/document.xml':'<doc/>','word/media/a b.png':b'bytes','word/_rels/document.xml.rels':
                '<Relationships><Relationship Id="image" Type="image" Target="media/a%20b.png"/><Relationship Id="external" Type="image" TargetMode="External" Target="https://example.com/private"/></Relationships>'})
            with package(source) as archive:
                related = relationships(archive,'word/document.xml')
                self.assertEqual(related['image'][0],'word/media/a b.png'); self.assertIsNone(related['external'][0])
    def test_spreadsheet_cached_formula_inline_string_boolean_and_merge(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source = Path(folder)/'unknown.xlsx'
            ns = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
            entries(source,{'xl/workbook.xml':f'<workbook xmlns="{ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sales" r:id="s"/></sheets></workbook>',
                'xl/_rels/workbook.xml.rels':'<Relationships><Relationship Id="s" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
                'xl/sharedStrings.xml':f'<sst xmlns="{ns}"><si><t>Revenue</t></si></sst>',
                'xl/worksheets/sheet1.xml':f'<worksheet xmlns="{ns}"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c></row><row r="2"><c r="A2"><f>SUM(B2:C2)</f><v>42000</v></c><c r="B2" t="inlineStr"><is><t>North</t></is></c><c r="C2" t="b"><v>1</v></c><c r="D2"><f>FETCHDATA()</f></c></row></sheetData><mergeCells><mergeCell ref="A1:D1"/></mergeCells></worksheet>'})
            writer = SegmentWriter(Path(folder)/'out',tokens); office(source,writer)
            text = '\n'.join(item['evidence'] for item in content(writer))
            for value in ('Revenue','A2: 42000','B2: North','C2: TRUE','Formula result unavailable','A1:D1'): self.assertIn(value,text)
            self.assertIn('formula_cache_unavailable',writer.reasons)
            for address in ('XFE1','A1048577','0','A0'):
                with self.assertRaises(ValueError): cell_position(address)
    def test_odf_numeric_boolean_formula_repetition_and_merged_heading(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source = Path(folder)/'unknown.ods'
            entries(source,{'content.xml':'''<office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"><table:table table:name="Sales"><table:table-row><table:table-cell table:number-columns-spanned="2"><text:p>Revenue</text:p></table:table-cell></table:table-row><table:table-row table:number-rows-repeated="2"><table:table-cell office:value="42000" table:formula="of:=SUM(A1:A2)"/><table:table-cell office:boolean-value="false"/></table:table-row></table:table></office:document-content>'''})
            writer = SegmentWriter(Path(folder)/'out',tokens); office(source,writer)
            text = '\n'.join(item['evidence'] for item in content(writer))
            for value in ('merged 2 columns','A2: 42000','A3: 42000','B3: false'): self.assertIn(value,text)
    def test_authored_office_pack_extracts_all_six_formats_without_renderer(self):
        pack = ROOT/'artifacts/diagnostics/multimodal/office-fixtures-v2'
        if not (pack/'fixtures.json').is_file(): self.skipTest('Author the development Office fixture pack first')
        expected = json.loads((pack/'fixtures.json').read_text())['items']
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            for index,item in enumerate(expected):
                with self.subTest(format=Path(item['file']).suffix):
                    writer = SegmentWriter(Path(folder)/str(index),tokens); office(pack/item['file'],writer)
                    text = '\n'.join(piece['evidence'] for piece in content(writer))
                    for value in item['expectedText']: self.assertIn(value,text)
                    self.assertTrue(writer.files)

if __name__ == '__main__': unittest.main()
