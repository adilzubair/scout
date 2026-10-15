"""Source-grounded table, merged-heading and scoped-column reader contracts."""
from pathlib import Path
import tempfile
import unittest

from semantic_search.office_readers import office
from semantic_search.readers import SegmentWriter, column
from test_content_readers import TEST_ROOT, content, entries, tokens

S='http://schemas.openxmlformats.org/spreadsheetml/2006/main'
R='http://schemas.openxmlformats.org/officeDocument/2006/relationships'

def cell(address,value):
    from xml.sax.saxutils import escape
    return f'<c r="{address}" t="inlineStr"><is><t>{escape(str(value))}</t></is></c>'

def row(number,values):return f'<row r="{number}">'+''.join(cell(address,value) for address,value in values)+'</row>'

def workbook(path,rows,*,merges=(),tables=(),external=False):
    members={'xl/workbook.xml':f'<workbook xmlns="{S}" xmlns:r="{R}"><sheets><sheet name="Forecast" r:id="sheet"/></sheets></workbook>',
        'xl/_rels/workbook.xml.rels':f'<Relationships><Relationship Id="sheet" Type="{R}/worksheet" Target="worksheets/data.xml"/></Relationships>'}
    parts=''.join(f'<tablePart r:id="t{i}"/>' for i in range(len(tables)))
    members['xl/worksheets/data.xml']=f'<worksheet xmlns="{S}" xmlns:r="{R}"><sheetData>'+rows+'</sheetData><mergeCells>'+''.join(f'<mergeCell ref="{value}"/>' for value in merges)+'</mergeCells><tableParts>'+parts+'</tableParts></worksheet>'
    members['xl/worksheets/_rels/data.xml.rels']='<Relationships>'+''.join(f'<Relationship Id="t{i}" Type="{R}/table" Target="'+('https://example.invalid/table.xml" TargetMode="External' if external else f'../tables/t{i}.xml')+'"/>' for i in range(len(tables)))+'</Relationships>'
    for i,table in enumerate(tables):members[f'xl/tables/t{i}.xml']=table
    entries(path,members)

def table(name,ref,names,header=1,total=0):
    from xml.sax.saxutils import quoteattr
    return f'<table xmlns="{S}" name="{name}" displayName="{name}" ref="{ref}" headerRowCount="{header}" totalsRowCount="{total}"><tableColumns count="{len(names)}">'+''.join(f'<tableColumn id="{i+1}" name={quoteattr(value)}/>' for i,value in enumerate(names))+'</tableColumns></table>'


class XlsxContext(unittest.TestCase):
    @classmethod
    def setUpClass(cls):TEST_ROOT.mkdir(parents=True,exist_ok=True)

    def test_separate_tables_keep_declared_columns_without_other_table_or_sheet_labels(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'tables.xlsx'
            rows=row(1,[('A1','Unrelated note')])+row(5,[('A5','Region'),('B5','Revenue'),('D5','Department'),('E5','Employees')])
            rows+=''.join(row(n,[(f'A{n}','North'),(f'B{n}',42000),(f'D{n}','Operations'),(f'E{n}',7)]) for n in range(6,86))
            workbook(source,rows,tables=(table('Sales','A5:B85',['Region','Revenue']),table('Staff','D5:E85',['Department','Employees'])))
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            chunks=content(writer)
            sales=next(item['evidence'] for item in chunks if 'B50: 42000' in item['evidence'])
            staff=next(item['evidence'] for item in chunks if 'E50: 7' in item['evidence'])
            for expected in ('Declared table: Sales','B: Revenue','A5:B85'):self.assertIn(expected,sales)
            for forbidden in ('Employees','Department','Unrelated note'):self.assertNotIn(forbidden,sales)
            self.assertIn('Declared table: Staff',staff);self.assertIn('E: Employees',staff);self.assertNotIn('Revenue',staff)

    def test_merged_leading_rows_retain_parent_and_leaf_labels_in_late_chunks(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'hierarchy.xlsx'
            rows=row(1,[('A1','Budget 2026')])+row(2,[('A2','Sales'),('C2','Costs')])+row(3,[('A3','Region'),('B3','Actual'),('C3','Target'),('D3','Variance')])
            rows+=''.join(row(n,[(f'A{n}','North'),(f'B{n}',42000),(f'C{n}',50000),(f'D{n}',8000)]) for n in range(4,86))
            workbook(source,rows,merges=('A1:D1','A2:B2','C2:D2'))
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            value=next(item['evidence'] for item in content(writer) if 'B50: 42000' in item['evidence'])
            for expected in ('Leading sheet rows:','A1: Budget 2026 [merged A1:D1]','A2: Sales [merged A2:B2]','B3: Actual','C3: Target'):self.assertIn(expected,value)

    def test_hidden_and_multiple_header_rows_preserve_declared_semantics(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            for header in (0,2):
                source=Path(folder)/f'header{header}.xlsx'
                rows=row(5,[('A5','Year'),('B5',2026)])+row(6,[('A6','Region'),('B6','Actual')])
                rows+=''.join(row(n,[(f'A{n}','North'),(f'B{n}',n)]) for n in range(7,81))
                workbook(source,rows,tables=(table('ForecastData','A5:B80',['Region','Revenue'],header),))
                writer=SegmentWriter(Path(folder)/str(header),tokens);office(source,writer)
                value=next(item['evidence'] for item in content(writer) if 'B50: 50' in item['evidence'])
                self.assertIn(f'Header rows: {header}',value);self.assertIn('B: Revenue',value)
                if header==2:self.assertIn('Declared header cells: A5: Year | B5: 2026 | A6: Region | B6: Actual',value)
                else:self.assertNotIn('Declared header cells:',value)

    def test_wide_table_late_columns_keep_their_own_labels(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'wide.xlsx';names=[f'metric{i}' for i in range(1,31)]
            rows=row(1,[(column(i)+'1',value) for i,value in enumerate(names,1)])
            rows+=''.join(row(n,[(column(i)+str(n),i*1000+n) for i in range(1,31)]) for n in range(2,77))
            workbook(source,rows,tables=(table('Metrics','A1:AD76',names),))
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            value=next(item['evidence'] for item in content(writer) if 'Z70: 26070' in item['evidence'])
            self.assertIn('Z: metric26',value);self.assertNotIn('A: metric1 |',value)
            self.assertTrue(all(len(tokens(item['content']['text']).ids)<=2048 for item in content(writer)))

    def test_context_clipping_is_visible_and_cell_payloads_remain_exact(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'long.xlsx';long='description '*700
            rows=row(1,[('A1',long)])+row(2,[('A2','0.38306799999999996E-4')])
            workbook(source,rows,tables=(table('LongLabel','A1:A2',[long]),))
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            self.assertIn('sheet_context_limit',writer.reasons)
            self.assertTrue(any('A2: 0.38306799999999996E-4' in item['evidence'] for item in content(writer)))

    def test_external_table_is_not_fetched_and_literal_cells_remain_available(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'external.xlsx';workbook(source,row(5,[('A5',7)]),tables=(table('External','A5:A6',['Count']),),external=True)
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            self.assertIn('external_resource_unavailable',writer.reasons);self.assertIn('A5: 7','\n'.join(item['evidence'] for item in content(writer)))

    def test_invalid_ranges_overlap_and_column_mismatches_are_explicit(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'invalid.xlsx'
            for definition,reason in ((table('Bad','B5:A6',['One','Two']),'invalid_table_range'),(table('Bad','A5:B6',['One']),'table_column_mismatch'),(table('Bad','A5:B6',['One','Two'],3),'invalid_table_row_counts')):
                workbook(source,row(5,[('A5',7)]),tables=(definition,))
                with self.assertRaisesRegex(ValueError,reason):office(source,SegmentWriter(Path(folder)/reason,tokens))
            workbook(source,row(5,[('A5',7)]),tables=(table('One','A5:B6',['A','B']),table('Two','B5:C6',['B','C'])))
            with self.assertRaisesRegex(ValueError,'overlapping_table_ranges'):office(source,SegmentWriter(Path(folder)/'overlap',tokens))
            for merged in ('D2:A1','A1:B2:C3','A1:XFE2'):
                workbook(source,row(1,[('A1','Title')]),merges=(merged,))
                with self.assertRaises(ValueError):office(source,SegmentWriter(Path(folder)/'merge',tokens))

    def test_unsigned_metadata_counts_accept_valid_noncanonical_lexical_forms(self):
        with tempfile.TemporaryDirectory(dir=TEST_ROOT) as folder:
            source=Path(folder)/'counts.xlsx'
            definition=table('Counts','$A$5:$A$6',['Count'],' +01 ').replace('count="1"','count="001"')
            workbook(source,row(5,[('A5','Count')])+row(6,[('A6',7)]),tables=(definition,))
            writer=SegmentWriter(Path(folder)/'out',tokens);office(source,writer)
            value=next(item['evidence'] for item in content(writer) if 'A6: 7' in item['evidence'])
            self.assertIn('Header rows: 1',value);self.assertIn('Range: A5:A6',value)


if __name__=='__main__':unittest.main()
