"""Full-model smoke for an authored sandbox output pack; no retrieval-quality claim."""
import argparse
import json
from pathlib import Path
import socket
import time

import numpy as np
from semantic_search.embedding import FullEmbedder
from semantic_search.manifest import sha256
from semantic_search.protocol import decode_request

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model',type=Path,required=True)
    parser.add_argument('--root',type=Path,required=True)
    parser.add_argument('--device',choices=['cpu','cuda'],default='cpu')
    options = parser.parse_args()
    def blocked(*args,**kwargs): raise AssertionError('Outgoing network forbidden')
    socket.socket.connect = blocked; socket.socket.connect_ex = blocked
    model = FullEmbedder(options.model,device=options.device)
    counts = {}; times = []; packs = list(options.root.rglob('ready.json'))
    assert packs, 'No prepared outputs found'
    for path in packs:
        ready = json.loads(path.read_text())
        assert ready['schemaVersion'] == 1 and 0 < len(ready['segments']) <=256
        for receipt in ready['segments']:
            name = receipt['name']
            assert Path(name).name == name and name.startswith('segment-') and name.endswith('.json')
            segment = path.parent/name
            assert segment.stat().st_size == receipt['bytes'] and sha256(segment) == receipt['sha256']
            data = json.loads(segment.read_text(encoding='utf-8'))
            content, query, processing = decode_request(dict(schemaVersion=1,mode='document',content=data['content']))
            started = time.monotonic(); vector,tokens = model.embed(content,query=query,processing_kwargs=processing)
            elapsed = time.monotonic()-started; times.append(elapsed)
            assert vector.shape == (768,) and np.isfinite(vector).all() and abs(np.linalg.norm(vector)-1)<1e-5
            assert 0 < tokens <=8192
            counts[data['modality']] = counts.get(data['modality'],0)+1
            print(f'PASS {path.parent.parent.name} {name} {data["modality"]} {tokens} tokens {elapsed:.3f}s',flush=True)
    print(json.dumps(dict(device=options.device,packs=len(packs),segments=sum(counts.values()),modalities=counts,
                          inferenceSeconds=sum(times),maximumSegmentSeconds=max(times))))

if __name__ == '__main__': main()
