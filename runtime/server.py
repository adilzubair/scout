"""Authenticated loopback sidecar. The model is loaded once, from local pinned weights."""
import asyncio
import ctypes
import json
import os
from pathlib import Path
import secrets
import socket
import threading
import time

os.environ.setdefault('USE_TF', '0')
os.environ.setdefault('HF_HUB_OFFLINE', '1')

def load_agent(model, root, device='cpu', threads=4):
    import torch
    import laya
    torch.set_num_threads(max(1, threads))
    manifest = json.loads(Path(__file__).with_name('models.json').read_text())
    if model not in manifest['checkpoints']:
        raise ValueError('Unknown checkpoint')
    path = Path(root) / model / manifest['checkpoints'][model]
    if not (path / 'model.safetensors').is_file():
        raise RuntimeError('Checkpoint not downloaded. Run setup-laya.ps1.')
    return laya.load(str(path), device=device)

def create_app(agent, secret):
    from fastapi import FastAPI, HTTPException, Request
    from ranking import rank
    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
    lock = threading.Lock()

    @app.middleware('http')
    async def auth(request, call_next):
        from fastapi.responses import JSONResponse
        auth_value = request.headers.get('authorization', '')
        if not secret or not secrets.compare_digest(auth_value, 'Bearer ' + secret):
            return JSONResponse({'detail': 'Unauthorized'}, status_code=401)
        if request.headers.get('origin'):
            return JSONResponse({'detail': 'Browser origins are not allowed'}, status_code=403)
        if int(request.headers.get('content-length', '0')) > 65536:
            return JSONResponse({'detail': 'Request too large'}, status_code=413)
        return await call_next(request)

    async def execute(function):
        if not lock.acquire(blocking=False):
            raise HTTPException(409, 'Inference busy')
        def work():
            try:
                return function()
            finally:
                lock.release()
        # Lock belongs to the actual worker even if the HTTP connection disappears.
        return await asyncio.to_thread(work)

    @app.get('/health')
    async def health():
        return {'status': 'ok', 'model': os.environ.get('LAYA_MODEL', 'english'), 'device': str(agent.device), 'busy': lock.locked()}

    @app.post('/rank')
    async def ranking(request: Request):
        body = await request.json()
        if not isinstance(body, dict):
            raise HTTPException(422, 'Expected an object')
        candidates = body.get('candidates', [])
        if not isinstance(body.get('query'), str) or len(body['query']) > 512 or not isinstance(candidates, list) or not 1 <= len(candidates) <= 32:
            raise HTTPException(422, 'Expected query and 1–32 candidates')
        for c in candidates:
            if not isinstance(c, dict) or not all(isinstance(c.get(k), str) and len(c[k]) <= 2048 for k in ['id', 'title', 'kind']) or c['kind'] not in ['file', 'app', 'link', 'workspace', 'system', 'automation', 'folder']:
                raise HTTPException(422, 'Invalid candidate')
            if not isinstance(c.get('detail', ''), str) or not isinstance(c.get('basis', ''), str) or not isinstance(c.get('evidence', ''), str):
                raise HTTPException(422, 'Invalid candidate metadata')
            if c.get('recency') is not None and (not isinstance(c['recency'], (int, float)) or not __import__('math').isfinite(c['recency'])):
                raise HTTPException(422, 'Invalid recency')
        if len({c['id'] for c in candidates}) != len(candidates):
            raise HTTPException(422, 'Duplicate candidate IDs')
        if not isinstance(body.get('context', {}), dict):
            raise HTTPException(422, 'Invalid context')
        try:
            return await execute(lambda: rank(agent, body['query'], candidates, bool(body.get('plural', False)), context=body.get('context')))
        except ValueError as error:
            raise HTTPException(422, str(error)) from error

    @app.post('/v1/systemone')
    async def systemone(request: Request):
        body = await request.json()
        if not isinstance(body, dict) or not isinstance(body.get('questions'), dict):
            raise HTTPException(422, 'Expected typed questions')
        if len(body['questions']) > 12:
            raise HTTPException(422, 'Too many questions')
        try:
            return await execute(lambda: agent.predict(body.get('state'), body['questions']))
        except (ValueError, KeyError) as error:
            raise HTTPException(422, str(error)) from error
    return app

def monitor_parent(parent_pid):
    if os.name != 'nt' or not parent_pid:
        return
    kernel = ctypes.windll.kernel32
    kernel.OpenProcess.restype = ctypes.c_void_p
    handle = kernel.OpenProcess(0x00100000, False, parent_pid)
    if handle:
        kernel.WaitForSingleObject(ctypes.c_void_p(handle), 0xFFFFFFFF)
        os._exit(0)

def main():
    import uvicorn
    secret = os.environ.get('LAYA_API_KEY')
    if not secret:
        raise RuntimeError('LAYA_API_KEY is required')
    threading.Thread(target=monitor_parent, args=(int(os.environ.get('SCOUT_PARENT_PID', '0')),), daemon=True).start()
    agent = load_agent(os.environ.get('LAYA_MODEL', 'english'), os.environ['SCOUT_MODELS_DIR'], os.environ.get('LAYA_DEVICE', 'cpu'), int(os.environ.get('LAYA_THREADS', '4')))
    # Initialize inference kernels before advertising readiness. Otherwise the
    # first real query pays the cold GPU pass despite successful model loading.
    from ranking import rank
    rank(agent, 'notepad', [{'id': 'warmup', 'kind': 'app', 'title': 'Notepad'}])
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(('127.0.0.1', 0))
    ready = Path(os.environ['SCOUT_READY_FILE'])
    temp = ready.with_suffix('.tmp')
    temp.write_text(json.dumps({'port': sock.getsockname()[1]}))
    temp.replace(ready)
    server = uvicorn.Server(uvicorn.Config(create_app(agent, secret), log_level='error', access_log=False))
    server.run(sockets=[sock])

if __name__ == '__main__':
    main()
