#!/usr/bin/env python3
"""Python ↔ .NET buffalo_l parity harness (face-preprocess-v2-buffalo-l).

Embeds a fixture JPEG with the worker pipeline, optionally compares to a
.NET vector JSON produced by `dotnet test` / FaceParity helper.

Usage:
  python parity_check.py [image.jpg] [--write-json out.json]
  python parity_check.py image.jpg --dotnet-json dotnet_vec.json --min-cosine 0.95
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from embed import (
    DET_FILENAME,
    REC_FILENAME,
    BuffaloEmbedder,
    cosine_similarity,
    detect_faces_detail,
    embed_single_face,
    resolve_det_path,
    resolve_rec_path,
    try_load_detector,
    try_load_embedder,
)

WORKER_DIR = Path(__file__).resolve().parent
DEFAULT_IMAGE = (
    WORKER_DIR.parent
    / "media"
    / "faces"
    / "EMP-10008"
    / "90c4c3f7-442a-4fd8-a410-b85b328a297f"
    / "eaf692cf-e437-4ca5-b9d8-e4c2746dfd0c.jpg"
)


def embed_image(path: Path, detector, embedder: BuffaloEmbedder) -> list[float]:
    data = path.read_bytes()
    bgr, faces = detect_faces_detail(detector, data, min_face_px=60)
    if bgr is None or not faces:
        raise SystemExit(f"no face in {path}")
    result = embed_single_face(embedder, bgr, faces[0])
    if result is None:
        raise SystemExit(f"align/embed failed for {path}")
    return result[0]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("image", nargs="?", type=Path, default=DEFAULT_IMAGE)
    ap.add_argument("--write-json", type=Path, help="Write Python embedding JSON array")
    ap.add_argument("--dotnet-json", type=Path, help="Compare against .NET embedding JSON")
    ap.add_argument("--min-cosine", type=float, default=0.95)
    args = ap.parse_args()

    if not args.image.is_file():
        print(f"image missing: {args.image}", file=sys.stderr)
        return 2
    if resolve_det_path() is None:
        print(f"det_10g missing ({DET_FILENAME})", file=sys.stderr)
        return 2
    if resolve_rec_path() is None:
        print(f"w600k_r50 missing ({REC_FILENAME})", file=sys.stderr)
        return 2

    detector = try_load_detector()
    embedder = try_load_embedder()
    if detector is None or embedder is None:
        print("failed to load buffalo_l models", file=sys.stderr)
        return 2

    py_vec = embed_image(args.image, detector, embedder)
    print(f"python dims={len(py_vec)} image={args.image.name} model={embedder.meta['modelId']}")

    if args.write_json:
        import json

        args.write_json.write_text(json.dumps(py_vec), encoding="utf-8")
        print(f"wrote {args.write_json}")

    if args.dotnet_json:
        import json

        if not args.dotnet_json.is_file():
            print(f"dotnet json missing: {args.dotnet_json}", file=sys.stderr)
            return 2
        dn = json.loads(args.dotnet_json.read_text(encoding="utf-8"))
        cos = cosine_similarity(py_vec, dn)
        print(f"cosine python↔dotnet = {cos:.6f} (min {args.min_cosine})")
        if cos < args.min_cosine:
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
