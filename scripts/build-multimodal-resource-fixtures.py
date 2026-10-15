"""Extend the authored hardware corpus with full production-window stress inputs."""

import argparse
from fractions import Fraction
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import wave

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'runtime'))
from semantic_search.manifest import load_manifest
from semantic_search.paths import local_file


def digest(path):
    with path.open('rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--tokenizer', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    for name in ('OMP_NUM_THREADS', 'MKL_NUM_THREADS', 'OPENBLAS_NUM_THREADS'):
        os.environ[name] = '1'
    import av
    import numpy as np
    from PIL import Image
    from tokenizers import Tokenizer

    baseline = args.baseline.resolve(strict=True)
    inventory = json.loads((baseline.parent / 'inventory.json').read_text(encoding='utf-8'))
    names = ['notes.docx', 'notes.odt', 'growth.pptx', 'growth.odp', 'sales.xlsx', 'sales.ods',
             'tone.wav', 'tone.mp3', 'tone.m4a', 'tone.flac', 'scene.mp4', 'scene.mov', 'scene.mkv',
             'scene.webm', 'invoice.pdf', 'source.py', 'picture.png']
    files = {}
    for name in names:
        path = local_file(baseline / name, baseline)
        if digest(path) != inventory['fixtures/' + name]:
            raise ValueError('Baseline fixture checksum mismatch: ' + name)
        files[name] = path
    tokenizer = args.tokenizer.resolve(strict=True)
    spec = load_manifest()['files']['tokenizer.json']
    if tokenizer.stat().st_size != spec['bytes'] or digest(tokenizer) != spec['sha256']:
        raise ValueError('Use the pinned tokenizer for the production text budget.')
    encode = Tokenizer.from_file(str(tokenizer)).encode
    body = ' x' * 2048
    while len(encode(body).ids) > 2048:
        body = body[:-2]
    if len(encode(body).ids) != 2048:
        raise ValueError('Could not create an exact production text-window fixture.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    for name, source in files.items():
        shutil.copyfile(source, output / name)
    (output / 'window-text.txt').write_text(body, encoding='utf-8')
    Image.new('RGB', (1000, 1000), 'blue').save(output / 'window-image.png')
    samples = (np.sin(np.arange(192_000, dtype=np.float32) * (2 * np.pi * 440 / 16_000)) * 3000).astype('<i2')
    with wave.open(str(output / 'window-audio.wav'), 'wb') as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(16_000)
        audio.writeframes(samples.tobytes())
    frames = np.zeros((500, 500, 3), dtype=np.uint8)
    frames[:, :, 2] = 255
    with av.open(str(output / 'window-video.mkv'), 'w') as container:
        video = container.add_stream('ffv1', rate=1)
        video.width, video.height, video.pix_fmt = 500, 500, 'yuv420p'
        audio = container.add_stream('pcm_s16le', rate=16_000)
        audio.layout = 'mono'
        for index in range(8):
            frame = av.VideoFrame.from_ndarray(frames, format='rgb24')
            frame.pts, frame.time_base = index, Fraction(1, 1)
            for packet in video.encode(frame):
                container.mux(packet)
            sound = av.AudioFrame.from_ndarray(samples[index*16_000:(index+1)*16_000][None, :], format='s16', layout='mono')
            sound.sample_rate, sound.pts, sound.time_base = 16_000, index*16_000, Fraction(1, 16_000)
            for packet in audio.encode(sound):
                container.mux(packet)
        for stream in (video, audio):
            for packet in stream.encode(None):
                container.mux(packet)
    # Verify the encoded stress media, not just the requested encoder settings.
    with av.open(str(output / 'window-video.mkv')) as container:
        decoded = list(container.decode(video=0))
        if len(decoded) != 8 or any(frame.width*frame.height != 250_000 for frame in decoded):
            raise ValueError('Video stress fixture did not preserve eight bounded frames.')
    with av.open(str(output / 'window-video.mkv')) as container:
        decoded_audio = list(container.decode(audio=0))
        if sum(frame.samples for frame in decoded_audio) != 128_000:
            raise ValueError('Joint fixture did not preserve exactly eight seconds of PCM.')
    manifest = dict(schemaVersion=1, purpose='Authored engineering resource fixtures, never retrieval-quality labels',
                    sourceInventorySha256=digest(baseline.parent / 'inventory.json'), textReaderTokens=2048,
                    imagePixels=1_000_000, audioSamples=192_000, sampleRate=16_000,
                    videoFrames=8, videoFramePixels=250_000, jointAudioSamples=128_000,
                    avVersion=av.__version__, scriptSha256=digest(Path(__file__)),
                    files={path.name: dict(bytes=path.stat().st_size, sha256=digest(path)) for path in sorted(output.iterdir())})
    # Keep metadata outside the selected content folder: the actual reader sees only authored inputs.
    output.with_name(output.name + '.manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print('Verified 21 authored all-family fixtures with exact full production windows: ' + str(output), flush=True)


if __name__ == '__main__':
    main()
