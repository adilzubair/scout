"""Content-grounded contracts for the bounded renderer-free document readers."""
import io
import importlib
import json
import platform
from pathlib import Path
import tempfile
import sys
import unittest
from unittest.mock import patch
import zipfile
from types import SimpleNamespace

from PIL import Image
from semantic_search.packages import package, relationships
from semantic_search.readers import SegmentWriter, plain, images, pdf, csv_sheet, text_chunks, pdfium_module
from semantic_search.office_readers import office, cell_position
from semantic_search.protocol import decode_request

ROOT = Path(__file__).resolve().parents[1]
TEST_ROOT = ROOT/'artifacts/diagnostics/multimodal/reader-contracts'
def tokens(text):
    return SimpleNamespace(ids=list(range(len(text))), offsets=[(i,i+1) for i in range(len(text))])
def entries(path, values):
    with zipfile.ZipFile(path, 'w') as archive:
        for name,value in values.items(): archive.writestr(name,value)

def odf_sheet(path, body):
    entries(path, {'content.xml': '<office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" '
        'xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">'
        '<office:body><office:spreadsheet><table:table table:name="Forecast">'+body+
        '</table:table></office:spreadsheet></office:body></office:document-content>'})
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
    def test_windows_pdf_loader_avoids_wmi_and_restores_platform_after_success_or_failure(self):
        sentinel=object()
        for failed in (False,True):
            def load(name):
                self.assertEqual(name,'pypdfium2');self.assertEqual(platform.system(),'Windows')
                if failed:raise RuntimeError('import failed')
                return sentinel
            with patch.object(sys,'platform','win32'),patch.object(platform,'system',side_effect=AssertionError('WMI query forbidden')) as original,patch.object(importlib,'import_module',side_effect=load):
                if failed:
                    with self.assertRaisesRegex(RuntimeError,'import failed'):pdfium_module()
                else:self.assertIs(pdfium_module(),sentinel)
                self.assertIs(platform.system,original);original.assert_not_called()
    def test_non_windows_pdf_loader_keeps_normal_platform_detection(self):
        sentinel=object()
        with patch.object(sys,'platform','linux'),patch.object(platform,'system',return_value='Linux') as original:
            def load(name):
                self.assertIs(platform.system,original);self.assertEqual(platform.system(),'Linux');return sentinel
            with patch.object(importlib,'import_module',side_effect=load):self.assertIs(pdfium_module(),sentinel)
    def test_sheet_chunks_repeat_headers_without_splitting_ordinary_numeric_cells(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            context='Sheet: Measurements\nHeaders: A1: Gene | B1: adjusted_p_value\n'
            cells=[f'A{row}: {row}.38306799999999996E-4' for row in range(2,202)]
            writer=SegmentWriter(Path(folder)/'out',tokens)
            self.assertTrue(writer.table(context,cells,dict(kind='sheet',sheet='Measurements')))
            chunks=content(writer);self.assertGreater(len(chunks),1)
            bodies=[]
            for chunk in chunks:
                value=chunk['content']['text'];self.assertTrue(value.startswith(context));self.assertLessEqual(len(tokens(value).ids),2048)
                body=value[len(context):];bodies.append(body)
                rows=[int(line.split(':',1)[0][1:]) for line in body.splitlines()]
                self.assertEqual(chunk['locator']['startCell'],'A'+str(min(rows)))
                self.assertEqual(chunk['locator']['endCell'],'A'+str(max(rows)))
                self.assertNotIn('(continued)',body)
            self.assertEqual(''.join(bodies),'\n'.join(cells))
    def test_giant_cell_continuations_keep_the_real_address_and_every_character(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            context='Sheet: Notes\nHeaders: long free text\n'
            cells=['A2: '+('مرحبا'*1000)+'\nB999: this is literal cell text\n'+('tail '*1000),'C3: next cell']
            writer=SegmentWriter(Path(folder)/'out',tokens)
            self.assertTrue(writer.table(context,cells,dict(kind='sheet',sheet='Notes')))
            bodies=[];continued=False
            for chunk in content(writer):
                value=chunk['content']['text'];self.assertLessEqual(len(value),2048);self.assertTrue(value.startswith(context))
                body=value[len(context):]
                if body.startswith('A2 (continued):\n'):
                    continued=True;body=body[len('A2 (continued):\n'):]
                self.assertNotIn('999',chunk['locator']['startCell']+chunk['locator']['endCell'])
                bodies.append(body)
            self.assertTrue(continued)
            self.assertEqual(''.join(bodies),'\n'.join(cells))
    def test_sheet_context_and_segment_limits_are_visible_and_invalid_addresses_rejected(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            writer=SegmentWriter(Path(folder)/'out',tokens,max_segments=1)
            self.assertFalse(writer.table('Sheet: Tall\nHeaders: '+'h'*4000,['A2: '+'x'*5000],dict(kind='sheet',sheet='Tall')))
            self.assertEqual(len(writer.files),1)
            self.assertIn('sheet_context_limit',writer.reasons);self.assertIn('segment_limit',writer.reasons)
            for value in ('A0: invalid','XFE1: invalid','A1048577: invalid','unlabelled value'):
                with self.subTest(value=value),self.assertRaisesRegex(ValueError,'sheet_cell_address'):
                    writer.table('Sheet: Tall\n',[value],dict(kind='sheet',sheet='Tall'))
    def test_csv_large_cells_keep_headers_and_addressed_continuations(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'wide.csv';source.write_text('Gene,Description\nAT1,'+'description '*600+'\nAT2,second\n',encoding='utf-8')
            writer=SegmentWriter(Path(folder)/'out',tokens);csv_sheet(source,writer)
            chunks=content(writer);self.assertGreater(len(chunks),1)
            self.assertTrue(all(chunk['evidence'].startswith('Sheet: CSV\nHeaders: Gene | Description\n') for chunk in chunks))
            self.assertTrue(any('B2 (continued):' in chunk['evidence'] for chunk in chunks))
            self.assertTrue(any('B3: second' in chunk['evidence'] for chunk in chunks))
    def test_trimmed_table_boundaries_are_retokenized_before_emission(self):
        def boundary_tokens(text):
            extra=16 if text.endswith('\n') else 0
            return SimpleNamespace(ids=list(range(len(text)+extra)),offsets=[(i,i+1) for i in range(len(text))]+[(len(text),len(text))]*extra)
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            context='Sheet: Boundary\nHeaders: Value\n'
            cells=['A2: '+'x'*(2048-len(context)-5),'B3: last']
            writer=SegmentWriter(Path(folder)/'out',boundary_tokens)
            self.assertTrue(writer.table(context,cells,dict(kind='sheet',sheet='Boundary')))
            bodies=[]
            for chunk in content(writer):
                value=chunk['content']['text'];self.assertLessEqual(len(boundary_tokens(value).ids),2048)
                body=value[len(context):]
                if body.startswith('A2 (continued):\n'):body=body[len('A2 (continued):\n'):]
                bodies.append(body)
            self.assertEqual(''.join(bodies),'\n'.join(cells))
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
    def test_odf_declared_multirow_headers_and_grouped_rows_keep_real_addresses(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'grouped.ods'
            odf_sheet(source,'''<table:table-header-rows>
                <table:table-row><table:table-cell table:number-columns-spanned="3"><text:p>Budget 2026</text:p></table:table-cell><table:covered-table-cell table:number-columns-repeated="2"/></table:table-row>
                <table:table-row><table:table-cell><text:p>Region</text:p></table:table-cell><table:table-cell><text:p>Actual</text:p></table:table-cell><table:table-cell><text:p>Plan</text:p></table:table-cell></table:table-row>
                </table:table-header-rows><table:table-rows><table:table-row><table:table-cell><text:p>North</text:p></table:table-cell><table:table-cell office:value="42000"/><table:table-cell office:value="50000"/></table:table-row></table:table-rows>
                <table:table-row-group><table:table-rows><table:table-row table:number-rows-repeated="2"><table:table-cell/></table:table-row>
                <table:table-row><table:table-cell><text:p>South</text:p></table:table-cell><table:table-cell office:value="17000"/><table:table-cell office:value="21000"/></table:table-row></table:table-rows></table:table-row-group>''')
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            chunks=content(writer)
            north=next(chunk for chunk in chunks if 'B3: 42000' in chunk['evidence'])
            south=next(chunk for chunk in chunks if 'B6: 17000' in chunk['evidence'])
            for chunk,row in ((north,3),(south,6)):
                for expected in ('Declared header rows:', 'A1: Budget 2026 [merged 3 columns × 1 rows]', 'A2: Region', 'B2: Actual', 'C2: Plan'):
                    self.assertIn(expected,chunk['evidence'])
                self.assertEqual(chunk['locator'],dict(kind='sheet',sheet='Forecast',startCell=f'A{row}',endCell=f'C{row}'))
    def test_odf_group_header_context_does_not_leak_to_sibling_or_outer_rows(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'scope.ods'
            odf_sheet(source,'''<table:table-header-rows><table:table-row><table:table-cell><text:p>Global</text:p></table:table-cell></table:table-row></table:table-header-rows>
                <table:table-row-group><table:table-header-rows><table:table-row><table:table-cell><text:p>Local</text:p></table:table-cell></table:table-row></table:table-header-rows>
                <table:table-row><table:table-cell office:value="11"/></table:table-row></table:table-row-group>
                <table:table-row-group><table:table-rows><table:table-row><table:table-cell office:value="22"/></table:table-row></table:table-rows></table:table-row-group>
                <table:table-row><table:table-cell office:value="33"/></table:table-row>''')
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            chunks=content(writer)
            local=next(chunk for chunk in chunks if 'A3: 11' in chunk['evidence'])
            self.assertIn('A1: Global',local['evidence']);self.assertIn('A2: Local',local['evidence'])
            for address in ('A4: 22','A5: 33'):
                value=next(chunk['evidence'] for chunk in chunks if address in chunk['evidence'])
                self.assertIn('A1: Global',value);self.assertNotIn('Local',value)
    def test_odf_header_context_limits_are_explicit_without_losing_original_header_cells(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'large-header.ods'
            odf_sheet(source,'<table:table-header-rows><table:table-row><table:table-cell><text:p>'+('h'*10000)+
                '</text:p></table:table-cell></table:table-row></table:table-header-rows><table:table-rows><table:table-row><table:table-cell office:value="7"/></table:table-row></table:table-rows>')
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            self.assertIn('sheet_context_limit',writer.reasons)
            self.assertTrue(any('A2: 7' in item['evidence'] for item in content(writer)))
            self.assertTrue(all(len(tokens(item['content']['text']).ids)<=2048 for item in content(writer)))
            bodies=[]
            for item in content(writer):
                if item['locator']['startCell']!='A1':continue
                value=item['content']['text'].split('\n',2)[-1]
                if value.startswith('A1 (continued):\n'):value=value[len('A1 (continued):\n'):]
                bodies.append(value)
            self.assertEqual(''.join(bodies),'A1: '+'h'*10000)
    def test_odf_excessive_row_container_nesting_is_rejected(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'deep.ods'
            odf_sheet(source,'<table:table-row-group>'*65+'<table:table-row><table:table-cell office:value="1"/></table:table-row>'+'</table:table-row-group>'*65)
            with self.assertRaisesRegex(ValueError,'odf_row_nesting_budget'):
                office(source,SegmentWriter(Path(folder)/'out',tokens))
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
