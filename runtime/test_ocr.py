"""Authored OCR/scan coverage contracts; these fixtures are not a quality holdout."""
import argparse
import json
import hashlib
from pathlib import Path
import tempfile
import struct
import zlib
from types import SimpleNamespace
import unittest

from PIL import Image,ImageDraw,ImageFont
from semantic_search.ocr import WindowsOcr,languages
from semantic_search.readers import SegmentWriter,images,pdf

ROOT=Path(__file__).resolve().parents[1]
PACK=ROOT/'artifacts/diagnostics/multimodal/scan-fixtures-v1'
WORK=ROOT/'artifacts/diagnostics/multimodal/ocr-contracts'
def tokens(text):return SimpleNamespace(ids=list(range(len(text))),offsets=[(i,i+1) for i in range(len(text))])
def page(text):
    image=Image.new('RGB',(1400,800),'white')
    ImageDraw.Draw(image).multiline_text((80,80),text,font=ImageFont.load_default(size=54),fill='black',spacing=30)
    return image
def author():
    PACK.mkdir(parents=True,exist_ok=True)
    marker=PACK/'fixtures.json'
    if marker.is_file():
        existing=json.loads(marker.read_text())
        if existing.get('schemaVersion')==1 and all((PACK/item['file']).is_file() and
                hashlib.sha256((PACK/item['file']).read_bytes()).hexdigest()==item.get('sha256') for item in existing['items']):return
    first=page('QUARTERLY REVENUE\nNorth division 42000')
    second=page('RIVER DELTA PURCHASE\nInvoice total 73510')
    first.save(PACK/'f001.png')
    first.save(PACK/'f002.tiff',save_all=True,append_images=[second])
    first.save(PACK/'f003.pdf',save_all=True,append_images=[second],resolution=100)
    items=[
        dict(file='f001.png',family='images',pages=1,expected=['QUARTERLY REVENUE','42000']),
        dict(file='f002.tiff',family='images',pages=2,expected=['QUARTERLY REVENUE','42000','RIVER DELTA','73510']),
        dict(file='f003.pdf',family='documents',pages=2,expected=['QUARTERLY REVENUE','42000','RIVER DELTA','73510'])]
    for item in items:item['sha256']=hashlib.sha256((PACK/item['file']).read_bytes()).hexdigest()
    marker.write_text(json.dumps(dict(schemaVersion=1,items=items)),encoding='utf-8')

class OcrChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):WORK.mkdir(parents=True,exist_ok=True);author()
    def test_languages_are_bounded_and_inert(self):
        self.assertEqual(languages(['en-US','en-US']),['en-US'])
        for value in (True,'en-US',['../eng'],['en-US']*4,['a'*60],['en-US+ara']):
            with self.subTest(value=value),self.assertRaises(ValueError):languages(value)
    def test_unavailable_ocr_retains_vision_and_reports_partial(self):
        ocr=SimpleNamespace(reasons={'ocr_language_unavailable'},recognize=lambda image:[],coverage=lambda:dict(engine='windows',languages=[],availableLanguages=[]))
        with tempfile.TemporaryDirectory(dir=WORK) as folder:
            writer=SegmentWriter(Path(folder)/'out',tokens,ocr=ocr);images(PACK/'f001.png',writer)
            result=writer.finish(PACK/'f001.png')
            self.assertTrue(result['partial']);self.assertEqual(result['reasons'],['ocr_language_unavailable'])
            self.assertEqual(len(writer.files),1)
            self.assertEqual(json.loads((writer.output/writer.files[0]['name']).read_text())['modality'],'image')
    def test_ocr_text_retains_locator_and_obeys_segment_budget(self):
        ocr=SimpleNamespace(reasons=set(),recognize=lambda image:[('en-US','text recognized from pixels')],coverage=lambda:dict(engine='windows',languages=['en-US'],availableLanguages=['en-US']))
        with tempfile.TemporaryDirectory(dir=WORK) as folder:
            for limit in (1,10):
                writer=SegmentWriter(Path(folder)/str(limit),tokens,max_segments=limit,ocr=ocr);images(PACK/'f002.tiff',writer)
                if limit==1:self.assertIn('segment_limit',writer.reasons)
                else:
                    segments=[json.loads((writer.output/item['name']).read_text()) for item in writer.files]
                    self.assertEqual([segment['locator']['page'] for segment in segments],[1,1,2,2])
                    self.assertEqual([segment['modality'] for segment in segments],['image','text','image','text'])
    def test_actual_english_scans_and_missing_language(self):
        engine=WindowsOcr(['en-US'])
        if not engine.engines:self.skipTest('This host has no Windows English OCR pack')
        for fixture in json.loads((PACK/'fixtures.json').read_text())['items']:
            with self.subTest(fixture=fixture['file']),tempfile.TemporaryDirectory(dir=WORK) as folder:
                writer=SegmentWriter(Path(folder)/'out',tokens,ocr=WindowsOcr(['en-US']))
                (pdf if fixture['file'].endswith('.pdf') else images)(PACK/fixture['file'],writer)
                segments=[json.loads((writer.output/item['name']).read_text()) for item in writer.files]
                text='\n'.join(segment['evidence'] for segment in segments)
                for value in fixture['expected']:self.assertIn(value,text)
                self.assertEqual(sorted({segment['locator']['page'] for segment in segments}),list(range(1,fixture['pages']+1)))
        missing=WindowsOcr(['zz-ZZ'])
        self.assertFalse(missing.engines);self.assertIn('ocr_language_unavailable',missing.reasons)
    def test_ocr_budget_does_not_remove_visual_content(self):
        engine=WindowsOcr(['en-US'],pages=0)
        if not engine.engines:self.skipTest('This host has no Windows English OCR pack')
        self.assertEqual(engine.recognize(page('TEST')),[])
        self.assertIn('ocr_page_limit',engine.reasons)
    def test_encrypted_pdf_and_oversized_image_fail_before_content_decode(self):
        from pypdf import PdfWriter
        with tempfile.TemporaryDirectory(dir=WORK) as folder:
            source=Path(folder)/'locked.pdf';document=PdfWriter();document.add_blank_page(300,400);document.encrypt('authored-password')
            with source.open('wb') as output:document.write(output)
            with self.assertRaisesRegex(ValueError,'encrypted_pdf'):pdf(source,SegmentWriter(Path(folder)/'pdf-out',tokens))
            huge=Path(folder)/'huge.png'
            header=struct.pack('>IIBBBBB',30000,30000,8,2,0,0,0)
            def chunk(kind,data):return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data))
            huge.write_bytes(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',header)+chunk(b'IDAT',zlib.compress(b'\0'))+chunk(b'IEND',b''))
            with self.assertRaises(Image.DecompressionBombError):images(huge,SegmentWriter(Path(folder)/'image-out',tokens))
    def test_pdf_page_budget_reports_partial_coverage(self):
        with tempfile.TemporaryDirectory(dir=WORK) as folder:
            source=PACK/'f003.pdf';writer=SegmentWriter(Path(folder)/'out',tokens)
            pdf(source,writer,max_pages=1)
            result=writer.finish(source)
            self.assertTrue(result['partial']);self.assertIn('page_limit',result['reasons'])
            self.assertEqual(len(writer.files),1)

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--author',action='store_true');args=parser.parse_args()
    if args.author:author();print(PACK)
    else:unittest.main(argv=[__file__])
