"""Opt-in CPU FP32 memory profile on authored in-memory inputs; never a quality gate."""

import argparse
import builtins
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
    parser.add_argument('--native-threads', type=int, choices=range(1, 33),
                        help='Override native pools at the first Torch import; default follows the production constructor.')
    parser.add_argument('--maximum-windows', action='store_true',
                        help='Use production 2048-token text, 1 MP image, 12 s audio and eight-frame AV window sizes.')
    parser.add_argument('--blas-threads', type=int, choices=range(1, 33))
    parser.add_argument('--immediate-purge', action='store_true')
    parser.add_argument('--import-timings', action='store_true', help='Aggregate nested constructor imports; adds diagnostic overhead.')
    parser.add_argument('--platform-trace', action='store_true', help='Time Windows platform WMI calls without altering their results.')
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
    if args.blas_threads:
        os.environ['OPENBLAS_NUM_THREADS'] = str(args.blas_threads)
    if args.immediate_purge:
        os.environ['MIMALLOC_PURGE_DELAY'] = '0'
        os.environ['MIMALLOC_PURGE_DECOMMITS'] = '1'
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
    import_timings = {}
    import_gate = threading.Lock()
    import_local = threading.local()
    dropped_import_names = 0
    platform_calls = []
    original_wmi = None
    platform_module = None
    original_source_to_code = None
    source_loader = None
    compilation = dict(files=0, sourceBytes=0, seconds=0.0)

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
        from semantic_search.embedding import FullEmbedder
        if args.import_timings:
            from importlib.machinery import SourceFileLoader as source_loader
            original_source_to_code = source_loader.source_to_code
            def timed_compile(*values, **options):
                began = time.perf_counter()
                try:
                    return original_source_to_code(*values, **options)
                finally:
                    elapsed = time.perf_counter()-began
                    with import_gate:
                        compilation['files'] += 1
                        compilation['sourceBytes'] += len(values[1]) if len(values)>1 else 0
                        compilation['seconds'] += elapsed
            source_loader.source_to_code = timed_compile
        if args.platform_trace:
            import platform as platform_module
            original_wmi = getattr(platform_module, '_wmi_query', None)
            if original_wmi is not None:
                def timed_wmi(*values, **options):
                    began = time.perf_counter()
                    try:
                        return original_wmi(*values, **options)
                    finally:
                        if len(platform_calls) < 256:
                            platform_calls.append(dict(table=str(values[0])[:32] if values else '',
                                keys=[str(value)[:64] for value in values[1:9]],
                                stage=stage,startSeconds=began-started,seconds=time.perf_counter()-began))
                platform_module._wmi_query = timed_wmi
        original_import = builtins.__import__
        observed = set()
        def timed_import(name, *values, **options):
            nonlocal dropped_import_names
            first = name in ('torch', 'sentence_transformers') and name not in observed
            if first:
                observed.add(name)
                # The production constructor sets its own limits before this
                # boundary. Apply an explicitly requested diagnostic override
                # here so the experiment measures the requested native pools.
                if name == 'torch':
                    if args.native_threads:
                        for key in ('OMP_NUM_THREADS', 'MKL_NUM_THREADS', 'OPENBLAS_NUM_THREADS'):
                            os.environ[key] = str(args.native_threads)
                    if args.blas_threads:
                        os.environ['OPENBLAS_NUM_THREADS'] = str(args.blas_threads)
                mark('import-' + name)
            if args.import_timings:
                stack = getattr(import_local, 'stack', None)
                if stack is None:
                    stack = import_local.stack = []
                frame = [time.perf_counter(), 0.0]
                stack.append(frame)
                try:
                    result = original_import(name, *values, **options)
                finally:
                    elapsed = time.perf_counter()-frame[0]
                    stack.pop()
                    if stack:
                        stack[-1][1] += elapsed
                    context = values[0] if values else options.get('globals')
                    level = values[3] if len(values)>3 else options.get('level', 0)
                    key = str(name)
                    if level and isinstance(context, dict):
                        package = context.get('__package__') or context.get('__name__', '').rpartition('.')[0]
                        prefix = package.rsplit('.', level-1)[0] if level>1 else package
                        key = prefix + ('.'+key if key else '')
                    key = (key or '<empty-import-name>')[:200]
                    with import_gate:
                        if key not in import_timings and len(import_timings) >= 4096:
                            dropped_import_names += 1
                        else:
                            row = import_timings.setdefault(key, dict(calls=0, inclusiveSeconds=0.0, selfSeconds=0.0))
                            row['calls'] += 1
                            row['inclusiveSeconds'] += elapsed
                            row['selfSeconds'] += max(0.0, elapsed-frame[1])
            else:
                result = original_import(name, *values, **options)
            if first:
                mark('after-import-' + name)
            return result
        mark('verify-and-load-full-fp32-model')
        builtins.__import__ = timed_import
        try:
            embedder = FullEmbedder(args.model, device='cpu', threads=4)
        finally:
            builtins.__import__ = original_import
            if original_source_to_code is not None:
                source_loader.source_to_code = original_source_to_code
        import torch
        import sentence_transformers
        from threadpoolctl import threadpool_info
        pools = threadpool_info()
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
        tone_samples = 192_000 if args.maximum_windows else 48_000
        frame_count = 8 if args.maximum_windows else 3
        frame_side = 500 if args.maximum_windows else 64
        tone = (np.sin(np.arange(tone_samples, dtype=np.float32) * (2 * np.pi * 440 / 16_000)) * .1).astype(np.float32)
        frames = np.zeros((frame_count, frame_side, frame_side, 3), dtype=np.uint8)
        frames[:, :, :, 2] = 255
        text = 'Authored calibration document about a blue square.'
        if args.maximum_windows:
            text = ' x' * 2048
            tokenizer = embedder.model[0].tokenizer
            while len(tokenizer.encode(text)) > 2048:
                text = text[:-2]
            if len(tokenizer.encode(text)) != 2048:
                raise ValueError('Profile text does not match the production reader token budget.')
        video_processing = {'video': {'do_sample_frames': False, 'video_metadata': dict(
            total_num_frames=frame_count, fps=1, duration=frame_count, frames_indices=list(range(frame_count)), video_backend='scout-profile')}}
        trials = [('query', 'authored memory calibration', True),
                  ('text', text, False),
                  ('image', {'image': Image.new('RGB', (1000, 1000) if args.maximum_windows else (64, 64), 'blue')}, False),
                  ('audio', {'audio': tone}, False), ('video', {'video': frames}, False),
                  ('joint-av', {'video': frames, 'audio': tone[:frame_count * 16_000]}, False)]
        vectors = []
        for name, content, query in trials:
            mark('embed-' + name)
            vector, tokens = embedder.embed(content, query=query,
                processing_kwargs=video_processing if isinstance(content, dict) and 'video' in content else None)
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
                                 maximumProductionWindows=args.maximum_windows,
                                 blasThreads=args.blas_threads, immediatePurge=args.immediate_purge,
                                 actualAllocatorEnvironment={key:os.environ.get(key) for key in
                                     ('MIMALLOC_PURGE_DELAY','MIMALLOC_PURGE_DECOMMITS')},
                                 integritySeconds=embedder.integrity_seconds, modelLoadSeconds=embedder.load_seconds,
                                 importOrder='Observed from production FullEmbedder constructor; imports are not preloaded',
                                 nestedImportTimings=import_timings, droppedImportNames=dropped_import_names,
                                 platformTraceRequested=args.platform_trace, platformWmiCalls=platform_calls,
                                 platformWmiTraceSupported=original_wmi is not None,
                                 nestedImportTracing=args.import_timings,
                                 sourceCompilation=compilation,
                                 events=events, vectors=vectors, torch=torch.__version__,
                                 sentenceTransformers=sentence_transformers.__version__,
                                 scope='Single Python process, CPU FP32, authored in-memory inputs; no parser/index, quality or physical-minimum approval',
                                 releaseReady=False))
    except BaseException as error:
        write('failure.json', dict(type=type(error).__name__, message=str(error), events=events))
        raise
    finally:
        if original_source_to_code is not None:
            source_loader.source_to_code = original_source_to_code
        if original_wmi is not None:
            platform_module._wmi_query = original_wmi
        stop.set()
        sampler.join(2)
        write('samples.json', samples)


if __name__ == '__main__':
    main()
