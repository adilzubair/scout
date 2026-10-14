"""Transactional, retryable add-on generations; existing installations stay usable."""

import argparse
import json
import os
from pathlib import Path
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
    identity = '|'.join(sha256(path) for path in [MANIFEST, ROOT / 'runtime/office_renderer.json',
        ROOT / 'runtime/multimodal_setup.json', ROOT / f'runtime/semantic_search/requirements.{device}.lock.txt'])
    return device + '-' + hashlib.sha256(identity.encode()).hexdigest()[:24]


def run(command, env):
    subprocess.run([str(value) for value in command], env=env, check=True,
                   creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))


def copy_model(source, destination):
    manifest = verify_model(source)
    destination.mkdir(parents=True, exist_ok=True)
    for name in manifest['files']:
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(Path(source) / name, target)
    verify_model(destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--uv', type=Path, required=True)
    parser.add_argument('--device', choices=['cpu', 'cuda'], default='cpu')
    parser.add_argument('--existing-model', type=Path, help='Import a verified local development model')
    parser.add_argument('--existing-office', type=Path, help='Import a verified local development renderer')
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
            run([python, Path(__file__), '--verify', str(generation), '--device', args.device], env)
            atomic_json(base / 'installed.json', json.loads((generation / 'installed.json').read_text(encoding='utf-8')))
            print('Existing add-on generation verified.', flush=True)
            return
        print('Preparing the isolated embedding environment...', flush=True)
        if not python.is_file():
            run([args.uv, 'venv', '--python', sys.executable, generation / 'venv'], env)
        run([args.uv, 'pip', 'sync', '--python', python, '--require-hashes', '--index-strategy',
             'unsafe-best-match', ROOT / f'runtime/semantic_search/requirements.{args.device}.lock.txt'], env)
        model = generation / 'model'
        print('Preparing the full multimodal model...', flush=True)
        if args.existing_model:
            copy_model(args.existing_model, model)
        else:
            if args.offline:
                raise ValueError('Offline setup needs a verified local model')
            run([python, ROOT / 'scripts/fetch-embeddinggemma2.py', '--output', model], env)
        office_spec = json.loads((ROOT / 'runtime/office_renderer.json').read_text(encoding='utf-8'))
        office = generation / 'office' / ('libreoffice-' + office_spec['version'])
        print('Preparing the Office page renderer...', flush=True)
        if args.existing_office:
            # Import only verified MSI inventory members, never user profile/cache files.
            from semantic_search.office import OfficeRenderer, package_path
            verified = OfficeRenderer(args.existing_office)
            ready = json.loads((verified.root / 'scout-renderer-ready.json').read_text(encoding='utf-8'))
            office.mkdir(parents=True, exist_ok=True)
            for name in ready['files']:
                relative = package_path(name)
                target = office / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(verified.root / relative, target)
            atomic_json(office / 'scout-renderer-ready.json', ready)
        elif not (office / 'scout-renderer-ready.json').is_file():
            if args.offline:
                raise ValueError('Offline setup needs a verified local renderer')
            run([python, ROOT / 'scripts/fetch-office-renderer.py', '--output', generation / 'office'], env)
        print('Verifying the offline model and all modality encoders...', flush=True)
        run([python, Path(__file__), '--verify', str(generation), '--device', args.device],
            {**env, 'HF_HUB_OFFLINE': '1', 'TRANSFORMERS_OFFLINE': '1'})
        # No venv rename: Windows launchers can contain absolute interpreter paths.
        marker = dict(schemaVersion=1, generation=generation.name, device=args.device,
            revision=load_manifest()['revision'], modelManifestSha256=sha256(MANIFEST),
            lockSha256=sha256(ROOT / f'runtime/semantic_search/requirements.{args.device}.lock.txt'),
            officeManifestSha256=sha256(ROOT / 'runtime/office_renderer.json'),
            setupManifestSha256=sha256(ROOT / 'runtime/multimodal_setup.json'),
            python='venv/Scripts/python.exe', model='model',
            office='office/libreoffice-' + office_spec['version'])
        atomic_json(generation / 'installed.json', marker)
        atomic_json(base / 'installed.json', marker)
        print('Multimodal add-on verified. Save settings to enable it.', flush=True)


def verify(generation, device):
    from semantic_search.embedding import FullEmbedder
    from semantic_search.office import OfficeRenderer
    from PIL import Image
    import numpy as np
    manifest = json.loads((ROOT / 'runtime/office_renderer.json').read_text(encoding='utf-8'))
    OfficeRenderer(generation / 'office' / ('libreoffice-' + manifest['version']))
    embedder = FullEmbedder(generation / 'model', device=device, threads=4)
    frame = Image.new('RGB', (64, 64), 'blue')
    for content, kwargs in [
        ('readiness', {'query': True}), ({'image': frame}, {}),
        ({'audio': np.zeros(16000, dtype=np.float32)}, {}),
        ({'video': np.stack([np.asarray(frame)]*2)}, {'processing_kwargs': {'video': {
            'do_sample_frames': False, 'video_metadata': dict(total_num_frames=2, fps=1,
                duration=2, frames_indices=[0, 1], video_backend='scout-setup')}}})]:
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
