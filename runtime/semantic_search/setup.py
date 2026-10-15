"""Transactional, retryable add-on generations; existing installations stay usable."""

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from semantic_search.manifest import MANIFEST, load_manifest, sha256, verify_model

ROOT = Path(__file__).resolve().parents[2]


def atomic_json(path, value):
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, indent=2)+'\n', encoding='utf-8')
    temporary.replace(path)


def generation_id(device):
    import hashlib
    identity = 'schema2|' + '|'.join(sha256(path) for path in [MANIFEST,
        ROOT / 'runtime/multimodal_setup.json', ROOT / f'runtime/semantic_search/requirements.{device}.lock.txt'])
    return device + '-' + hashlib.sha256(identity.encode()).hexdigest()[:24]


def run(command, env):
    subprocess.run([str(value) for value in command], env=env, check=True,
                   stdin=subprocess.DEVNULL,stdout=sys.stdout,stderr=sys.stderr,
                   creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))


def python_command(python,*arguments):
    return [python,'-I','-B','-X','utf8',*arguments]


def copy_model(source, destination):
    manifest = load_manifest()
    for name in manifest['files']:unlinked(Path(source)/name)
    verify_model(source,manifest)
    destination.mkdir(parents=True, exist_ok=True)
    for name,spec in manifest['files'].items():
        target = destination / name
        unlinked(target)
        target.parent.mkdir(parents=True, exist_ok=True)
        # Setup's exclusive lock owns this exact temporary name. A killed copy
        # leaves at most one partial per artifact; retry unlinks it before writing.
        temporary=target.with_name(target.name+'.scout-copy.partial')
        unlinked(temporary);temporary.unlink(missing_ok=True)
        if target.is_file() and target.stat().st_size==spec['bytes'] and sha256(target)==spec['sha256']:continue
        try:
            with (Path(source)/name).open('rb') as reader,temporary.open('xb') as writer:
                shutil.copyfileobj(reader,writer,1024*1024);writer.flush();os.fsync(writer.fileno())
            if temporary.stat().st_size!=spec['bytes'] or sha256(temporary)!=spec['sha256']:raise ValueError('copied_model_checksum_mismatch')
            temporary.replace(target)
        finally:temporary.unlink(missing_ok=True)
    verify_model(destination,manifest)


def unlinked(path):
    for entry in (path,*path.parents):
        if entry.is_symlink() or entry.is_junction():raise ValueError('linked_model_reuse_path')


def previous_model(base,generation):
    """Only reuse the selected prior generation, after full pinned verification."""
    try:
        marker=base/'installed.json';unlinked(marker)
        with marker.open('rb') as stream:raw=stream.read(16385)
        if len(raw)>16384:return None
        name=json.loads(raw)['generation']
        if not isinstance(name,str) or not re.fullmatch(r'(cpu|cuda)-[0-9a-f]{24}',name) or name==generation.name:return None
        source=base/'generations'/name/'model'
        for entry in load_manifest()['files']:unlinked(source/entry)
        verify_model(source)
        return source
    except (OSError,ValueError,KeyError,TypeError):return None


def verify_venv_home(python, interpreter):
    """Require the same immutable patch path used by the standard venv launcher."""
    cfg=python.parent.parent/'pyvenv.cfg'
    for path in (cfg,*cfg.parents):
        if path.is_symlink() or path.is_junction():raise ValueError('linked_interpreter_configuration')
    with cfg.open('rb') as stream:raw=stream.read(16385)
    if len(raw)>16384:raise ValueError('interpreter_configuration_budget')
    lines=raw.decode('utf-8').splitlines()
    homes=[line[7:] for line in lines if line.startswith('home = ')]
    expected=interpreter.resolve(strict=True).parent
    if len(homes)!=1 or not Path(homes[0]).is_absolute() or Path(homes[0])!=expected:
        raise ValueError('unexpected_interpreter_home')
    for path in (expected,*expected.parents):
        if path.is_symlink() or path.is_junction():raise ValueError('linked_interpreter_home')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--uv', type=Path, required=True)
    parser.add_argument('--device', choices=['cpu', 'cuda'], default='cpu')
    parser.add_argument('--existing-model', type=Path, help='Import a verified local development model')
    parser.add_argument('--offline', action='store_true', help='Require cached dependencies and explicit local artifacts')
    parser.add_argument('--cache-dir', type=Path, help='Explicit development dependency cache; removal does not own it')
    args = parser.parse_args()
    base = args.root
    base.mkdir(parents=True, exist_ok=True)
    base = base.resolve(strict=True)
    # Lock is OS-owned: even a killed setup process releases it, allowing retry.
    import msvcrt
    with (base / 'setup.lock').open('a+b') as lock:
        lock.seek(0)
        if lock.read(1) == b'':
            lock.write(b'0')
            lock.flush()
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise RuntimeError('Another add-on setup is running') from error
        generation = base / 'generations' / generation_id(args.device)
        generation.mkdir(parents=True, exist_ok=True)
        python = generation / 'venv/Scripts/python.exe'
        env = {**os.environ, 'UV_CACHE_DIR': str(args.cache_dir or base / 'cache'), 'HF_HOME': str(base / 'hf-cache'),
               'UV_PYTHON_INSTALL_DIR': str(base / 'python'), 'PYTHONDONTWRITEBYTECODE': '1', 'PYTHONUTF8': '1'}
        if args.offline:
            env['UV_OFFLINE'] = '1'
        if (generation / 'installed.json').is_file():
            # Never modify an environment that an existing host may be using.
            run(python_command(python, Path(__file__), '--verify', str(generation), '--device', args.device), env)
            run(python_command(python, Path(__file__).with_name('notices.py'), '--generation', generation), env)
            atomic_json(base / 'installed.json', json.loads((generation / 'installed.json').read_text(encoding='utf-8')))
            print('Existing add-on generation verified.', flush=True)
            return
        print('Preparing the isolated embedding environment...', flush=True)
        interpreter=Path(sys.executable).resolve(strict=True)
        # uv's Windows launcher embeds a mutable minor-version junction even
        # with an explicit patch path. CPython's launcher honors its pinned home.
        # Refresh only unpublished environments, preserving packages/downloads.
        run(python_command(interpreter,'-m','venv','--without-pip','--copies',generation/'venv'),env)
        verify_venv_home(python,interpreter)
        run([args.uv, '--no-config', 'pip', 'sync', '--python', python, '--require-hashes', '--link-mode', 'hardlink', '--index-strategy',
             'unsafe-best-match', ROOT / f'runtime/semantic_search/requirements.{args.device}.lock.txt'], env)
        model = generation / 'model'
        print('Preparing the full multimodal model...', flush=True)
        if args.existing_model:
            copy_model(args.existing_model, model)
        else:
            source=previous_model(base,generation)
            if source is not None:
                print('Reusing the verified model from the previous generation...',flush=True)
                copy_model(source,model)
            elif args.offline:
                raise ValueError('Offline setup needs a verified local model')
            else:
                run(python_command(python, ROOT / 'scripts/fetch-embeddinggemma2.py', '--output', model), env)
        print('Verifying the offline model and all modality encoders...', flush=True)
        run(python_command(python, Path(__file__), '--verify', str(generation), '--device', args.device),
            {**env, 'HF_HUB_OFFLINE': '1', 'TRANSFORMERS_OFFLINE': '1'})
        run(python_command(python, Path(__file__).with_name('notices.py'), '--generation', generation), env)
        # No venv rename: Windows launchers can contain absolute interpreter paths.
        marker = dict(schemaVersion=2, generation=generation.name, device=args.device,
            revision=load_manifest()['revision'], modelManifestSha256=sha256(MANIFEST),
            lockSha256=sha256(ROOT / f'runtime/semantic_search/requirements.{args.device}.lock.txt'),
            setupManifestSha256=sha256(ROOT / 'runtime/multimodal_setup.json'),
            python='venv/Scripts/python.exe', model='model')
        atomic_json(generation / 'installed.json', marker)
        atomic_json(base / 'installed.json', marker)
        print('Multimodal add-on verified. Save settings to enable it.', flush=True)


def verify(generation, device):
    from semantic_search.embedding import FullEmbedder
    from PIL import Image
    embedder = FullEmbedder(generation / 'model', device=device, threads=4)
    import numpy as np
    frame = Image.new('RGB', (64, 64), 'blue')
    for content, kwargs in [
        ('readiness', {'query': True}), ({'image': frame}, {}),
        ({'audio': np.zeros(16000, dtype=np.float32)}, {}),
        ({'video': np.stack([np.asarray(frame)]*2)}, {'processing_kwargs': {'video': {
            'do_sample_frames': False, 'video_metadata': dict(total_num_frames=2, fps=1,
                duration=2, frames_indices=[0, 1], video_backend='scout-setup')}}}),
        ({'video':np.stack([np.asarray(frame)]*2),'audio':np.zeros(32000,dtype=np.float32)},
            {'processing_kwargs':{'video':{'do_sample_frames':False,'video_metadata':dict(total_num_frames=2,fps=1,duration=2,frames_indices=[0,1],video_backend='scout-setup')}}})]:
        embedder.embed(content, **kwargs)


if __name__ == '__main__':
    if '--verify' in sys.argv:
        parser = argparse.ArgumentParser()
        parser.add_argument('--verify', type=Path, required=True)
        parser.add_argument('--device', choices=['cpu', 'cuda'], required=True)
        options = parser.parse_args()
        verify(options.verify, options.device)
    else:
        main()
