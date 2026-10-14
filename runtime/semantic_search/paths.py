"""Canonical filesystem boundaries, including Windows AppContainer NT namespaces."""
import os
from pathlib import Path

def canonical(path):
    path = Path(path).absolute()
    if os.name != 'nt':
        return str(path.resolve(strict=True))
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.GetFinalPathNameByHandleW.argtypes = [wintypes.HANDLE, wintypes.LPWSTR, wintypes.DWORD, wintypes.DWORD]
    kernel.GetFinalPathNameByHandleW.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.CreateFileW(str(path), 0x80, 7, None, 3, 0x02000000, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        value = ctypes.create_unicode_buffer(32768)
        # FILE_NAME_NORMALIZED | VOLUME_NAME_NT. DOS device-name mapping is denied
        # inside an AppContainer. NT names still resolve links and identify volumes.
        count = kernel.GetFinalPathNameByHandleW(handle, value, len(value), 2)
        if not count:
            raise ctypes.WinError(ctypes.get_last_error())
        if count >= len(value):
            raise ValueError('Canonical path exceeded budget')
        return value.value.casefold()
    finally:
        kernel.CloseHandle(handle)

def absolute_local(path):
    path = Path(path).absolute()
    canonical(path)  # Strict existence and canonical handle resolution.
    return path

def within(path, root):
    actual, boundary = canonical(path), canonical(root)
    return os.path.commonpath((actual, boundary)) == boundary

def local_file(path, root):
    path = absolute_local(path)
    if not within(path, root) or not path.is_file():
        raise ValueError('Media must be a local file inside its owned root')
    return path
