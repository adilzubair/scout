"""Pinned local model identity and integrity checks, without inference imports."""

import hashlib
import json
import re
from pathlib import Path, PurePosixPath

MANIFEST = Path(__file__).resolve().parents[1] / 'embeddinggemma2_model.json'


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def load_manifest(path=MANIFEST):
    value = json.loads(Path(path).read_text(encoding='utf-8'))
    if value.get('schemaVersion') != 1 or value.get('repository') != 'google/embeddinggemma-2':
        raise ValueError('Unsupported model manifest')
    if not re.fullmatch(r'[0-9a-f]{40}', value.get('revision', '')):
        raise ValueError('A full immutable model revision is required')
    if set(value.get('fullModalities', [])) != {'text', 'image', 'audio', 'video'}:
        raise ValueError('This runtime requires the full multimodal model')
    if value.get('contextTokens') != 8192 or value.get('dimensions') != [768, 512, 256, 128]:
        raise ValueError('Unsupported input budget or embedding dimensions')
    files = value.get('files', {})
    required = {'model.safetensors', 'config.json', 'tokenizer.json', 'modules.json',
                'config_sentence_transformers.json', 'sentence_bert_config.json'}
    if not required.issubset(files):
        raise ValueError('Incomplete model manifest')
    for name, spec in files.items():
        relative = PurePosixPath(name)
        if relative.is_absolute() or '\\' in name or ':' in name or '..' in relative.parts:
            raise ValueError('Invalid artifact path')
        size = spec.get('bytes')
        if not re.fullmatch(r'[0-9a-f]{64}', spec.get('sha256', '')) or type(size) is not int or size <= 0:
            raise ValueError('Invalid artifact digest or length')
    return value


def verify_model(directory, manifest=None):
    manifest = manifest or load_manifest()
    root = Path(directory).resolve(strict=True)
    for name, spec in manifest['files'].items():
        path = (root / name).resolve(strict=True)
        if not path.is_relative_to(root) or path.stat().st_size != spec['bytes']:
            raise ValueError(f'Invalid artifact: {name}')
        if sha256(path) != spec['sha256']:
            raise ValueError(f'Artifact checksum mismatch: {name}')
    config = json.loads((root / 'config.json').read_text(encoding='utf-8'))
    if config.get('model_type') != 'embedding_gemma2' or not config.get('audio_config') or not config.get('vision_config'):
        raise ValueError('Expected EmbeddingGemma 2 with audio and vision encoders')
    return manifest
