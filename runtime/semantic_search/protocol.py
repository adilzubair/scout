"""Versioned, bounded prepared-media inputs. No filesystem or URL inputs."""

import base64
import binascii
import io
import math

MAX_BODY = 32 * 1024**2


def decode_bytes(value, limit):
    if not isinstance(value, str) or len(value) > ((limit+2)//3)*4:
        raise ValueError('Invalid encoded media length')
    try:
        data = base64.b64decode(value, validate=True)
    except (ValueError, binascii.Error) as error:
        raise ValueError('Invalid encoded media') from error
    if not data or len(data) > limit:
        raise ValueError('Invalid decoded media length')
    return data


def image(value):
    from PIL import Image
    with Image.open(io.BytesIO(decode_bytes(value, 8*1024**2))) as source:
        if source.format not in ('PNG', 'JPEG', 'WEBP', 'BMP', 'TIFF') or source.width*source.height > 4_000_000:
            raise ValueError('Unsupported or oversized image')
        if getattr(source, 'n_frames', 1) != 1:
            raise ValueError('Segment multi-frame images before embedding')
        return source.convert('RGB')


def audio(value):
    import numpy as np
    if not isinstance(value,dict) or set(value)!={'data','sampleRate'} or type(value['sampleRate']) is not int or value['sampleRate']!=16000:
        raise ValueError('Audio must be prepared mono 16 kHz float32')
    data=decode_bytes(value['data'],60*16000*4)
    if len(data)%4:raise ValueError('Invalid audio sample alignment')
    samples=np.frombuffer(data,dtype='<f4').copy()
    if not np.isfinite(samples).all() or np.max(np.abs(samples))>1:raise ValueError('Invalid audio samples')
    return samples


def decode_request(body):
    import numpy as np
    if not isinstance(body, dict) or type(body.get('schemaVersion')) is not int or body.get('schemaVersion') != 1:
        raise ValueError('Unsupported embedding protocol')
    mode = body.get('mode')
    if mode == 'query':
        if set(body) != {'schemaVersion', 'mode', 'text'}:
            raise ValueError('Invalid query fields')
        text = body['text']
        if not isinstance(text, str) or not text.strip() or len(text) > 32000:
            raise ValueError('Invalid query text')
        return text, True, None
    if mode != 'document' or set(body) != {'schemaVersion', 'mode', 'content'}:
        raise ValueError('Invalid document fields')
    content = body['content']
    if not isinstance(content, dict):
        raise ValueError('Invalid content')
    kind = content.get('type')
    if kind == 'text' and set(content) == {'type', 'text'}:
        text = content['text']
        if not isinstance(text, str) or not text.strip() or len(text) > 200000:
            raise ValueError('Invalid document text')
        return text, False, None
    if kind == 'image' and set(content) == {'type', 'data'}:
        return {'image': image(content['data'])}, False, None
    if kind == 'audio' and set(content) == {'type', 'data', 'sampleRate'}:
        return {'audio':audio({key:content[key] for key in ('data','sampleRate')})},False,None
    if kind == 'video' and set(content) in ({'type','frames','fps','indices','totalFrames'},{'type','frames','fps','indices','totalFrames','audio'}):
        frames, indices, fps, total = (content[key] for key in ('frames', 'indices', 'fps', 'totalFrames'))
        if (not isinstance(frames, list) or not 1 <= len(frames) <= 16 or not isinstance(indices, list)
                or len(indices) != len(frames) or type(total) is not int or not 1 <= total <= 1_000_000
                or type(fps) not in (int, float) or not math.isfinite(fps) or not 0 < fps <= 240 or total/fps > 60
                or any(type(i) is not int or i < 0 or i >= total for i in indices)
                or indices != sorted(set(indices))):
            raise ValueError('Invalid prepared video metadata')
        decoded = []
        pixels = 0
        for value in frames:
            frame = np.asarray(image(value))
            pixels += frame.shape[0]*frame.shape[1]
            if pixels > 16_000_000 or (decoded and frame.shape != decoded[0].shape):
                raise ValueError('Video frame budget or shape mismatch')
            decoded.append(frame)
        metadata = dict(total_num_frames=total, fps=float(fps), duration=total/fps,
                        frames_indices=indices, video_backend='scout-prepared')
        result={'video':np.stack(decoded)}
        if 'audio' in content:
            result['audio']=audio(content['audio'])
            if abs(len(result['audio'])/16000-total/fps)>.0101:raise ValueError('Joint audiovisual duration mismatch')
        return result,False,{'video':{'do_sample_frames':False,'video_metadata':metadata}}
    raise ValueError('Unsupported prepared content')
