"""Authored codec, timestamp and bounded-output contracts; not a quality holdout."""
import argparse
import base64
from fractions import Fraction
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import av
import numpy as np
from semantic_search.media_readers import AudioWindows, VideoWindows, MediaBudget, media, opened, audio_track, Counter, joint_windows
from semantic_search.readers import SegmentWriter, image_bytes
from semantic_search.protocol import decode_request

ROOT = Path(__file__).resolve().parents[1]
TEST_ROOT = ROOT/'artifacts/diagnostics/multimodal/media-contracts'
PACK = ROOT/'artifacts/diagnostics/multimodal/media-fixtures-v1'

def tokens(text): return SimpleNamespace(ids=list(range(len(text))), offsets=[(i,i+1) for i in range(len(text))])
def contents(writer): return [json.loads((writer.output/item['name']).read_text()) for item in writer.files]

def sound(path, seconds=3, *, codec='pcm_s16le', rate=48000, silent=False, channels=2):
    with av.open(str(path), 'w') as output:
        stream = output.add_stream(codec, rate=rate)
        stream.layout = 'stereo' if channels == 2 else 'mono'
        for start in range(0, round(seconds*rate), 1024):
            n = min(1024, round(seconds*rate)-start)
            t = (np.arange(n)+start)/rate
            values = np.zeros(n, dtype=np.float32) if silent else (.3*np.sin(2*np.pi*440*t)).astype(np.float32)
            frame = av.AudioFrame.from_ndarray(np.stack([values]*channels), format='fltp', layout=stream.layout)
            frame.sample_rate = rate; frame.pts = start; frame.time_base = Fraction(1,rate)
            for packet in stream.encode(frame): output.mux(packet)
        for packet in stream.encode(None): output.mux(packet)

def movie(path, seconds=3, *, codec='libx264', with_audio=False, pts=None, rate=5):
    with av.open(str(path), 'w') as output:
        video = output.add_stream(codec, rate=rate)
        video.width, video.height, video.pix_fmt = 160, 96, 'yuv420p'
        video.codec_context.thread_count = 1
        if codec == 'libx264': video.options = {'preset': 'ultrafast', 'crf': '18'}
        audio = output.add_stream('aac',rate=48000) if with_audio else None
        if audio: audio.layout = 'stereo'
        for i,tick in enumerate(pts if pts is not None else range(round(seconds*rate))):
            image = np.zeros((96,160,3),dtype=np.uint8)
            image[:,:,i%3] = 210
            image[20:70,30+i%40:60+i%40,:] = 255
            frame = av.VideoFrame.from_ndarray(image, format='rgb24')
            frame.pts = tick; frame.time_base = Fraction(1,rate)
            for packet in video.encode(frame): output.mux(packet)
        for packet in video.encode(None): output.mux(packet)
        if with_audio:
            for start in range(0,round(seconds*48000),1024):
                n = min(1024,round(seconds*48000)-start)
                values = (.2*np.sin(2*np.pi*660*(np.arange(n)+start)/48000)).astype(np.float32)
                frame = av.AudioFrame.from_ndarray(np.stack([values,values]),format='fltp',layout='stereo')
                frame.sample_rate = 48000; frame.pts = start; frame.time_base = Fraction(1,48000)
                for packet in audio.encode(frame): output.mux(packet)
            for packet in audio.encode(None): output.mux(packet)

def author_pack():
    PACK.mkdir(parents=True,exist_ok=True)
    items = []
    for file,codec in [('tone.wav','pcm_s16le'),('tone.mp3','libmp3lame'),('tone.m4a','aac'),('tone.flac','flac')]:
        sound(PACK/file,codec=codec); items.append(dict(file=file,family='audio',codec=codec,seconds=3))
    for file,codec,audio in [('scene.mp4','libx264',True),('scene.mov','libx264',False),
                             ('scene.mkv','libx264',True),('scene.webm','libvpx',False)]:
        movie(PACK/file,codec=codec,with_audio=audio); items.append(dict(file=file,family='video',codec=codec,seconds=3,audio=audio))
    sound(PACK/'long.wav',seconds=26); items.append(dict(file='long.wav',family='audio',codec='pcm_s16le',seconds=26))
    sound(PACK/'silent.wav',silent=True); items.append(dict(file='silent.wav',family='audio',codec='pcm_s16le',seconds=3))
    movie(PACK/'long.mp4',seconds=18); items.append(dict(file='long.mp4',family='video',codec='libx264',seconds=18,audio=False))
    movie(PACK/'variable.mkv',pts=[0,1,5,8,11,15]); items.append(dict(file='variable.mkv',family='video',codec='libx264',audio=False))
    for file,codec in [('tone.ogg','libopus'),('tone.aac','aac')]:
        sound(PACK/file,codec=codec); items.append(dict(file=file,family='audio',codec=codec,seconds=3))
    for file,codec in [('scene.avi','mpeg4'),('scene.m4v','libx264')]:
        movie(PACK/file,codec=codec); items.append(dict(file=file,family='video',codec=codec,seconds=3,audio=False))
    (PACK/'fixtures.json').write_text(json.dumps(dict(schemaVersion=1,description='Authored tone, silence and moving colored shapes; smoke fixtures only',items=items)),encoding='utf-8')
    return items

class MediaReaders(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        TEST_ROOT.mkdir(parents=True,exist_ok=True)
        cls.items = author_pack()
    def run_reader(self, file, family, budget=None, **limits):
        folder = tempfile.TemporaryDirectory(dir=TEST_ROOT); self.addCleanup(folder.cleanup)
        writer = SegmentWriter(Path(folder.name)/'out',tokens,**limits)
        media(PACK/file,writer,family,budget)
        return writer, contents(writer)
    def test_required_real_codec_combinations_prepare_valid_inputs(self):
        for item in self.items[:8]:
            with self.subTest(file=item['file']):
                writer, segments = self.run_reader(item['file'],item['family'])
                self.assertTrue(segments)
                self.assertLessEqual(len(segments),3,'coarse stream timestamps must not split every audio frame')
                self.assertIn(item['family'],{s['modality'] for s in segments})
                if item.get('audio'): self.assertIn('audio',{s['modality'] for s in segments})
                for segment in segments:
                    decode_request(dict(schemaVersion=1,mode='document',content=segment['content']))
                    start,end = (segment['locator'][k] for k in ('startSeconds','endSeconds'))
                    self.assertTrue(0 <= start < end <= 3.1,(start,end))
    def test_audio_resampling_and_overlap_retain_the_entire_timeline(self):
        writer, segments = self.run_reader('long.wav','audio')
        self.assertEqual([(s['locator']['startSeconds'],s['locator']['endSeconds']) for s in segments],[(0,12),(10,22),(20,26)])
        self.assertFalse(writer.reasons)
        for segment in segments:
            samples = np.frombuffer(base64.b64decode(segment['content']['data']),dtype='<f4')
            self.assertEqual(len(samples),round((segment['locator']['endSeconds']-segment['locator']['startSeconds'])*16000))
            self.assertTrue(np.isfinite(samples).all()); self.assertAlmostEqual(float(np.max(samples)),.3,places=3)
    def test_variable_decoded_block_sizes_resample_as_one_continuous_timeline(self):
        folder=tempfile.TemporaryDirectory(dir=TEST_ROOT);self.addCleanup(folder.cleanup)
        writer=SegmentWriter(Path(folder.name)/'out',tokens)
        frames=[];start=0
        while start<26*44100:
            n=min((128,2048,256,1024)[len(frames)%4],26*44100-start)
            values=(.3*np.sin(2*np.pi*440*(np.arange(n)+start)/44100)).astype(np.float32)
            frame=av.AudioFrame.from_ndarray(np.stack([values]*2),format='fltp',layout='stereo')
            frame.sample_rate=44100;frame.pts=start;frame.time_base=Fraction(1,44100)
            frames.append(frame);start+=n
        budget=MediaBudget();stream=SimpleNamespace(time_base=Fraction(1,44100))
        with patch('semantic_search.media_readers.decoded',return_value=iter(frames)):
            audio_track(SimpleNamespace(start_time=0),stream,writer,budget,Counter(budget))
        segments=contents(writer)
        self.assertNotIn('audio_timeline_gap',writer.reasons)
        self.assertEqual(len(segments),3)
        self.assertAlmostEqual(segments[0]['locator']['startSeconds'],0,places=3)
        self.assertGreater(segments[-1]['locator']['endSeconds'],25.9)
        for first,second in zip(segments,segments[1:]):
            self.assertAlmostEqual(first['locator']['endSeconds']-second['locator']['startSeconds'],2,places=3)
    def test_bounded_clock_adjustment_is_reported_without_tiny_fragments(self):
        folder=tempfile.TemporaryDirectory(dir=TEST_ROOT);self.addCleanup(folder.cleanup)
        writer=SegmentWriter(Path(folder.name)/'out',tokens);windows=AudioWindows(writer,MediaBudget())
        # Measured prepared PTS/sample pattern from the independently acquired clip.
        windows.add(0,np.zeros(7252,dtype=np.float32))
        windows.add(.4634375,np.zeros(209,dtype=np.float32))
        windows.add(.4663125,np.zeros(15209,dtype=np.float32));windows.flush()
        segments=contents(writer)
        self.assertEqual(len(segments),1)
        self.assertIn('audio_timestamp_adjusted',writer.reasons)
        self.assertNotIn('audio_timeline_gap',writer.reasons)
        self.assertEqual(segments[0]['locator'],dict(kind='time',startSeconds=0,endSeconds=22670/16000))
        self.assertEqual(len(base64.b64decode(segments[0]['content']['data'])),22670*4)
    def test_clock_adjustment_does_not_hide_gaps_outside_twenty_milliseconds(self):
        folder=tempfile.TemporaryDirectory(dir=TEST_ROOT);self.addCleanup(folder.cleanup)
        writer=SegmentWriter(Path(folder.name)/'out',tokens);windows=AudioWindows(writer,MediaBudget())
        windows.add(0,np.zeros(16000,dtype=np.float32))
        windows.add(1.025,np.zeros(16000,dtype=np.float32));windows.flush()
        self.assertIn('audio_timeline_gap',writer.reasons)
        self.assertNotIn('audio_timestamp_adjusted',writer.reasons)
        self.assertEqual([(s['locator']['startSeconds'],s['locator']['endSeconds']) for s in contents(writer)],[(0,1),(1.025,2.025)])
    def test_clock_adjustment_cannot_accumulate_unbounded_drift(self):
        folder=tempfile.TemporaryDirectory(dir=TEST_ROOT);self.addCleanup(folder.cleanup)
        writer=SegmentWriter(Path(folder.name)/'out',tokens);windows=AudioWindows(writer,MediaBudget())
        windows.add(0,np.zeros(16000,dtype=np.float32))
        windows.add(1.015,np.zeros(16000,dtype=np.float32))
        windows.add(2.030,np.zeros(16000,dtype=np.float32));windows.flush()
        self.assertIn('audio_timestamp_adjusted',writer.reasons);self.assertIn('audio_timeline_gap',writer.reasons)
        self.assertEqual([(s['locator']['startSeconds'],s['locator']['endSeconds']) for s in contents(writer)],[(0,2),(2.03,3.03)])
    def test_additional_exposed_formats_have_real_decoder_coverage(self):
        for item in self.items[12:]:
            with self.subTest(file=item['file']):
                writer, segments = self.run_reader(item['file'],item['family'])
                self.assertTrue(segments)
                for segment in segments: decode_request(dict(schemaVersion=1,mode='document',content=segment['content']))
    def test_silence_is_embedded_without_fabricating_a_transcript(self):
        _, segments = self.run_reader('silent.wav','audio')
        samples = np.frombuffer(base64.b64decode(segments[0]['content']['data']),dtype='<f4')
        self.assertTrue(np.all(samples == 0)); self.assertNotIn('transcript',segments[0]['evidence'].lower())
    def test_video_sampling_overlap_and_exact_sample_receipts(self):
        writer, segments = self.run_reader('long.mp4','video')
        self.assertIn('video_frames_sampled',writer.reasons)
        self.assertEqual([(s['locator']['startSeconds'],round(s['locator']['endSeconds'],3)) for s in segments],[(0,8),(7,15),(14,18)])
        for segment in segments:
            data = segment['content']; times = segment['coverage']['sampleTimesSeconds']
            self.assertLessEqual(len(times),16); self.assertEqual(len(times),len(data['frames']))
            self.assertTrue(segment['thumbnail'])
            for tick,t in zip(data['indices'],times): self.assertLessEqual(abs(t-(segment['locator']['startSeconds']+tick/data['fps'])),.005001)
    def test_variable_rate_sampling_uses_pts_and_retains_final_duration(self):
        _, segments = self.run_reader('variable.mkv','video')
        self.assertEqual(segments[0]['coverage']['sampleTimesSeconds'],[0,1,2.2])
        self.assertAlmostEqual(segments[0]['locator']['endSeconds'],3.2,places=3)
    def test_duration_frame_and_output_limits_are_visible(self):
        for family,file in [('audio','long.wav'),('video','long.mp4')]:
            writer, segments = self.run_reader(file,family,MediaBudget(seconds=2.5))
            self.assertIn('media_duration_limit',writer.reasons)
            self.assertTrue(segments); self.assertTrue(all(s['locator']['endSeconds'] <=2.5 for s in segments))
            writer, segments = self.run_reader(file,family,MediaBudget(decode_frames=2))
            self.assertIn('decode_frame_limit',writer.reasons)
        writer, segments = self.run_reader('long.wav','audio',max_segments=1)
        self.assertEqual(len(segments),1); self.assertIn('segment_limit',writer.reasons)
        writer, segments = self.run_reader('scene.mp4','video',max_bytes=1)
        self.assertEqual(segments,[]); self.assertIn('prepared_output_limit',writer.reasons)
    def test_audio_gaps_keep_real_timestamps_instead_of_inserting_silence(self):
        folder = tempfile.TemporaryDirectory(dir=TEST_ROOT); self.addCleanup(folder.cleanup)
        writer = SegmentWriter(Path(folder.name)/'out',tokens)
        windows = AudioWindows(writer,MediaBudget())
        windows.add(0,np.ones(16000,dtype=np.float32)*.2)
        windows.add(3,np.ones(16000,dtype=np.float32)*.2); windows.flush()
        self.assertIn('audio_timeline_gap',writer.reasons)
        self.assertEqual([(s['locator']['startSeconds'],s['locator']['endSeconds']) for s in contents(writer)],[(0,1),(3,4)])
    def test_matroska_millisecond_audio_timestamps_do_not_invent_gaps(self):
        writer, segments = self.run_reader('scene.mkv','video')
        self.assertEqual([s['modality'] for s in segments],['video','audio','video'])
        self.assertIn('audio',segments[-1]['content'])
        self.assertNotIn('audio_timeline_gap',writer.reasons)
        self.assertGreater(segments[1]['locator']['endSeconds'],2.99)
    def test_regressing_or_nonfinite_audio_is_rejected(self):
        folder = tempfile.TemporaryDirectory(dir=TEST_ROOT); self.addCleanup(folder.cleanup)
        writer = SegmentWriter(Path(folder.name)/'out',tokens)
        windows = AudioWindows(writer,MediaBudget())
        with self.assertRaises(ValueError): windows.add(0,np.array([np.nan],dtype=np.float32))
        windows.add(0,np.zeros(16000,dtype=np.float32)); windows.flush()
        with self.assertRaises(ValueError): windows.add(0,np.zeros(16000,dtype=np.float32))
    def test_corrupt_media_playlists_and_wrong_track_family_are_rejected(self):
        folder = tempfile.TemporaryDirectory(dir=TEST_ROOT); self.addCleanup(folder.cleanup)
        fake = Path(folder.name)/'input.mp4'; fake.write_text('#EXTM3U\nhttps://127.0.0.1/private\n')
        writer = SegmentWriter(Path(folder.name)/'out',tokens)
        with self.assertRaises(Exception): media(fake,writer,'video')
        self.assertFalse(writer.files)
        with self.assertRaises(ValueError): media(PACK/'tone.m4a',writer,'video')
        with self.assertRaises(ValueError):
            with opened(Path(folder.name)/'input.m3u8'): pass
    def test_prepared_receipts_include_limits_and_hashes(self):
        writer,_ = self.run_reader('scene.mp4','video')
        ready = writer.finish(PACK/'scene.mp4')
        self.assertTrue(ready['partial']); self.assertEqual(ready['coverage']['audioTracks'],1)
        self.assertEqual(ready['coverage']['videoSampleIntervalSeconds'],1)
        self.assertEqual(ready['coverage']['audioSegments'],1); self.assertEqual(ready['coverage']['videoSegments'],1)
        self.assertEqual(ready['coverage']['jointAvSegments'],1)
        for receipt in ready['segments']: self.assertEqual((writer.output/receipt['name']).stat().st_size,receipt['bytes'])
    def test_sparse_held_video_frames_never_exceed_the_window_budget(self):
        from PIL import Image
        folder = tempfile.TemporaryDirectory(dir=TEST_ROOT); self.addCleanup(folder.cleanup)
        writer = SegmentWriter(Path(folder.name)/'out',tokens)
        sink = VideoWindows(writer,MediaBudget())
        data,thumbnail = image_bytes(Image.new('RGB',(64,64),'blue'))
        sink.add(0,data,thumbnail); sink.emit(120)
        self.assertIn('video_window_coverage_limit',writer.reasons)
        segment = contents(writer)[0]
        self.assertEqual(segment['locator']['endSeconds'],8)
        decode_request(dict(schemaVersion=1,mode='document',content=segment['content']))

    def test_joint_windows_stitch_overlapping_audio_by_sample_position(self):
        from PIL import Image
        folder=tempfile.TemporaryDirectory(dir=TEST_ROOT);self.addCleanup(folder.cleanup)
        writer=SegmentWriter(Path(folder.name)/'out',tokens)
        picture,thumbnail=image_bytes(Image.new('RGB',(64,64),'green'))
        for start in (0,7):
            writer.emit(dict(type='video',frames=[picture],fps=100,indices=[0],totalFrames=800),
                dict(kind='time',startSeconds=start,endSeconds=start+8),thumbnail=thumbnail,coverage=dict(sampleTimesSeconds=[start]))
        videos=writer.files.copy();source=(np.arange(22*16000,dtype=np.float32)%16000/32000).astype('<f4')
        for start in (0,10):
            writer.emit(dict(type='audio',data=base64.b64encode(source[start*16000:(start+12)*16000]).decode(),sampleRate=16000),
                dict(kind='time',startSeconds=start,endSeconds=start+12))
        audios=writer.files[2:].copy()
        self.assertEqual(joint_windows(writer,videos,audios,Counter(MediaBudget())),2)
        for start,segment in zip((0,7),contents(writer)[4:]):
            values=np.frombuffer(base64.b64decode(segment['content']['audio']['data']),dtype='<f4')
            np.testing.assert_array_equal(values,source[start*16000:(start+8)*16000])
            decoded,_,_=decode_request(dict(schemaVersion=1,mode='document',content=segment['content']))
            self.assertEqual(set(decoded),{'audio','video'})
        self.assertFalse(writer.reasons)

    def test_joint_windows_never_pad_missing_audio_and_respect_segment_limit(self):
        from PIL import Image
        for gap,limit in [(True,256),(False,2)]:
            with self.subTest(gap=gap):
                folder=tempfile.TemporaryDirectory(dir=TEST_ROOT);self.addCleanup(folder.cleanup)
                writer=SegmentWriter(Path(folder.name)/'out',tokens,max_segments=limit)
                picture,thumbnail=image_bytes(Image.new('RGB',(64,64),'green'))
                writer.emit(dict(type='video',frames=[picture],fps=100,indices=[0],totalFrames=800),
                    dict(kind='time',startSeconds=0,endSeconds=8),thumbnail=thumbnail,coverage=dict(sampleTimesSeconds=[0]))
                videos=writer.files.copy();samples=np.zeros((7 if gap else 8)*16000,dtype='<f4')
                writer.emit(dict(type='audio',data=base64.b64encode(samples).decode(),sampleRate=16000),
                    dict(kind='time',startSeconds=1 if gap else 0,endSeconds=8))
                self.assertEqual(joint_windows(writer,videos,writer.files[1:].copy(),Counter(MediaBudget())),0)
                self.assertIn('joint_av_audio_incomplete' if gap else 'joint_av_not_indexed',writer.reasons)
                self.assertEqual(len(writer.files),2)

if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--author-pack',action='store_true')
    args,rest = parser.parse_known_args()
    if args.author_pack: print(json.dumps(dict(pack=str(PACK),items=len(author_pack()))))
    else: unittest.main(argv=[__file__]+rest)
