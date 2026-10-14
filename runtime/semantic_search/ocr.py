"""Bounded local Windows OCR; visual embeddings remain independent of languages."""
import asyncio
import re
import time

TAG = re.compile(r'[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,3}')

def languages(values):
    if not isinstance(values,list) or len(values)>3 or any(not isinstance(v,str) or not TAG.fullmatch(v) for v in values):
        raise ValueError('ocr_language_configuration')
    return list(dict.fromkeys(values))

class WindowsOcr:
    def __init__(self, requested=None, *, seconds=20, pages=64):
        self.requested=languages(requested or [])
        self.engines=[];self.available=[];self.selected=[];self.reasons=set()
        self.seconds,self.remaining,self.spent=seconds,pages,0
        try:
            from winrt.windows.media.ocr import OcrEngine
            from winrt.windows.globalization import Language
            self.available=[v.language_tag for v in OcrEngine.available_recognizer_languages]
            if not self.requested:
                engine=OcrEngine.try_create_from_user_profile_languages()
                if engine is not None:self.engines.append(engine)
            else:
                for tag in self.requested:
                    engine=OcrEngine.try_create_from_language(Language(tag))
                    if engine is None:self.reasons.add('ocr_language_unavailable')
                    else:self.engines.append(engine)
            self.selected=[engine.recognizer_language.language_tag for engine in self.engines]
            self.max_dimension=min(4096,OcrEngine.max_image_dimension)
            if not self.engines:self.reasons.add('ocr_language_unavailable')
        except (ImportError,OSError,RuntimeError):
            self.reasons.add('ocr_unavailable')

    def recognize(self, image):
        if not self.engines:return []
        if self.remaining<=0:self.reasons.add('ocr_page_limit');return []
        if self.spent>=self.seconds:self.reasons.add('ocr_time_limit');return []
        self.remaining-=1
        began=time.monotonic()
        try:
            from PIL import Image
            from .readers import opaque_image
            from winrt.windows.graphics.imaging import SoftwareBitmap,BitmapPixelFormat
            from winrt.windows.storage.streams import DataWriter
            if min(image.size)<=0 or image.width*image.height>100_000_000:raise ValueError('image_pixel_budget')
            image=opaque_image(image);image.thumbnail((self.max_dimension,self.max_dimension),Image.Resampling.LANCZOS)
            if image.width*image.height>4_000_000:
                scale=(4_000_000/(image.width*image.height))**.5
                image=image.resize((max(1,int(image.width*scale)),max(1,int(image.height*scale))),Image.Resampling.LANCZOS)
            writer=DataWriter();bitmap=None
            try:
                writer.write_bytes(image.convert('RGBA').tobytes())
                bitmap=SoftwareBitmap.create_copy_from_buffer(writer.detach_buffer(),BitmapPixelFormat.RGBA8,image.width,image.height)
                async def read():
                    results=[]
                    for engine in self.engines:
                        result=await engine.recognize_async(bitmap)
                        text='\n'.join(line.text for line in result.lines).strip()
                        if len(text)>32000:self.reasons.add('ocr_text_limit');text=text[:32000]
                        if text:results.append((engine.recognizer_language.language_tag,text))
                    return results
                async def bounded():return await asyncio.wait_for(read(),min(3,max(.001,self.seconds-self.spent)))
                return asyncio.run(bounded())
            finally:
                if bitmap is not None:bitmap.close()
                writer.close()
        except TimeoutError:self.reasons.add('ocr_time_limit');self.spent=self.seconds;return []
        except (ImportError,OSError,RuntimeError):self.reasons.add('ocr_failed');return []
        finally:self.spent+=time.monotonic()-began

    def coverage(self):
        return dict(engine='windows',languages=self.selected,availableLanguages=self.available)
