"""Opt-in CPU FP32 memory profile on authored in-memory inputs; never a quality gate."""

import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import sys
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--after-load-gc', action='store_true')
    parser.add_argument('--serial-load', action='store_true')
    parser.add_argument('--lazy-commit', action='store_true')
    parser.add_argument('--native-threads', type=int, choices=range(1, 33), default=4,
                        help='Native pools before diagnostic imports (default: 4); model Torch threads stay at 4.')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    sys.path.insert(0, str(args.runtime.resolve()))
    for name in ('HF_HUB_OFFLINE', 'TRANSFORMERS_OFFLINE'):
        os.environ[name] = '1'
    os.environ['USE_TF'] = '0'
    if args.serial_load:
        os.environ['HF_DEACTIVATE_ASYNC_LOAD'] = '1'
    if args.lazy_commit:
        os.environ['MIMALLOC_EAGER_COMMIT'] = '0'
        os.environ['MIMALLOC_ARENA_EAGER_COMMIT'] = '0'
    if args.native_threads:
        for name in ('OMP_NUM_THREADS', 'MKL_NUM_THREADS', 'OPENBLAS_NUM_THREADS'):
            os.environ[name] = str(args.native_threads)
    import psutil
    process = psutil.Process()
    stage = 'python'
    samples = []
    stop = threading.Event()
    started = time.perf_counter()

    def memory():
        info = process.memory_info()
        return dict(seconds=time.perf_counter() - started, stage=stage,
                    privateBytes=info.private, peakCommitBytes=info.peak_pagefile,
                    workingSetBytes=info.rss)

    def sample():
        while not stop.wait(.05):
            samples.append(memory())

    sampler = threading.Thread(target=sample, daemon=True)
    sampler.start()
    events = []

    def mark(name):
        nonlocal stage
        stage = name
        row = memory()
        events.append(row)
        print(json.dumps(row), flush=True)

    def write(name, value):
        (output / name).write_text(json.dumps(value, indent=2, allow_nan=False), encoding='utf-8')

    write('host.json', dict(python=sys.version, executable=sys.executable,
                           physicalBytes=psutil.virtual_memory().total,
                           availableBytes=psutil.virtual_memory().available,
                           runtime=str(args.runtime.resolve()), model=str(args.model.resolve()),
                           scriptSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()))
    try:
        mark('import-torch')
        import torch
        mark('import-sentence-transformers')
        import sentence_transformers
        from threadpoolctl import threadpool_info
        pools = threadpool_info()
        mark('load-full-fp32-model')
        from semantic_search.embedding import FullEmbedder
        embedder = FullEmbedder(args.model, device='cpu', threads=4)
        weights = Counter()
        storages = {}
        for parameter in embedder.model.parameters():
            weights[str(parameter.dtype)] += parameter.numel() * parameter.element_size()
            storage = parameter.untyped_storage()
            storages[storage.data_ptr()] = storage.nbytes()
        mark('model-loaded')
        if args.after_load_gc:
            import gc
            collected = gc.collect()
            mark('after-load-gc')
        else:
            collected = None
        mappings = [dict(path=row.path, workingSetBytes=row.rss) for row in process.memory_maps()
                    if row.path.lower().endswith(('.safetensors', '.bin'))]
        import numpy as np
        from PIL import Image
        tone = (np.sin(np.arange(48_000, dtype=np.float32) * (2 * np.pi * 440 / 16_000)) * .1).astype(np.float32)
        frames = np.zeros((3, 64, 64, 3), dtype=np.uint8)
        frames[:, :, :, 2] = 255
        trials = [('query', 'authored memory calibration', True),
                  ('text', 'Authored calibration document about a blue square.', False),
                  ('image', {'image': Image.new('RGB', (64, 64), 'blue')}, False),
                  ('audio', {'audio': tone}, False), ('video', {'video': frames}, False),
                  ('joint-av', {'video': frames, 'audio': tone}, False)]
        vectors = []
        for name, content, query in trials:
            mark('embed-' + name)
            vector, tokens = embedder.embed(content, query=query)
            assert vector.shape == (768,) and np.isfinite(vector).all()
            assert abs(float(np.linalg.norm(vector)) - 1) < .001
            vectors.append(dict(input=name, tokens=tokens, vectorSha256=hashlib.sha256(vector.tobytes()).hexdigest()))
            mark('after-' + name)
        mark('idle')
        time.sleep(2)
        write('report.json', dict(schemaVersion=1, passed=True, device='cpu', dtype=embedder.dtype,
                                 threads=4, parameterBytesByDtype=weights, modelRevision=embedder.manifest['revision'],
                                 afterLoadGc=args.after_load_gc, collectedObjects=collected, checkpointMappings=mappings,
                                 serialLoad=args.serial_load, uniqueParameterStorageBytes=sum(storages.values()),
                                 lazyCommit=args.lazy_commit,
                                 nativeThreads=args.native_threads, nativePools=pools,
                                 events=events, vectors=vectors, torch=torch.__version__,
                                 sentenceTransformers=sentence_transformers.__version__,
                                 scope='Single Python process, CPU FP32, authored in-memory inputs; no parser/index, quality or physical-minimum approval',
                                 releaseReady=False))
    except BaseException as error:
        write('failure.json', dict(type=type(error).__name__, message=str(error), events=events))
        raise
    finally:
        stop.set()
        sampler.join(2)
        write('samples.json', samples)


if __name__ == '__main__':
    main()
