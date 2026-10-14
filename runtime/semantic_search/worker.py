"""Authenticated offline embedding worker; never logs content or request bodies."""

import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import sys
import threading
import time
from PIL import Image

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from semantic_search.protocol import MAX_BODY, decode_request


class WorkerServer(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 8

    def __init__(self, embedder, secret):
        super().__init__(('127.0.0.1', 0), Handler)
        self.embedder, self.secret = embedder, secret
        self.inference = threading.Lock()
        self.requests = threading.BoundedSemaphore(8)

    def process_request(self, request, client_address):
        if not self.requests.acquire(blocking=False):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except BaseException:
            self.requests.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.requests.release()


class Handler(BaseHTTPRequestHandler):
    def setup(self):
        super().setup()
        self.connection.settimeout(15)

    def log_message(self, *_):
        pass

    def send(self, status, body):
        data = json.dumps(body, allow_nan=False).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Connection', 'close')
        self.end_headers()
        self.close_connection = True
        try:
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def authorized(self):
        host = self.headers.get('Host', '')
        if host != f'127.0.0.1:{self.server.server_port}' or self.headers.get('Origin') or self.headers.get('Sec-Fetch-Site'):
            self.send(403, {'error': 'forbidden'})
            return False
        supplied = self.headers.get('Authorization', '')
        if not hmac.compare_digest(supplied.encode('utf-8'), ('Bearer ' + self.server.secret).encode('ascii')):
            self.send(401, {'error': 'unauthorized'})
            return False
        return True

    def do_GET(self):
        if not self.authorized():
            return
        if self.path != '/health':
            self.send(404, {'error': 'not_found'})
            return
        self.send(200, dict(schemaVersion=1, status='ready', engine='embeddinggemma2',
            pid=os.getpid(), device=self.server.embedder.device, dimensions=self.server.embedder.dimensions,
            modalities=['text', 'image', 'audio', 'video'], revision=self.server.embedder.manifest['revision']))

    def do_POST(self):
        if not self.authorized():
            return
        if self.path != '/embed':
            self.send(404, {'error': 'not_found'})
            return
        length = self.headers.get('Content-Length', '')
        if self.headers.get('Transfer-Encoding') or not length.isdecimal() or not 0 < int(length) <= MAX_BODY:
            self.send(413, {'error': 'body_limit'})
            return
        if self.headers.get_content_type() != 'application/json':
            self.send(415, {'error': 'content_type'})
            return
        # Serialize before reading/decoding large payloads. Interactive scheduling is host-owned.
        if not self.server.inference.acquire(blocking=False):
            self.send(409, {'error': 'busy'})
            return
        try:
            data = self.rfile.read(int(length))
            if len(data) != int(length):
                raise ValueError('Incomplete request')
            content, query, processing = decode_request(json.loads(data))
            vector, tokens = self.server.embedder.embed(content, query=query, processing_kwargs=processing)
            self.send(200, dict(schemaVersion=1, vector=vector.tolist(), tokens=tokens))
        except (ValueError, TypeError, KeyError, UnicodeError, OSError, Image.DecompressionBombError):
            self.send(422, {'error': 'invalid_content'})
        except RuntimeError:
            # Do not expose framework messages, local paths or content in API errors.
            self.send(503, {'error': 'inference_failed'})
        finally:
            self.server.inference.release()


def main():
    from semantic_search.embedding import FullEmbedder
    import psutil
    secret = os.environ['SCOUT_EMBEDDING_TOKEN']
    if not re.fullmatch('[0-9a-fA-F]{64}', secret):
        raise ValueError('Invalid worker credential')
    parent = psutil.Process(int(os.environ['SCOUT_PARENT_PID']))
    parent_identity = parent.create_time()
    ready = Path(os.environ['SCOUT_EMBEDDING_READY'])
    if ready.exists():
        raise FileExistsError('Worker readiness must be fresh')
    def watch_parent():
        while True:
            try:
                if not parent.is_running() or parent.create_time() != parent_identity:
                    os._exit(0)
            except psutil.NoSuchProcess:
                os._exit(0)
            time.sleep(0.5)
    threading.Thread(target=watch_parent, daemon=True).start()
    embedder = FullEmbedder(os.environ['SCOUT_EMBEDDING_MODEL'],
        device=os.environ.get('SCOUT_EMBEDDING_DEVICE', 'cpu'),
        threads=int(os.environ.get('SCOUT_EMBEDDING_THREADS', '4')), dimensions=768)
    # Load all encoders before publishing readiness, then warm the interactive path.
    embedder.embed('worker readiness', query=True)
    with WorkerServer(embedder, secret) as server:
        temporary = ready.with_suffix('.tmp')
        temporary.write_text(json.dumps(dict(schemaVersion=1, port=server.server_port,
            pid=os.getpid(), revision=embedder.manifest['revision'])), encoding='utf-8')
        temporary.replace(ready)
        server.serve_forever(poll_interval=0.2)


if __name__ == '__main__':
    main()
