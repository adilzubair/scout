"""Real loopback boundary contracts using an injected inference double."""

import base64
import io
import json
import threading
import unittest
import urllib.error
import urllib.request

import numpy as np
from PIL import Image
from semantic_search.manifest import load_manifest
from semantic_search.protocol import decode_request
from semantic_search.worker import WorkerServer


def picture():
    data = io.BytesIO()
    Image.new('RGB', (64, 64), 'blue').save(data, format='PNG')
    return base64.b64encode(data.getvalue()).decode()


def document(content):
    return dict(schemaVersion=1, mode='document', content=content)


class FakeEmbedder:
    device = 'cpu'
    dimensions = 768
    manifest = load_manifest()
    def __init__(self):
        self.entered, self.release = threading.Event(), threading.Event()
        self.block = False
    def embed(self, content, **kwargs):
        if self.block:
            self.entered.set()
            self.release.wait(5)
        return np.full(768, 1/np.sqrt(768), dtype=np.float32), 100


class WorkerContracts(unittest.TestCase):
    def setUp(self):
        self.embedder = FakeEmbedder()
        self.server = WorkerServer(self.embedder, 'a'*64)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.embedder.release.set()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(5)

    def request(self, path='health', body=None, auth=True, headers=None):
        fields = {'Authorization': 'Bearer ' + 'a'*64} if auth else {}
        if body is not None:
            fields['Content-Type'] = 'application/json'
        fields.update(headers or {})
        request = urllib.request.Request(f'http://127.0.0.1:{self.server.server_port}/{path}',
            data=json.dumps(body).encode() if body is not None else None, headers=fields)
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status, json.load(response)
        except urllib.error.HTTPError as error:
            return error.code, json.load(error)

    def test_health_requires_secret_and_refuses_browser_origin_or_foreign_host(self):
        self.assertEqual(self.request(auth=False)[0], 401)
        self.assertEqual(self.request(headers={'Authorization': 'Bearer é'})[0], 401)
        self.assertEqual(self.request(headers={'Origin': 'https://example.com'})[0], 403)
        self.assertEqual(self.request(headers={'Host': 'example.com'})[0], 403)
        self.assertEqual(self.request(headers={'Sec-Fetch-Site': 'same-origin'})[0], 403)
        health = self.request()[1]
        self.assertEqual(health['modalities'], ['text', 'image', 'audio', 'video'])
        self.assertEqual(health['revision'], load_manifest()['revision'])

    def test_all_prepared_modalities_cross_the_authenticated_protocol(self):
        for content in [dict(type='text', text='meeting notes'), dict(type='image', data=picture()),
                        dict(type='audio', data=base64.b64encode(np.zeros(16000, dtype='<f4')).decode(), sampleRate=16000),
                        dict(type='video', frames=[picture(), picture()], fps=1, indices=[0, 1], totalFrames=2)]:
            status, result = self.request('embed', document(content))
            self.assertEqual(status, 200)
            self.assertEqual(len(result['vector']), 768)

    def test_files_urls_unknown_fields_and_invalid_audio_are_rejected(self):
        for content in [dict(type='image', path='C:/private.png'), dict(type='image', data='https://example.com/image'),
                        dict(type='audio', data=base64.b64encode(np.full(10, np.nan, dtype='<f4')).decode(), sampleRate=16000),
                        dict(type='audio', data=base64.b64encode(np.zeros(100, dtype='<f4')).decode(), sampleRate=48000),
                        dict(type='text', text='notes', path='C:/private')]:
            self.assertEqual(self.request('embed', document(content))[0], 422)

    def test_video_indices_and_budget_do_not_allow_silent_truncation(self):
        for extra in [dict(indices=[1, 0]), dict(indices=[0, 0]), dict(totalFrames=1),
                      dict(frames=[picture()]*17, indices=list(range(17)), totalFrames=17), dict(fps=float('nan')),
                      dict(totalFrames=100000, fps=1)]:
            content = dict(type='video', frames=[picture(), picture()], indices=[0, 1], fps=1, totalFrames=2)
            content.update(extra)
            with self.assertRaises(ValueError):
                decode_request(document(content))

    def test_joint_video_audio_has_strict_nested_fields_and_one_duration(self):
        sound=dict(data=base64.b64encode(np.zeros(32000,dtype='<f4')).decode(),sampleRate=16000)
        content=dict(type='video',frames=[picture(),picture()],indices=[0,1],fps=1,totalFrames=2,audio=sound)
        decoded,_,processing=decode_request(document(content))
        self.assertEqual(set(decoded),{'video','audio'});self.assertEqual(len(decoded['audio']),32000)
        self.assertFalse(processing['video']['do_sample_frames'])
        self.assertEqual(self.request('embed',document(content))[0],200)
        for invalid in [dict(sound,url='https://example.com/audio'),dict(sound,sampleRate=True),dict(sound,sampleRate=48000),
            dict(sound,data=base64.b64encode(np.zeros(16000,dtype='<f4')).decode()),
            dict(sound,data=base64.b64encode(np.full(32000,np.nan,dtype='<f4')).decode())]:
            self.assertEqual(self.request('embed',document(dict(content,audio=invalid)))[0],422)

    def test_busy_requests_cannot_queue_unbounded_decoding_or_parallel_inference(self):
        self.embedder.block = True
        results = []
        thread = threading.Thread(target=lambda: results.append(self.request('embed', dict(schemaVersion=1, mode='query', text='first'))))
        thread.start()
        try:
            self.assertTrue(self.embedder.entered.wait(5))
            self.assertEqual(self.request('embed', dict(schemaVersion=1, mode='query', text='second'))[0], 409)
        finally:
            self.embedder.release.set()
            thread.join(10)
        self.assertEqual(results[0][0], 200)

    def test_content_type_size_and_unknown_routes_are_explicit(self):
        body = dict(schemaVersion=1, mode='query', text='query')
        self.assertEqual(self.request('embed', body, headers={'Content-Type': 'text/plain'})[0], 415)
        self.assertEqual(self.request('embed', body, headers={'Content-Length': str(33*1024**2)})[0], 413)
        self.assertEqual(self.request('unknown')[0], 404)
        self.assertEqual(self.request('embed', dict(schemaVersion=2, mode='query', text='query'))[0], 422)


if __name__ == '__main__':
    unittest.main()
