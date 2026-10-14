"""Sandbox-only extraction entry point: fixed owned input, bounded prepared files."""
import argparse
import json
from pathlib import Path
import sys

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).absolute().parents[1]))
from semantic_search.paths import local_file
from semantic_search.manifest import sha256

FAMILIES = {'documents', 'presentations', 'spreadsheets', 'code', 'images', 'audio', 'video'}

def extract(request_path):
    from tokenizers import Tokenizer
    from semantic_search.readers import SegmentWriter, plain, images, pdf, csv_sheet
    from semantic_search.office_readers import office
    from semantic_search.media_readers import media
    from semantic_search.ocr import WindowsOcr, languages
    root = Path(request_path).absolute().parent
    request_path = local_file(request_path, root)
    if request_path.stat().st_size > 16384: raise ValueError('request_budget')
    request = json.loads(request_path.read_text(encoding='utf-8-sig'))
    required={'schemaVersion','source','sourceSha256','family','tokenizer'}
    if (not required<=set(request) or set(request)-required-{'ocrLanguages'} or type(request['schemaVersion']) is not int or request['schemaVersion'] != 1):
        raise ValueError('invalid_extract_request')
    if request['family'] not in FAMILIES: raise ValueError('unsupported_family')
    name = request['source']
    if not isinstance(name,str) or Path(name).name != name or not name.startswith('input.') or ':' in name or '\\' in name or '/' in name:
        raise ValueError('invalid_source_name')
    source = local_file(root/name, root)
    if sha256(source) != request['sourceSha256']: raise ValueError('snapshot_hash_mismatch')
    tokenizer = Tokenizer.from_file(str(Path(request['tokenizer'])))
    ocr_languages=languages(request.get('ocrLanguages',[]))
    use_ocr=request['family'] in ('documents','presentations','spreadsheets','images')
    writer = SegmentWriter(root/'output', tokenizer.encode,ocr=WindowsOcr(ocr_languages) if use_ocr else None)
    suffix = source.suffix.lower()
    if suffix in ('.docx','.odt','.pptx','.odp','.xlsx','.ods'): office(source,writer)
    elif suffix == '.pdf': pdf(source,writer)
    elif suffix in ('.csv','.tsv'): csv_sheet(source,writer)
    elif request['family'] == 'images': images(source,writer)
    elif request['family'] == 'code' or suffix in ('.txt','.md','.markdown'): plain(source,writer)
    elif request['family'] in ('audio','video'): media(source,writer,request['family'])
    else: raise ValueError('unsupported_content_format')
    if sha256(source) != request['sourceSha256']: raise ValueError('snapshot_changed')
    if not writer.files: raise ValueError('no_extractable_content')
    return writer.finish(source)

def require_sandbox():
    """Both extraction and playback require the same zero-capability boundary."""
    import ctypes
    from ctypes import wintypes
    kernel, advapi = ctypes.WinDLL('kernel32',use_last_error=True), ctypes.WinDLL('advapi32',use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    advapi.OpenProcessToken.argtypes = [wintypes.HANDLE,wintypes.DWORD,ctypes.POINTER(wintypes.HANDLE)]
    advapi.GetTokenInformation.argtypes = [wintypes.HANDLE,ctypes.c_int,ctypes.c_void_p,wintypes.DWORD,ctypes.POINTER(wintypes.DWORD)]
    token, size, value = wintypes.HANDLE(), wintypes.DWORD(), wintypes.DWORD()
    if not advapi.OpenProcessToken(kernel.GetCurrentProcess(),8,ctypes.byref(token)): raise ValueError('sandbox_required')
    try:
        if not advapi.GetTokenInformation(token,29,ctypes.byref(value),4,ctypes.byref(size)) or value.value != 1: raise ValueError('sandbox_required')
        advapi.GetTokenInformation(token,30,None,0,ctypes.byref(size))
        capabilities = ctypes.create_string_buffer(size.value)
        if not advapi.GetTokenInformation(token,30,capabilities,size.value,ctypes.byref(size)) or wintypes.DWORD.from_buffer(capabilities).value != 0: raise ValueError('sandbox_required')
    finally: kernel.CloseHandle(token)

def failure_reason(error):
    # Stable reason codes only. Exception bodies may contain source paths/content.
    if type(error) is ValueError and str(error).isascii() and all(c.isalnum() or c=='_' for c in str(error)) and 0<len(str(error))<=80:return str(error)
    known={('PIL.Image','DecompressionBombError'):'image_pixel_budget',('PIL','UnidentifiedImageError'):'malformed_image',
           ('zipfile','BadZipFile'):'malformed_package',('xml.etree.ElementTree','ParseError'):'malformed_xml',
           ('pypdf.errors','PdfReadError'):'malformed_pdf',('builtins','UnicodeDecodeError'):'unsupported_text_encoding',
           ('av.error','InvalidDataError'):'malformed_media',('av.error','DecoderNotFoundError'):'media_decoder_unavailable'}
    return known.get((type(error).__module__,type(error).__name__),'extraction_failed')

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--request', type=Path, required=True)
    options = parser.parse_args()
    try:
        require_sandbox()
        extract(options.request)
    except Exception as error:
        # Never persist exception bodies, paths or extracted document text.
        reason = failure_reason(error)
        (options.request.absolute().parent/'failed.json').write_text(json.dumps(dict(schemaVersion=1,reason=reason)),encoding='utf-8')
        raise SystemExit(2)
