"""Explicit setup of the pinned helper; normal inference never downloads files."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile
from huggingface_hub import hf_hub_download


def verified(path, expected):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024*1024), b''):
            digest.update(block)
    if digest.hexdigest() != expected:
        raise ValueError('Checksum mismatch: ' + path.name)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(Path(__file__).with_name('semantic_model.json').read_text())
    path = hf_hub_download(manifest['repository'], manifest['filename'], revision=manifest['revision'], local_dir=args.output / manifest['directory'])
    verified(Path(path), manifest['sha256'])
    runtime = manifest['runtime']
    folder = args.output.resolve().parent / runtime['directory']
    folder.mkdir(parents=True, exist_ok=True)
    archive = folder / 'runtime.zip'
    if not archive.exists():
        temporary = folder / 'runtime.download'
        urllib.request.urlretrieve(runtime['url'], temporary)
        verified(temporary, runtime['sha256'])
        temporary.replace(archive)
    verified(archive, runtime['sha256'])
    with zipfile.ZipFile(archive) as bundle:
        for member in bundle.infolist():
            if not (folder / member.filename).resolve().is_relative_to(folder):
                raise ValueError('Runtime archive contains an unsafe path')
        bundle.extractall(folder)
    print('Local semantic helper installed and checksums verified.')


if __name__ == '__main__':
    main()
