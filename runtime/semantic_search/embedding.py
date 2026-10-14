"""Offline full-modality inference with explicit input and output validation."""

import os
from pathlib import Path
import time

from .manifest import verify_model
from .paths import local_file


def normalize_vector(vector, dimensions):
    import numpy as np
    vector = np.asarray(vector, dtype=np.float32)
    if vector.shape != (768,) or not np.isfinite(vector).all():
        raise ValueError('Invalid embedding shape or non-finite output')
    if dimensions not in (128, 256, 512, 768):
        raise ValueError('Unsupported embedding dimension')
    shortened = vector[:dimensions].copy()
    norm = float(np.linalg.norm(shortened))
    if not norm > 0 or not np.isfinite(norm):
        raise ValueError('Zero or invalid embedding norm')
    return shortened / norm


def validate_content(content):
    """Accept prepared memory inputs only; processors must never fetch media URLs."""
    if isinstance(content, str):
        if not content.strip() or len(content) > 512_000:
            raise ValueError('Empty or excessively large text input')
        return
    if not isinstance(content, dict) or not content or set(content) - {'text', 'image', 'audio', 'video'}:
        raise ValueError('Unsupported content input')
    import numpy as np
    from PIL import Image
    for kind, value in content.items():
        if kind == 'text':
            if not isinstance(value, str):
                raise ValueError('Mixed content text must be a string')
            validate_content(value)
        elif kind == 'image':
            if not isinstance(value, Image.Image) or value.width * value.height > 4_000_000:
                raise ValueError('Image must be decoded in memory within the pixel budget')
        elif kind == 'audio':
            if (not isinstance(value, np.ndarray) or value.ndim != 1 or not 0 < value.size <= 960_000
                    or not np.issubdtype(value.dtype, np.floating) or not np.isfinite(value).all()):
                raise ValueError('Audio must be finite mono samples at 16 kHz within sixty seconds')
        elif kind == 'video':
            if (not isinstance(value, np.ndarray) or value.ndim != 4 or not 0 < len(value) <= 16
                    or value.shape[-1] != 3 or value.shape[1] * value.shape[2] > 4_000_000
                    or value.dtype != np.uint8):
                raise ValueError('Video must be bounded RGB frames decoded in memory')


class FullEmbedder:
    def __init__(self, model_directory, device='cpu', threads=4, dimensions=768):
        if device not in ('cpu', 'cuda') or not 1 <= threads <= 32:
            raise ValueError('Invalid device or thread count')
        if dimensions not in (128, 256, 512, 768):
            raise ValueError('Unsupported embedding dimension')
        os.environ['HF_HUB_OFFLINE'] = '1'
        os.environ['TRANSFORMERS_OFFLINE'] = '1'
        os.environ['USE_TF'] = '0'
        started = time.perf_counter()
        self.manifest = verify_model(model_directory)
        self.integrity_seconds = time.perf_counter() - started
        import torch
        from sentence_transformers import SentenceTransformer

        torch.set_num_threads(threads)
        if device == 'cuda' and not torch.cuda.is_available():
            raise ValueError('CUDA requested but unavailable')
        dtype = torch.bfloat16 if device == 'cuda' and torch.cuda.is_bf16_supported() else torch.float32
        started = time.perf_counter()
        self.model = SentenceTransformer(str(Path(model_directory).resolve()), device=device,
            local_files_only=True, trust_remote_code=False,
            model_kwargs={'dtype': dtype})
        self.load_seconds = time.perf_counter() - started
        config = self.model[0].model.config
        if not config.audio_config or not config.vision_config:
            raise ValueError('Audio and vision encoders must both be loaded')
        self.device = device
        self.dtype = str(dtype)
        self.dimensions = dimensions

    def embed(self, content, *, query=False, title=None, processing_kwargs=None):
        validate_content(content)
        import torch
        from sentence_transformers.util import batch_to_device

        if query:
            if not isinstance(content, str) or not content.strip():
                raise ValueError('A query must be nonempty text')
            content = 'task: search result | query: ' + content
        elif isinstance(content, str):
            content = f'title: {title or "none"} | text: {content}'
        processing = dict(processing_kwargs or {})
        for key in ('text', 'chat_template'):
            processing[key] = {**processing.get(key, {}), 'truncation': False}
        features = self.model.preprocess([content], processing_kwargs=processing)
        ids = features.get('input_ids')
        if ids is None or ids.ndim != 2 or ids.shape[-1] > self.manifest['contextTokens']:
            raise ValueError('Input exceeds the shared token budget or lacks token IDs; segment it first')
        with torch.inference_mode():
            output = self.model(batch_to_device(features, self.device))['sentence_embedding'][0]
        vector = normalize_vector(output.detach().float().cpu().numpy(), self.dimensions)
        return vector, int(ids.shape[-1])
