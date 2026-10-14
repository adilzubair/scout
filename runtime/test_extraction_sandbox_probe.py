"""Live Windows token/network/filesystem probe run by the C# sandbox harness."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import traceback

def report_error(kind, value, trace):
    with Path('probe-error.txt').open('w', encoding='utf-8') as output:
        traceback.print_exception(kind, value, trace, file=output)
sys.excepthook = report_error

config = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
kernel = ctypes.WinDLL('kernel32', use_last_error=True)
advapi = ctypes.WinDLL('advapi32', use_last_error=True)
kernel.GetCurrentProcess.restype = wintypes.HANDLE
kernel.CloseHandle.argtypes = [wintypes.HANDLE]
advapi.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
advapi.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
token = wintypes.HANDLE()
if not advapi.OpenProcessToken(kernel.GetCurrentProcess(), 8, ctypes.byref(token)):
    raise ctypes.WinError(ctypes.get_last_error())
try:
    value, size = wintypes.DWORD(), wintypes.DWORD()
    if not advapi.GetTokenInformation(token, 29, ctypes.byref(value), 4, ctypes.byref(size)):
        raise ctypes.WinError(ctypes.get_last_error())
    appcontainer = value.value == 1
    advapi.GetTokenInformation(token, 30, None, 0, ctypes.byref(size))
    capabilities = ctypes.create_string_buffer(size.value)
    if not advapi.GetTokenInformation(token, 30, capabilities, size.value, ctypes.byref(size)):
        raise ctypes.WinError(ctypes.get_last_error())
    capability_count = wintypes.DWORD.from_buffer(capabilities).value
finally:
    kernel.CloseHandle(token)

def denied_read():
    try:
        Path(config['privateSource']).read_bytes()
        return False
    except PermissionError:
        return True

def denied_network():
    # A reachable parent-owned IPv4 listener avoids confusing an offline host with denial.
    try:
        with socket.create_connection(('127.0.0.1', config['port']), timeout=2):
            return 0
    except OSError as error:
        return 10060 if isinstance(error, TimeoutError) else (getattr(error, 'winerror', 0) or 0)

report = dict(appcontainer=appcontainer, capabilityCount=capability_count, privateReadDenied=denied_read(), networkError=denied_network(),
              inheritedSecret=os.environ.get('SCOUT_SANDBOX_TEST_SECRET', ''), pid=os.getpid())
if config.get('checkPackage'):
    import tokenizers
    report.update(prefix=sys.prefix,executable=sys.executable,packagePath=tokenizers.__file__)
if len(sys.argv) > 2 and sys.argv[2] == '--child':
    Path('child.json').write_text(json.dumps(report), encoding='utf-8')
else:
    subprocess.run([sys.executable, __file__, sys.argv[1], '--child'], check=True, timeout=20)
    report['child'] = json.loads(Path('child.json').read_text(encoding='utf-8'))
    try:
        (Path(config['readOnlyRoot']) / 'sandbox-must-not-write.txt').write_text('forbidden')
        report['runtimeWriteDenied'] = False
    except PermissionError:
        report['runtimeWriteDenied'] = True
    Path('result.json').write_text(json.dumps(report), encoding='utf-8')
