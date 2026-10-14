"""Authored playback compatibility and bounds, separate from retrieval quality."""
import json
from pathlib import Path
import shutil
import tempfile
import unittest

from semantic_search.manifest import sha256
from semantic_search.media_readers import opened, origin, timestamp
from semantic_search.playback import prepare

ROOT = Path(__file__).resolve().parents[1]
PACK = ROOT/'artifacts/diagnostics/multimodal/media-fixtures-v1'
WORK = ROOT/'artifacts/diagnostics/multimodal/playback-contracts'


class PlaybackChecks(unittest.TestCase):
    def setUp(self):
        WORK.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=WORK)
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def request(self, name, start=.5, end=2.5):
        source = self.root/('input'+Path(name).suffix)
        shutil.copyfile(PACK/name, source)
        data = dict(schemaVersion=1, source=source.name, sourceSha256=sha256(source),
                    family='audio' if name.startswith(('tone', 'long.wav', 'silent')) else 'video',
                    startSeconds=start, endSeconds=end)
        request = self.root/'request.json'
        request.write_text(json.dumps(data), encoding='utf-8')
        return request, data

    def test_codecs_and_timestamps(self):
        for name in ('tone.wav','tone.mp3','tone.m4a','tone.flac','tone.ogg','tone.aac',
                     'scene.mp4','scene.mov','scene.mkv','scene.webm','scene.avi','scene.m4v',
                     'variable.mkv','long.mp4','long.wav','silent.wav'):
            with self.subTest(name=name):
                request, data = self.request(name)
                result = prepare(request)
                output = self.root/result['name']
                self.assertEqual(sha256(output), result['sha256'])
                self.assertEqual(data['sourceSha256'], result['sourceSha256'])
                self.assertGreater(result['endSeconds'], result['startSeconds'])
                self.assertLessEqual(result['bytes'], 32*1024**2)
                with opened(output) as container:
                    offset = origin(container)
                    times = [timestamp(frame, offset) for frame in container.decode()]
                    self.assertTrue(times)
                    self.assertGreaterEqual(min(times), -.1)
                    self.assertLessEqual(max(times), 2.2)
                    if data['family']=='video':
                        self.assertEqual(container.streams.video[0].codec_context.name, 'h264')

    def test_later_window_is_bounded(self):
        for name in ('long.wav','long.mp4'):
            with self.subTest(name=name):
                request, _ = self.request(name, 11, 14)
                result = prepare(request)
                self.assertGreaterEqual(result['startSeconds'], 10.9)
                self.assertLessEqual(result['endSeconds'], 14.1)
                with opened(self.root/result['name']) as container:
                    self.assertLessEqual(container.duration/1_000_000, 3.2)

    def test_rejects_invalid_requests_and_changed_snapshot(self):
        request, original = self.request('tone.wav')
        for changes in (dict(schemaVersion=True), dict(startSeconds=True), dict(endSeconds=30),
                        dict(startSeconds=-1), dict(endSeconds=float('nan')), dict(source='../tone.wav'),
                        dict(sourceSha256='0'*64), dict(family='image')):
            with self.subTest(changes=changes):
                request.write_text(json.dumps(original | changes), encoding='utf-8')
                with self.assertRaises(ValueError):
                    prepare(request)

    def test_refuses_external_media(self):
        request, data = self.request('scene.mp4')
        source = self.root/data['source']
        source.write_text('#EXTM3U\nhttps://example.invalid/private.mp4\n')
        data['sourceSha256'] = sha256(source)
        request.write_text(json.dumps(data), encoding='utf-8')
        with self.assertRaises(Exception):
            prepare(request)


if __name__=='__main__':
    unittest.main()
