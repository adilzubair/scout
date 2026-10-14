"""Read Windows MSI tables and cabinet streams without executing installation actions."""

import ctypes
from contextlib import contextmanager
import os
from pathlib import Path, PureWindowsPath


def safe_component(value):
    if (not value or value in ('.', '..') or any(c in value for c in '\\/:\0<>"|?*')
            or value.rstrip(' .') != value or PureWindowsPath(value).is_absolute()
            or PureWindowsPath(value).is_reserved()):
        raise ValueError('Unsafe MSI path component')
    return value


def target_name(default):
    value = default.split(':', 1)[0].split('|')[-1]
    return '' if value == '.' else safe_component(value)


def file_layout(directory_rows, component_rows, file_rows, root):
    directories = {}
    for identifier, parent, name in directory_rows:
        if identifier in directories:
            raise ValueError('Duplicate MSI directory')
        directories[identifier] = (parent, name)
    components = {}
    for identifier, directory in component_rows:
        if identifier in components:
            raise ValueError('Duplicate MSI component')
        components[identifier] = directory
    if root not in directories:
        raise ValueError('Pinned MSI application directory missing')
    cache = {root: Path()}

    def resolve(identifier, trail=()):
        if identifier in cache:
            return cache[identifier]
        if identifier in trail or len(trail) > 64 or identifier not in directories:
            raise ValueError('Invalid MSI directory graph')
        parent, default = directories[identifier]
        if not parent or parent == identifier:
            cache[identifier] = None  # Files outside the application directory are not installed.
        else:
            base = resolve(parent, (*trail, identifier))
            cache[identifier] = base / target_name(default) if base is not None else None
        return cache[identifier]

    layout, seen = {}, set()
    for key, component, name, size in file_rows:
        safe_component(key)  # Cabinet member names are File table keys.
        if component not in components:
            raise ValueError('MSI file refers to an unknown component')
        directory = resolve(components[component])
        if directory is None:
            continue
        relative = directory / safe_component(name.split('|')[-1])
        if str(relative).casefold() in seen or key in layout or not 0 <= int(size) <= 512 * 1024 * 1024:
            raise ValueError('Duplicate or oversized MSI file')
        seen.add(str(relative).casefold())
        layout[key] = (relative, int(size))
    if not layout or len(layout) > 40000 or sum(size for _, size in layout.values()) > 3 * 1024**3:
        raise ValueError('MSI application payload budget exceeded')
    return layout


class MsiDatabase:
    def __init__(self, package):
        if os.name != 'nt':
            raise OSError('MSI extraction requires Windows')
        self.api = ctypes.WinDLL('msi.dll')
        uint, pointer = ctypes.c_uint, ctypes.POINTER
        definitions = {
            'MsiOpenDatabaseW': ([ctypes.c_wchar_p, ctypes.c_void_p, pointer(uint)], uint),
            'MsiDatabaseOpenViewW': ([uint, ctypes.c_wchar_p, pointer(uint)], uint),
            'MsiViewExecute': ([uint, uint], uint),
            'MsiViewFetch': ([uint, pointer(uint)], uint),
            'MsiRecordGetStringW': ([uint, uint, ctypes.c_wchar_p, pointer(uint)], uint),
            'MsiRecordReadStream': ([uint, uint, ctypes.c_void_p, pointer(uint)], uint),
            'MsiCloseHandle': ([uint], uint),
        }
        for name, (arguments, result) in definitions.items():
            function = getattr(self.api, name)
            function.argtypes, function.restype = arguments, result
        self.handle = uint()
        self.check(self.api.MsiOpenDatabaseW(str(Path(package).resolve(strict=True)), None,
                                           ctypes.byref(self.handle)))  # MSIDBOPEN_READONLY = 0.

    @staticmethod
    def check(code):
        if code:
            raise OSError(f'Windows MSI data access failed ({code})')

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.api.MsiCloseHandle(self.handle)

    @contextmanager
    def query(self, sql):
        view = ctypes.c_uint()
        self.check(self.api.MsiDatabaseOpenViewW(self.handle, sql, ctypes.byref(view)))
        try:
            self.check(self.api.MsiViewExecute(view, 0))
            yield view
        finally:
            self.api.MsiCloseHandle(view)

    def string(self, record, column):
        length = ctypes.c_uint(4095)
        buffer = ctypes.create_unicode_buffer(4096)
        self.check(self.api.MsiRecordGetStringW(record, column, buffer, ctypes.byref(length)))
        return buffer.value

    def rows(self, sql, columns):
        with self.query(sql) as view:
            for _ in range(40001):
                record = ctypes.c_uint()
                code = self.api.MsiViewFetch(view, ctypes.byref(record))
                if code == 259:  # ERROR_NO_MORE_ITEMS
                    return
                self.check(code)
                try:
                    yield tuple(self.string(record, column) for column in range(1, columns+1))
                finally:
                    self.api.MsiCloseHandle(record)
            raise ValueError('MSI table row budget exceeded')

    def export_cabinet(self, name, destination):
        safe_component(name)
        # The name comes from the verified MSI; quoted names remain rejected.
        if "'" in name:
            raise ValueError('Invalid MSI stream name')
        with self.query(f"SELECT `Data` FROM `_Streams` WHERE `Name`='{name}'") as view:
            record = ctypes.c_uint()
            self.check(self.api.MsiViewFetch(view, ctypes.byref(record)))
            try:
                total = 0
                buffer = ctypes.create_string_buffer(1024 * 1024)
                with Path(destination).open('xb') as stream:
                    while True:
                        length = ctypes.c_uint(len(buffer))
                        self.check(self.api.MsiRecordReadStream(record, 1, buffer, ctypes.byref(length)))
                        if not length.value:
                            break
                        total += length.value
                        if total > 512 * 1024 * 1024:
                            raise ValueError('Cabinet stream budget exceeded')
                        stream.write(buffer.raw[:length.value])
            finally:
                self.api.MsiCloseHandle(record)
