"""Windows integration: authenticated helper and cleanup after sidecar death."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


def main():
    root=Path(__file__).resolve().parents[1]
    if '--child' in sys.argv:
        from semantic_helper import SemanticHelper
        helper=SemanticHelper(root / '.runtime/models')
        Path(sys.argv[-1]).write_text(json.dumps({'pid':helper.process.pid,'port':helper.port}))
        try:
            while True:
                time.sleep(1)
        finally:
            helper.close()
        return
    with tempfile.TemporaryDirectory(prefix='helper-process-',dir=root/'artifacts') as folder:
        ready=Path(folder)/'ready.json'
        child=subprocess.Popen([sys.executable,__file__,'--child',str(ready)],creationflags=subprocess.CREATE_NO_WINDOW)
        handle=None
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
        kernel.OpenProcess.restype=wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes=[wintypes.HANDLE,wintypes.DWORD]
        kernel.CloseHandle.argtypes=[wintypes.HANDLE]
        try:
            for _ in range(600):
                if ready.exists():
                    break
                if child.poll() is not None:
                    raise RuntimeError('Helper owner exited during startup')
                time.sleep(.1)
            else:
                raise TimeoutError('Helper readiness')
            data=json.loads(ready.read_text())
            handle=kernel.OpenProcess(0x00100000,False,data['pid'])
            assert handle
            req=urllib.request.Request(f'http://127.0.0.1:{data["port"]}/completion',data=b'{"prompt":"hello","n_predict":1}',headers={'Content-Type':'application/json'})
            try:
                urllib.request.urlopen(req,timeout=2)
                raise AssertionError('Helper accepted unauthenticated inference')
            except urllib.error.HTTPError as error:
                assert error.code==401
            child.kill()
            child.wait(timeout=5)
            assert kernel.WaitForSingleObject(handle,5000)==0, 'Helper survived its owner'
            print('PASS: helper rejects unauthenticated inference and exits when its owner is killed.')
        finally:
            if child.poll() is None:
                child.kill();child.wait(timeout=5)
            if handle:
                kernel.CloseHandle(handle)


if __name__=='__main__':
    main()
