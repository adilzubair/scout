"""Full-model process and HTTP smoke test with outgoing Python sockets blocked."""

import argparse
import base64
import io
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

import numpy as np
from PIL import Image
from semantic_search.processes import stop_tree

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--device', choices=['cpu', 'cuda'], default='cpu')
    args = parser.parse_args()
    temporary = ROOT / 'artifacts/diagnostics/multimodal/worker-contracts'
    temporary.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=temporary) as directory:
        work = Path(directory)
        ready = work / 'ready.json'
        secret = secrets.token_hex(32)
        wrapper = work / 'offline.py'
        wrapper.write_text('import socket, runpy\n'
            'def forbidden(*args, **kwargs):\n    raise AssertionError("Outgoing network forbidden")\n'
            'socket.socket.connect = forbidden\nsocket.socket.connect_ex = forbidden\n'
            f'runpy.run_path({str(ROOT / "runtime/semantic_search/worker.py")!r}, run_name="__main__")\n')
        env = {**os.environ, 'SCOUT_EMBEDDING_TOKEN': secret, 'SCOUT_EMBEDDING_MODEL': str(args.model.resolve()),
               'SCOUT_EMBEDDING_READY': str(ready), 'SCOUT_PARENT_PID': str(os.getpid()),
               'SCOUT_EMBEDDING_DEVICE': args.device, 'HF_HUB_OFFLINE': '1', 'TRANSFORMERS_OFFLINE': '1'}
        with (work / 'process.log').open('wb') as log:
            process = subprocess.Popen([os.sys.executable, str(wrapper)], env=env, stdout=log, stderr=log,
                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            try:
                start = time.monotonic()
                while not ready.is_file():
                    if process.poll() is not None:
                        raise RuntimeError((work / 'process.log').read_text(errors='replace')[-2000:])
                    if time.monotonic()-start > 120:
                        raise TimeoutError('Worker startup')
                    time.sleep(0.1)
                endpoint = json.loads(ready.read_text())
                import psutil
                owned = {process.pid, *[child.pid for child in psutil.Process(process.pid).children(recursive=True)]}
                assert endpoint['pid'] in owned
                def request(path, body=None):
                    req = urllib.request.Request(f'http://127.0.0.1:{endpoint["port"]}/{path}',
                        headers={'Authorization': 'Bearer '+secret, 'Content-Type': 'application/json'},
                        data=json.dumps(body).encode() if body is not None else None)
                    with urllib.request.urlopen(req, timeout=60) as response:
                        return json.load(response)
                assert request('health')['modalities'] == ['text', 'image', 'audio', 'video']
                output = io.BytesIO()
                Image.new('RGB', (64, 64), 'blue').save(output, format='PNG')
                image = base64.b64encode(output.getvalue()).decode()
                audio = base64.b64encode((np.sin(np.arange(16000)*0.1)*0.2).astype('<f4')).decode()
                for body in [dict(schemaVersion=1, mode='query', text='a blue picture'),
                    *[dict(schemaVersion=1, mode='document', content=content) for content in [
                        dict(type='text', text='meeting notes about deployment'), dict(type='image', data=image),
                        dict(type='audio', data=audio, sampleRate=16000),
                        dict(type='video', frames=[image, image], indices=[0, 1], fps=1, totalFrames=2),
                        dict(type='video',frames=[image,image],indices=[0,1],fps=1,totalFrames=2,
                            audio=dict(data=base64.b64encode(np.zeros(32000,dtype='<f4')).decode(),sampleRate=16000))]]]:
                    result = request('embed', body)
                    vector = np.asarray(result['vector'])
                    assert len(vector) == 768 and np.isfinite(vector).all() and abs(np.linalg.norm(vector)-1) < 1e-5
                print(f'PASS: independent {args.device} worker, authenticated HTTP, full-model text/image/audio/video and joint AV, outgoing sockets blocked')
            finally:
                stop_tree(process)


if __name__ == '__main__':
    main()
