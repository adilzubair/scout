"""Real socket contract test, with an isolated server and the downloaded English checkpoint."""
import json
import argparse
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--helper', action='store_true')
    parser.add_argument('--models-dir', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    temporary_root = root / 'artifacts' / 'test-tmp'
    temporary_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='scout-http-', dir=temporary_root) as folder:
        ready = Path(folder) / 'ready.json'
        secret = secrets.token_hex(32)
        env = {**os.environ, 'SCOUT_READY_FILE': str(ready), 'SCOUT_MODELS_DIR': str(args.models_dir or root / '.runtime/models'), 'LAYA_API_KEY': secret, 'LAYA_DEVICE': 'cpu', 'SCOUT_PARENT_PID': str(os.getpid())}
        env['SCOUT_SEMANTIC_HELPER'] = '1' if args.helper else '0'
        if args.helper:
            env['LAYA_DEVICE'] = 'cuda'
        process = subprocess.Popen([os.sys.executable, str(root / 'runtime/server.py')], env=env, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0), stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        try:
            for _ in range(600):
                if ready.exists():
                    break
                if process.poll() is not None:
                    raise RuntimeError(process.stderr.read().decode())
                time.sleep(.1)
            else:
                raise TimeoutError('Server startup')
            port = json.loads(ready.read_text())['port']
            def request(path, body=None, auth=True, origin=None):
                headers = {'Content-Type': 'application/json'}
                if auth:
                    headers['Authorization'] = 'Bearer ' + secret
                if origin:
                    headers['Origin'] = origin
                req = urllib.request.Request(f'http://127.0.0.1:{port}/{path}', data=json.dumps(body).encode() if body is not None else None, headers=headers)
                for attempt in range(30):
                    try:
                        with urllib.request.urlopen(req, timeout=10) as response:
                            return response.status, json.load(response)
                    except urllib.error.HTTPError as error:
                        return error.code, json.load(error)
                    except urllib.error.URLError:
                        if attempt == 29:
                            raise
                        time.sleep(.1)
            assert request('health', auth=False)[0] == 401
            assert request('health', origin='https://example.com')[0] == 403
            assert request('health')[1]['status'] == 'ok'
            if args.helper:
                assert request('health')[1]['helper'] == 'ready'
                code, body = request('rank', {'query': 'pdf proving I paid for a purchase', 'candidates': [{'id':'receipt','title':'Purchase receipt.pdf','kind':'file'},{'id':'invoice','title':'Invoice.pdf','kind':'file'}]})
                assert code == 200 and body.get('engine') == 'laya+qwen3-4b' and max(body['scores'],key=body['scores'].get) == 'receipt', (code,body)
            code, body = request('rank', {'query': 'notepad', 'candidates': [{'id': 'notepad', 'title': 'Notepad', 'kind': 'app'}, {'id': 'calc', 'title': 'Calculator', 'kind': 'app'}]})
            assert code == 200 and 'notepad' in body['scores'], (code, body)
            code, body = request('rank', {'query': 'project folder', 'candidates': [{'id': 'folder', 'title': 'Project', 'kind': 'folder'}]})
            assert code == 200 and 'scores' in body, (code, body)
            assert request('rank', {'query': 'notepad', 'candidates': []})[0] == 422
            code, body = request('v1/systemone', {'state': 'notepad', 'questions': {'target': {'type': 'choice', 'instructions': 'Which app is named?', 'criteria': {'A': 'Notepad', 'B': 'Calculator'}}}})
            assert code == 200 and body['answers']['target']['type'] == 'choice', (code, body)
            print('PASS: authenticated loopback HTTP, browser-origin rejection, rank adapter, malformed request, Jev-compatible endpoint')
        finally:
            process.terminate()
            process.wait(timeout=10)
            process.stderr.close()

if __name__ == '__main__':
    main()
