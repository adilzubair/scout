"""Sandboxed short playback clips for codecs unavailable in Windows MediaElement."""
import argparse
import json
import math
from fractions import Fraction
from pathlib import Path
import sys

if __package__ in (None, ''): sys.path.insert(0,str(Path(__file__).absolute().parents[1]))
from semantic_search.paths import local_file
from semantic_search.manifest import sha256
from semantic_search.media_readers import opened, origin, timestamp, Counter, MediaBudget, decoded

def prepare(request_path):
    import av
    import numpy as np
    root=Path(request_path).absolute().parent
    request_path=local_file(request_path,root)
    if request_path.stat().st_size>16384: raise ValueError('request_budget')
    data=json.loads(request_path.read_text(encoding='utf-8-sig'))
    if (set(data)!={'schemaVersion','source','sourceSha256','family','startSeconds','endSeconds'}
            or type(data['schemaVersion']) is not int or data['schemaVersion']!=1): raise ValueError('invalid_playback_request')
    name=data['source']
    if not isinstance(name,str) or Path(name).name!=name or not name.startswith('input.') or any(c in name for c in ':\\/'): raise ValueError('invalid_source_name')
    source=local_file(root/name,root)
    start,end=data['startSeconds'],data['endSeconds']
    if (type(start) not in (int,float) or type(end) not in (int,float) or not math.isfinite(start+end)
            or not 0<=start<end<=620 or end-start>20 or data['family'] not in ('audio','video')): raise ValueError('playback_window_budget')
    if sha256(source)!=data['sourceSha256']: raise ValueError('snapshot_hash_mismatch')
    counter=Counter(MediaBudget(wall_seconds=60))
    video=data['family']=='video';name='clip.mp4' if video else 'clip.wav';output=root/name
    first,last=None,start
    with opened(source) as container:
        has_audio=bool(container.streams.audio)
        if video and not container.streams.video or not video and not has_audio: raise ValueError('playback_track_missing')
    def seek(container):
        offset=origin(container)
        if start:
            try: container.seek(max(0,round((start+offset)*av.time_base)),backward=True,any_frame=False)
            except av.FFmpegError: pass
        return offset
    with av.open(str(output),'w',format='mp4' if video else 'wav') as target:
        v=target.add_stream('libx264',rate=15) if video else None
        a=target.add_stream('aac' if video else 'pcm_s16le',rate=16000) if has_audio else None
        if a: a.layout='mono'
        if v:
            v.options={'preset':'ultrafast','tune':'zerolatency','crf':'24'}
            v.pix_fmt='yuv420p';v.codec_context.thread_count=1
            with opened(source) as container:
                offset=seek(container);stream=container.streams.video[0];previous=-1
                for frame in decoded(container,stream,counter):
                    t=timestamp(frame,offset)
                    if t<start: continue
                    if t>=end: break
                    if not 0<frame.width*frame.height<=16_000_000: raise ValueError('video_pixel_budget')
                    tick=round((t-start)*15)
                    if tick<=previous: continue
                    previous=tick
                    scale=min(1,640/frame.width,360/frame.height)
                    width=max(2,int(frame.width*scale)//2*2);height=max(2,int(frame.height*scale)//2*2)
                    image=frame.reformat(width=width,height=height,format='rgb24').to_image()
                    if frame.rotation: image=image.rotate(frame.rotation,expand=True)
                    if first is None: first=t;v.width=image.width//2*2;v.height=image.height//2*2
                    if image.size!=(v.width,v.height): image=image.resize((v.width,v.height))
                    prepared=av.VideoFrame.from_ndarray(np.asarray(image),format='rgb24');prepared.pts=tick;prepared.time_base=Fraction(1,15)
                    for packet in v.encode(prepared): target.mux(packet)
                    last=max(last,t+1/15)
                for packet in v.encode(None): target.mux(packet)
        if a:
            with opened(source) as container:
                offset=seek(container);resampler=av.AudioResampler(format='fltp',layout='mono',rate=16000,options={'rematrix_maxval':'1'})
                previous=-1
                def write(frame):
                    nonlocal first,last,previous
                    t=timestamp(frame,offset);values=frame.to_ndarray().reshape(-1)
                    lo=max(0,math.ceil((start-t)*16000-1e-6));hi=min(len(values),math.floor((end-t)*16000+1e-6))
                    if hi<=lo:return
                    values=np.clip(values[lo:hi],-1,1)
                    if not np.isfinite(values).all():raise ValueError('invalid_audio_samples')
                    t+=lo/16000;tick=round((t-start)*16000)
                    # Millisecond container timestamps can round adjacent frames backwards.
                    if previous>=0 and abs(tick-previous)<=32:tick=previous
                    if tick<previous:raise ValueError('audio_timestamp_regression')
                    previous=tick+len(values)
                    prepared=av.AudioFrame.from_ndarray(values.reshape(1,-1).astype(np.float32),format='fltp',layout='mono')
                    prepared.sample_rate=16000;prepared.pts=tick;prepared.time_base=Fraction(1,16000)
                    for packet in a.encode(prepared):target.mux(packet)
                    first=t if first is None else min(first,t);last=max(last,t+len(values)/16000)
                for frame in decoded(container,container.streams.audio[0],counter):
                    if not 1<=frame.sample_rate<=192000 or not 1<=len(frame.layout.channels)<=8 or not 0<frame.samples<=frame.sample_rate*5:raise ValueError('audio_frame_budget')
                    t=timestamp(frame,offset)
                    if t>=end:break
                    for ready in resampler.resample(frame):write(ready)
                for ready in resampler.resample(None):write(ready)
                for packet in a.encode(None):target.mux(packet)
    if first is None or output.stat().st_size>32*1024**2:raise ValueError('playback_output_budget')
    if sha256(source)!=data['sourceSha256']:raise ValueError('snapshot_changed')
    with opened(output) as container: actual_start=start+origin(container)
    result=dict(schemaVersion=1,name=name,bytes=output.stat().st_size,sha256=sha256(output),sourceSha256=data['sourceSha256'],startSeconds=actual_start,endSeconds=min(end,last))
    (root/'playback.json').write_text(json.dumps(result,allow_nan=False),encoding='utf-8')
    return result

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--request',type=Path,required=True);args=parser.parse_args()
    try:
        from semantic_search.extract import require_sandbox
        require_sandbox();prepare(args.request)
    except Exception:
        (args.request.absolute().parent/'playback-failed.json').write_text('{"schemaVersion":1,"reason":"playback_failed"}',encoding='utf-8')
        raise SystemExit(2)
