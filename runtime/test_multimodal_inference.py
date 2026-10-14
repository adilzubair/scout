"""Real-model offline contracts. Requires the separately installed full checkpoint."""

import argparse
from pathlib import Path
import socket
from unittest.mock import patch

import numpy as np
from semantic_search.embedding import FullEmbedder


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--device', choices=['cpu', 'cuda'], default='cpu')
    args = parser.parse_args()
    with patch.object(socket.socket, 'connect', side_effect=AssertionError('Unexpected network access')):
        model = FullEmbedder(args.model, args.device, dimensions=256)
        vector, tokens = model.embed('find an invoice paid yesterday', query=True)
        assert vector.shape == (256,) and np.isfinite(vector).all()
        assert abs(float(np.linalg.norm(vector)) - 1) < 1e-5
        assert tokens > 0
        print('Offline full-model loading and normalized 256d inference passed')
        with patch.object(model.model, 'forward', side_effect=AssertionError('Oversized input reached inference')):
            try:
                model.embed(' word' * 10000, query=True)
            except ValueError as error:
                assert 'token budget' in str(error)
            else:
                raise AssertionError('Oversized input was silently truncated')
        print('Oversized input rejected before inference')


if __name__ == '__main__':
    main()
