"""Streaming local media readers. The production CLI requires a no-network sandbox."""
import base64
from contextlib import contextmanager
from dataclasses import dataclass
import math
from pathlib import Path
import time

from .readers import image_bytes

RATE = 16000
# Explicit demuxers prevent a renamed playlist/device from selecting another reader.
FORMATS = {'.wav': 'wav', '.mp3': 'mp3', '.m4a': 'mov', '.aac': 'aac',
           '.flac': 'flac', '.ogg': 'ogg', '.mp4': 'mov', '.mov': 'mov',
           '.m4v': 'mov', '.mkv': 'matroska', '.webm': 'matroska', '.avi': 'avi'}

@dataclass(frozen=True)
class MediaBudget:
    seconds: float = 600
    decode_frames: int = 60000
    decode_packets: int = 200000
    wall_seconds: float = 90
    audio_window: float = 12
    audio_overlap: float = 2
    video_window: float = 8
    video_overlap: float = 1
    sample_interval: float = 1

    def __post_init__(self):
        values = (self.seconds, self.wall_seconds, self.audio_window, self.audio_overlap,
                  self.video_window, self.video_overlap, self.sample_interval)
        if (any(type(v) not in (int, float) or not math.isfinite(v) for v in values)
                or not 0 < self.seconds <= 600 or not 0 < self.wall_seconds <= 90
                or not 0 < self.audio_window <= 12 or not 0 <= self.audio_overlap < self.audio_window
                or not 0 < self.video_window <= 8 or not 0 <= self.video_overlap < self.video_window
                or not self.video_window/16 <= self.sample_interval <= self.video_window
                or type(self.decode_frames) is not int or not 1 <= self.decode_frames <= 60000
                or type(self.decode_packets) is not int or not 1 <= self.decode_packets <= 200000):
            raise ValueError('media_budget')

class DecodeLimit(Exception):
    pass

class Counter:
    def __init__(self, budget):
        self.budget, self.started = budget, time.monotonic()
        self.frames = self.packets = 0
    def tick(self, packet=False):
        self.packets += int(packet); self.frames += int(not packet)
        if time.monotonic()-self.started > self.budget.wall_seconds: raise DecodeLimit('media_time_limit')
        if self.frames > self.budget.decode_frames: raise DecodeLimit('decode_frame_limit')
        if self.packets > self.budget.decode_packets: raise DecodeLimit('decode_packet_limit')

def deny_external(url, flags, options):
    raise ValueError('external_media_resource')

@contextmanager
def opened(source):
    import av
    # Codec diagnostics can contain source metadata; only bounded reason codes leave the CLI.
    av.logging.set_level(av.logging.PANIC)
    demuxer = FORMATS.get(Path(source).suffix.lower())
    if not demuxer: raise ValueError('unsupported_media_format')
    # Supply one already-open snapshot, deny all secondary I/O and URL protocols.
    with Path(source).open('rb') as input:
        with av.open(input, mode='r', format=demuxer, io_open=deny_external,
                     options={'protocol_whitelist': '', 'enable_drefs': '0', 'use_absolute_path': '0',
                              'probesize': '1048576', 'analyzeduration': '5000000', 'err_detect': 'explode'}) as container:
            if len(container.streams) > 64: raise ValueError('media_stream_limit')
            yield container

def origin(container):
    import av
    if container.start_time is not None:
        value = container.start_time/av.time_base
    else:
        starts = [float(s.start_time*s.time_base) for s in container.streams
                  if s.type in ('audio', 'video') and s.start_time is not None and s.time_base is not None]
        value = min(starts, default=0)
    if not math.isfinite(value) or abs(value) > 864000: raise ValueError('media_timestamp')
    return value

def timestamp(frame, offset):
    if frame.pts is None or frame.time_base is None: raise ValueError('media_timestamp_missing')
    value = float(frame.pts*frame.time_base)-offset
    if not math.isfinite(value) or not -60 <= value <= 864000: raise ValueError('media_timestamp')
    return value

def decoded(container, stream, counter):
    stream.codec_context.thread_count = 1
    for packet in container.demux(stream):
        counter.tick(packet=True)
        if packet.size > 16*1024**2: raise ValueError('media_packet_budget')
        for frame in packet.decode():
            counter.tick()
            if frame.is_corrupt: raise ValueError('corrupt_media_frame')
            yield frame

class AudioWindows:
    def __init__(self, writer, budget):
        import numpy as np
        self.writer, self.budget = writer, budget
        self.window = round(budget.audio_window*RATE)
        self.step = self.window-round(budget.audio_overlap*RATE)
        if self.step <= 0: raise ValueError('media_budget')
        self.samples, self.start, self.last_end = np.empty(0, dtype='<f4'), None, -1

    def emit(self):
        if not len(self.samples) or self.start+len(self.samples) <= self.last_end: return True
        end = self.start+len(self.samples)
        data = base64.b64encode(self.samples.astype('<f4', copy=False).tobytes()).decode('ascii')
        ok = self.writer.emit(dict(type='audio', data=data, sampleRate=RATE),
                              dict(kind='time', startSeconds=self.start/RATE, endSeconds=end/RATE),
                              'Audio window; speech and environmental sound.')
        if ok: self.last_end = end
        return ok

    def flush(self):
        import numpy as np
        ok = self.emit()
        self.samples, self.start = np.empty(0, dtype='<f4'), None
        return ok

    def add(self, seconds, samples, *, tolerance_samples=2):
        import numpy as np
        samples = np.asarray(samples, dtype='<f4').reshape(-1)
        if not len(samples) or not np.isfinite(samples).all(): raise ValueError('invalid_audio_samples')
        if np.max(np.abs(samples)) > 1:
            # Lossy decoders can overshoot full scale; the prepared protocol is [-1,1].
            self.writer.reasons.add('audio_peak_clipped'); samples = np.clip(samples, -1, 1)
        start = round(seconds*RATE)
        if start < 0: samples, start = samples[-start:], 0
        if not len(samples): return True
        if self.start is None and start < self.last_end: raise ValueError('audio_timestamp_regression')
        if self.start is not None:
            expected = self.start+len(self.samples)
            delta=abs(start-expected)
            # Sample counters avoid tiny fragments from bounded decoder/resampler
            # PTS jitter. This never inserts/drops samples or accumulates unbounded
            # drift: every incoming PTS must remain within 20 ms of that counter.
            if delta <= max(tolerance_samples,round(.020*RATE)):
                if delta>tolerance_samples:self.writer.reasons.add('audio_timestamp_adjusted')
                start = expected
            elif start != expected:
                if start < self.last_end: raise ValueError('audio_timestamp_regression')
                self.writer.reasons.add('audio_timeline_gap')
                if not self.flush(): return False
        if self.start is None: self.start = start
        offset = 0
        while offset < len(samples):
            count = min(self.window-len(self.samples), len(samples)-offset)
            self.samples = np.concatenate((self.samples, samples[offset:offset+count]))
            offset += count
            if len(self.samples) == self.window:
                if not self.emit(): return False
                self.samples = self.samples[self.step:].copy(); self.start += self.step
        return True

def audio_track(container, stream, writer, budget, counter):
    import av
    sink, offset = AudioWindows(writer, budget), origin(container)
    resampler = av.AudioResampler(format='fltp', layout='mono', rate=RATE,
                                 options={'rematrix_maxval': '1'})
    last = -60
    tolerance = max(2, math.ceil(float(stream.time_base)*RATE)+2) if stream.time_base else 2
    if tolerance > RATE*.02: raise ValueError('audio_timestamp_precision')
    try:
        for frame in decoded(container, stream, counter):
            if (not 1 <= frame.sample_rate <= 192000 or not 1 <= len(frame.layout.channels) <= 8
                    or not 0 < frame.samples <= frame.sample_rate*5): raise ValueError('audio_frame_budget')
            t = timestamp(frame, offset)
            if t < last-2/RATE: raise ValueError('audio_timestamp_regression')
            last = t
            if t >= budget.seconds: raise DecodeLimit('media_duration_limit')
            for ready in resampler.resample(frame):
                start = timestamp(ready, offset)
                values = ready.to_ndarray().reshape(-1)
                allowed = max(0, math.floor((budget.seconds-start)*RATE+1e-6))
                if allowed < len(values):
                    if allowed and not sink.add(start, values[:allowed], tolerance_samples=tolerance): return
                    raise DecodeLimit('media_duration_limit')
                if not sink.add(start, values, tolerance_samples=tolerance): return
        for ready in resampler.resample(None):
            start = timestamp(ready, offset); values = ready.to_ndarray().reshape(-1)
            allowed = max(0, math.floor((budget.seconds-start)*RATE+1e-6))
            if allowed and not sink.add(start, values[:allowed], tolerance_samples=tolerance): return
            if allowed < len(values): raise DecodeLimit('media_duration_limit')
    except DecodeLimit as error: writer.reasons.add(str(error))
    finally: sink.flush()

class VideoWindows:
    def __init__(self, writer, budget):
        self.writer, self.budget, self.frames = writer, budget, []
        self.start, self.end, self.last_emitted = None, 0, -1
    def emit(self, end):
        if not self.frames or end <= self.last_emitted: return True
        if end > self.start+self.budget.video_window:
            end = self.start+self.budget.video_window
            self.writer.reasons.add('video_window_coverage_limit')
        # A virtual 100 Hz clock encodes sampled PTS with <=5ms rounding error.
        # It does not claim the original file has a constant frame rate.
        total = max(1, math.ceil((end-self.start)*100-1e-7))
        indices = [max(0, min(total-1, round((t-self.start)*100))) for t,_,_ in self.frames]
        if len(set(indices)) != len(indices): raise ValueError('video_sampling_timestamps')
        ok = self.writer.emit(dict(type='video', frames=[data for _,data,_ in self.frames],
                                   fps=100, indices=indices, totalFrames=total),
                              dict(kind='time', startSeconds=self.start, endSeconds=end),
                              'Sampled video frames; audio is indexed separately.', self.frames[0][2],
                              coverage=dict(sampleTimesSeconds=[t for t,_,_ in self.frames]))
        if ok: self.last_emitted = end
        return ok
    def add(self, t, data, thumbnail):
        if self.start is None: self.start = t
        while t >= self.start+self.budget.video_window-1e-7:
            if not self.emit(self.start+self.budget.video_window): return False
            self.start += self.budget.video_window-self.budget.video_overlap
            self.frames = [item for item in self.frames if item[0] >= self.start-1e-7]
            if not self.frames and t >= self.start+self.budget.video_window:
                self.start = t
        self.frames.append((t, data, thumbnail))
        return True

def video_track(container, stream, writer, budget, counter):
    from PIL import ImageOps
    sink, offset = VideoWindows(writer, budget), origin(container)
    last, next_sample, size = -1, 0, None
    writer.reasons.add('video_frames_sampled')
    try:
        for frame in decoded(container, stream, counter):
            if not 0 < frame.width*frame.height <= 16_000_000: raise ValueError('video_pixel_budget')
            t = timestamp(frame, offset)
            if t < 0: continue
            if t <= last: raise ValueError('video_timestamp_regression')
            if last >= 0 and t-last > budget.sample_interval*2: writer.reasons.add('video_timeline_gap')
            last = t
            if t >= budget.seconds: raise DecodeLimit('media_duration_limit')
            duration = float(frame.duration*frame.time_base) if frame.duration else 0
            if not 0 < duration <= 10:
                rate = stream.average_rate
                if rate is None or not 0 < float(rate) <= 240: raise ValueError('video_duration_missing')
                duration = 1/float(rate); writer.reasons.add('video_end_estimated')
            sink.end = min(budget.seconds, t+duration)
            if t+duration > budget.seconds+1e-7: writer.reasons.add('media_duration_limit')
            if t+1e-7 < next_sample: continue
            scale = min(1, math.sqrt(250000/(frame.width*frame.height)))
            image = frame.reformat(width=max(1,int(frame.width*scale)), height=max(1,int(frame.height*scale)), format='rgb24').to_image()
            rotation = frame.rotation
            if rotation: image = image.rotate(rotation, expand=True)
            if size is None: size = image.size
            elif image.size != size:
                image = ImageOps.pad(image, size); writer.reasons.add('video_dimensions_changed')
            data, thumbnail = image_bytes(image, pixels=250000)
            if not sink.add(t, data, thumbnail): return
            next_sample = t+budget.sample_interval
    except DecodeLimit as error: writer.reasons.add(str(error))
    finally: sink.emit(sink.end)

def media(source, writer, family, budget=None):
    if family not in ('audio', 'video'): raise ValueError('unsupported_media_family')
    budget = budget or MediaBudget()
    counter = Counter(budget)
    writer.coverage = dict(audioWindowSeconds=budget.audio_window, audioOverlapSeconds=budget.audio_overlap,
                           videoWindowSeconds=budget.video_window, videoOverlapSeconds=budget.video_overlap,
                           videoSampleIntervalSeconds=budget.sample_interval, maximumSeconds=budget.seconds,
                           audioSegments=0, videoSegments=0)
    with opened(source) as container:
        audio, video = list(container.streams.audio), list(container.streams.video)
        if family == 'audio' and not audio: raise ValueError('no_audio_track')
        if family == 'video' and not video: raise ValueError('no_video_track')
        writer.coverage.update(audioTracks=len(audio), videoTracks=len(video))
        if len(audio) > 1: writer.reasons.add('additional_audio_tracks_not_indexed')
        if family == 'video':
            if len(video) > 1: writer.reasons.add('additional_video_tracks_not_indexed')
            video_track(container, video[0], writer, budget, counter)
            writer.coverage['videoSegments'] = len(writer.files)
        else:
            audio_track(container, audio[0], writer, budget, counter)
            writer.coverage['audioSegments'] = len(writer.files)
    if family == 'video' and audio:
        if writer.full: writer.reasons.add('audio_track_not_indexed'); return
        # Reopen the same snapshot rather than buffering decoded audio for the entire file.
        before = len(writer.files)
        with opened(source) as container: audio_track(container, container.streams.audio[0], writer, budget, counter)
        writer.coverage['audioSegments'] = len(writer.files)-before
        if not writer.coverage['audioSegments']: writer.reasons.add('audio_track_not_indexed')
