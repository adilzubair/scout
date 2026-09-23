"""Explicit setup only. Normal launcher inference never downloads weights."""
import argparse
import json
from pathlib import Path
from huggingface_hub import snapshot_download

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--model', choices=['english', 'multilingual', 'typed-decisions', 'all'], default='english')
    args = parser.parse_args()
    manifest = json.loads(Path(__file__).with_name('models.json').read_text())
    keys = list(manifest['checkpoints']) if args.model == 'all' else [args.model]
    for key in keys:
        prefix = manifest['checkpoints'][key]
        prefix = prefix + '/' if prefix else ''
        patterns = [prefix + p for p in ['rl_agent_config.json', 'model.safetensors', 'tokenizer/*', 'encoder/*']]
        # Each checkpoint has its own directory, with exact upstream revision pinned.
        snapshot_download(manifest['repository'], revision=manifest['revision'], allow_patterns=patterns, local_dir=str(args.output / key))
        print(f'Downloaded {key} at {manifest["revision"]}', flush=True)

if __name__ == '__main__':
    main()
