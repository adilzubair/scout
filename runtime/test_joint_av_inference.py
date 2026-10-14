"""Offline joint audiovisual compatibility, not a retrieval-quality holdout."""
import argparse
import hashlib
import json
from pathlib import Path
import socket
import time
from unittest.mock import patch

import numpy as np
from semantic_search.embedding import FullEmbedder
from semantic_search.manifest import sha256


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model',type=Path,required=True)
    parser.add_argument('--device',choices=['cpu','cuda'],default='cpu')
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    if args.output.exists():raise FileExistsError('Refusing to replace compatibility evidence')
    frames=np.zeros((2,128,128,3),dtype=np.uint8)
    frames[:,:,:,2]=180;frames[0,15:70,10:55]=[240,210,20];frames[1,35:90,65:110]=[240,210,20]
    changed=frames.copy();changed[:,:,:,0]=210;changed[:,:,:,2]=20
    t=np.arange(32000,dtype=np.float32)/16000
    sound=(.2*np.sin(2*np.pi*220*t)).astype(np.float32)
    changed_sound=(.2*np.sin(2*np.pi*880*t)).astype(np.float32)
    processing={'video':{'do_sample_frames':False,'video_metadata':dict(total_num_frames=2,fps=1.,duration=2.,frames_indices=[0,1],video_backend='scout-prepared')}}
    with patch.object(socket.socket,'connect',side_effect=AssertionError('Unexpected network access')):
        model=FullEmbedder(args.model,args.device,dimensions=768)
        backbone=model.model[0].model;calls={'audio':0,'vision':0};features=[]
        def hook(kind):
            def called(*_):calls[kind]+=1
            return called
        handles=[backbone.audio_tower.register_forward_pre_hook(hook('audio')),backbone.vision_tower.register_forward_pre_hook(hook('vision'))]
        real_preprocess=model.model.preprocess
        def preprocess(*values,**kwargs):
            result=real_preprocess(*values,**kwargs)
            features.append({key:list(value.shape) for key,value in result.items() if hasattr(value,'shape')})
            return result
        rows=[];vectors={}
        try:
            with patch.object(model.model,'preprocess',side_effect=preprocess):
                for name,content in [('joint',{'video':frames,'audio':sound}),
                    ('changed-audio',{'video':frames,'audio':changed_sound}),
                    ('changed-video',{'video':changed,'audio':sound}),
                    ('video-only',{'video':frames}),('audio-only',{'audio':sound})]:
                    prior=dict(calls);start=time.perf_counter()
                    vector,tokens=model.embed(content,processing_kwargs=processing if 'video' in content else None)
                    used={key:calls[key]-prior[key] for key in calls}
                    assert all(used[key]>0 for key in (('audio','vision') if len(content)==2 else ('vision',) if 'video' in content else ('audio',))),used
                    assert vector.shape==(768,) and np.isfinite(vector).all() and abs(np.linalg.norm(vector)-1)<1e-5
                    assert 0<tokens<=8192
                    vectors[name]=vector
                    rows.append(dict(name=name,tokens=tokens,seconds=time.perf_counter()-start,encoderCalls=used,featureShapes=features[-1],vectorSha256=hashlib.sha256(vector.astype('<f4').tobytes()).hexdigest()))
                    print(f'PASS {name}: {tokens} tokens, encoders {used}',flush=True)
            differences={name:float(np.linalg.norm(vector-vectors['joint'])) for name,vector in vectors.items() if name!='joint'}
            assert all(value>1e-4 for value in differences.values()),differences
            with patch.object(model.model,'forward',side_effect=AssertionError('Oversized combined input reached inference')):
                try:model.embed({'text':' token'*9000,'video':frames,'audio':sound},processing_kwargs=processing)
                except ValueError as error:assert 'token budget' in str(error)
                else:raise AssertionError('Oversized combined input was truncated')
            print('PASS both changed channels affect the joint vector; shared budget rejects oversized input before inference',flush=True)
        finally:
            for handle in handles:handle.remove()
    result=dict(schemaVersion=1,purpose='authored compatibility only; not retrieval-quality evidence',device=args.device,
        modelRevision=model.manifest['revision'],modelManifestSha256=sha256(Path(__file__).parent/'embeddinggemma2_model.json'),
        testSha256=sha256(__file__),rows=rows,jointVectorL2Differences=differences,oversizedCombinedRejected=True)
    args.output.parent.mkdir(parents=True,exist_ok=True)
    with args.output.open('x',encoding='utf-8') as stream:json.dump(result,stream,indent=2)

if __name__=='__main__':main()
