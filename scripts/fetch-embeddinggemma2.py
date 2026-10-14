"""Download and verify the full pinned model into Scout's ignored local runtime."""

import argparse
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'runtime'))
from semantic_search.manifest import MANIFEST, load_manifest, sha256, verify_model


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / '.runtime/semantic-search/model')
    args = parser.parse_args()
    manifest = load_manifest()
    os.environ.setdefault('HF_HOME', str(ROOT / '.runtime/semantic-search/hf-cache'))
    from huggingface_hub import snapshot_download

    snapshot_download(manifest['repository'], revision=manifest['revision'],
                      allow_patterns=list(manifest['files']), local_dir=args.output, max_workers=2)
    verify_model(args.output, manifest)
    ready = args.output / 'scout-model-ready.json'
    temporary = ready.with_suffix('.tmp')
    temporary.write_text(json.dumps({'revision': manifest['revision'],
        'manifestSha256': sha256(MANIFEST), 'bytes': sum(f['bytes'] for f in manifest['files'].values())},
        indent=2) + '\n', encoding='utf-8')
    temporary.replace(ready)
    print(f'Full multimodal model verified: {manifest["revision"]}')


if __name__ == '__main__':
    main()
