"""Large original-sheet and hostile-tail contracts for bounded ODS parsing."""
import io
from pathlib import Path
import tempfile
import tracemalloc
import unittest
import xml.etree.ElementTree as ET

from PIL import Image
from semantic_search.office_readers import office
from semantic_search.packages import package, xml_stream
from semantic_search.readers import SegmentWriter
from test_content_readers import TEST_ROOT, content, entries, odf_sheet, tokens

ROW = '<table:table-row><table:table-cell office:value="42"><text:p>42</text:p></table:table-cell></table:table-row>'


class StreamingOds(unittest.TestCase):
    @classmethod
    def setUpClass(cls): TEST_ROOT.mkdir(parents=True, exist_ok=True)

    def test_large_sheet_validates_to_eof_and_retains_only_a_bounded_row_window(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'large.ods';odf_sheet(source,ROW*90000)
            writer=SegmentWriter(Path(folder)/'out',tokens,max_segments=4)
            tracemalloc.start()
            try:
                office(source,writer);_,peak=tracemalloc.get_traced_memory()
            finally:tracemalloc.stop()
            self.assertLess(peak,12*1024**2,'The sheet was materialized as a full XML tree.')
            chunks=content(writer);self.assertEqual(len(chunks),4)
            self.assertEqual(chunks[-1]['locator']['startCell'],'A4')
            self.assertIn('A4: 42',chunks[-1]['evidence']);self.assertIn('segment_limit',writer.reasons)

    def test_malformed_tail_is_rejected_before_any_prefix_segment_is_written(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'tail.ods';odf_sheet(source,ROW*90000+'<broken>')
            writer=SegmentWriter(Path(folder)/'out',tokens,max_segments=1)
            with self.assertRaises(ET.ParseError):office(source,writer)
            self.assertEqual(writer.files,[])

    def test_large_xml_dtd_is_rejected_in_utf8_and_utf16_before_entity_expansion(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            for index,encoding in enumerate(('utf-8','utf-16')):
                source=Path(folder)/f'entity-{index}.ods'
                payload='<!DOCTYPE doc [<!ENTITY x "secret">]><doc>'+(' '*9*1024**2)+'&x;</doc>'
                entries(source,{'content.xml':payload.encode(encoding)})
                writer=SegmentWriter(Path(folder)/str(index),tokens,max_segments=1)
                with self.assertRaisesRegex(ValueError,'xml_entities'):office(source,writer)
                self.assertEqual(writer.files,[])

    def test_large_non_content_xml_and_active_packages_keep_existing_rejections(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'other.ods'
            for name,value,reason in (('styles.xml','<styles>'+(' '*9*1024**2)+'</styles>','xml_budget'),
                                      ('Basic/module.xml','<script/>','active_or_encrypted_package')):
                entries(source,{'content.xml':'<doc/>',name:value})
                with self.assertRaisesRegex(ValueError,reason):office(source,SegmentWriter(Path(folder)/name.split('/')[0],tokens))

    def test_large_row_is_rejected_before_unbounded_cell_text_accumulates(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'row.ods';odf_sheet(source,'<table:table-row><table:table-cell><text:p>'+('x'*9*1024**2)+'</text:p></table:table-cell></table:table-row>')
            writer=SegmentWriter(Path(folder)/'out',tokens)
            with self.assertRaisesRegex(ValueError,'sheet_row_byte_budget'):office(source,writer)
            self.assertEqual(writer.files,[])

    def test_excessive_row_nodes_and_global_xml_depth_are_explicit(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'nodes.ods'
            odf_sheet(source,'<table:table-row><table:table-cell><text:p>'+('<text:span/>'*100001)+'</text:p></table:table-cell></table:table-row>')
            with self.assertRaisesRegex(ValueError,'sheet_row_node_budget'):
                office(source,SegmentWriter(Path(folder)/'nodes',tokens))
            entries(source,{'content.xml':'<doc>'*257+'</doc>'*257})
            with self.assertRaisesRegex(ValueError,'xml_nesting_budget'):
                office(source,SegmentWriter(Path(folder)/'deep',tokens))
            entries(source,{'content.xml':'<doc>'+('<x/>'*4000001)+'</doc>'})
            with self.assertRaisesRegex(ValueError,'xml_node_budget'):
                office(source,SegmentWriter(Path(folder)/'global-nodes',tokens))

    def test_streaming_sheet_retains_embedded_images_at_the_actual_row(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'images.ods';output=io.BytesIO();Image.new('RGB',(12,12),'blue').save(output,format='PNG')
            odf_sheet(source,ROW+'<table:table-row><table:table-cell table:number-columns-repeated="2"/><table:table-cell><draw:image xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:xlink="http://www.w3.org/1999/xlink" xlink:href="Pictures/blue.png"/></table:table-cell></table:table-row>')
            import zipfile
            with zipfile.ZipFile(source,'a') as archive:archive.writestr('Pictures/blue.png',output.getvalue())
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            images=[item for item in content(writer) if item['modality']=='image']
            self.assertEqual(len(images),1);self.assertEqual(images[0]['locator']['startCell'],'C2');self.assertEqual(images[0]['locator']['endCell'],'C2')

    def test_nested_table_is_an_explicit_unsupported_structure(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'nested.ods'
            odf_sheet(source,'<table:table-row><table:table-cell><table:table table:name="Nested">'+ROW+'</table:table></table:table-cell></table:table-row>')
            with self.assertRaisesRegex(ValueError,'nested_odf_table_unavailable'):
                office(source,SegmentWriter(Path(folder)/'out',tokens))

    def test_stream_authorization_requires_complete_package_validation(self):
        import zipfile
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'source.ods';odf_sheet(source,ROW)
            with zipfile.ZipFile(source) as raw:
                with self.assertRaisesRegex(ValueError,'unvalidated_xml_stream'):
                    list(xml_stream(raw,'content.xml'))
            with self.assertRaisesRegex(ValueError,'invalid_streaming_member'):
                with package(source,streaming_xml=('styles.xml',)):pass

    def test_archive_member_and_total_expansion_limits_are_still_checked_first(self):
        import struct,zipfile
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'oversize.ods'
            entries(source,{'content.xml':'<doc/>'})
            data=bytearray(source.read_bytes());offset=data.index(b'PK\x01\x02')
            struct.pack_into('<I',data,offset+24,64*1024**2+1);source.write_bytes(data)
            with self.assertRaisesRegex(ValueError,'unsafe_package_entry'):
                office(source,SegmentWriter(Path(folder)/'member',tokens))
            entries(source,{'content.xml':'<doc/>'})
            data=bytearray(source.read_bytes());offset=data.index(b'PK\x01\x02')
            struct.pack_into('<I',data,offset+24,7);source.write_bytes(data)
            with self.assertRaisesRegex(ValueError,'xml_size_mismatch'):
                office(source,SegmentWriter(Path(folder)/'size-mismatch',tokens))
            entries(source,{f'part{i}.xml':'<doc/>' for i in range(5)})
            data=bytearray(source.read_bytes());offset=0
            for _ in range(5):
                offset=data.index(b'PK\x01\x02',offset);struct.pack_into('<I',data,offset+24,64*1024**2);offset+=4
            source.write_bytes(data)
            with self.assertRaisesRegex(ValueError,'archive_budget'):
                office(source,SegmentWriter(Path(folder)/'total',tokens))


if __name__=='__main__':unittest.main()
