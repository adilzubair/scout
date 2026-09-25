"""Download one pinned Ettin CPU probe checkpoint into ignored .runtime/models."""

import argparse
import hashlib
import os
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SPECS = {
    '32m': ('b33e5ceb5110773ea9cf5e00c9bedc83a8c2afdd',
            '7ca231b98dc591122774127865a293aae90b9a2ccb148afe8c2587d03703f8d0'),
    '68m': ('d166fa88ddde3c42bc3ee92f7df476d941c8204a',
            '32972293b8c229a906129aee4feb08fee3df993f9e5ed247179c94bcbbaae06a'),
    '150m': ('025501c4e0f9bbeb4c5b198318e0089ff061cc14',
             '7ae500b430815623ae87f4c05ffe6fccaab88752963c6a1140cb4ef86f60da70'),
}
FILES = [
    'config.json', 'tokenizer.json', 'tokenizer_config.json', 'modules.json',
    'sentence_bert_config.json', 'config_sentence_transformers.json',
    '*_Pooling/config.json', '*_Dense/config.json', '*_Dense/model.safetensors',
    '*_LayerNorm/config.json', '*_LayerNorm/model.safetensors',
    'onnx/model_quint8_avx2.onnx',
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--size', choices=SPECS, required=True)
    args = parser.parse_args()
    revision, expected = SPECS[args.size]
    os.environ.setdefault('HF_HOME', str(ROOT / '.runtime' / 'hf-cache'))
    os.environ.setdefault('HF_HUB_DISABLE_XET', '1')
    from huggingface_hub import snapshot_download

    target = ROOT / '.runtime' / 'models' / f'ettin-reranker-{args.size}-v1'
    snapshot_download(f'cross-encoder/ettin-reranker-{args.size}-v1', revision=revision,
                      allow_patterns=FILES, local_dir=target, max_workers=2)
    artifact = target / 'onnx' / 'model_quint8_avx2.onnx'
    with artifact.open('rb') as stream:
        actual = hashlib.file_digest(stream, 'sha256').hexdigest()
    if actual != expected:
        raise SystemExit(f'ONNX SHA-256 mismatch for {args.size}: {actual}')
    print(f'{target}: verified {actual}')


if __name__ == '__main__':
    main()
