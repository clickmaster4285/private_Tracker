"""AdaFace IR-50 ONNX embedder (face-preprocess-v1).

Loads FACE_ADAFACE_ONNX when present. Without the file, embeddings stay deferred.
"""

from __future__ import annotations

import hashlib
import os
from pathlib import Path
from typing import Any

import cv2
import numpy as np

# ArcFace / AdaFace 5-point template for 112×112 (left_eye, right_eye, nose, left_mouth, right_mouth).
ARC_FACE_5PTS = np.array(
    [
        [38.2946, 51.6963],
        [73.5318, 51.5014],
        [56.0252, 71.7366],
        [41.5493, 92.3655],
        [70.7299, 92.2041],
    ],
    dtype=np.float32,
)

WORKER_DIR = Path(__file__).resolve().parent
DEFAULT_ONNX = WORKER_DIR / "models" / "adaface_ir50_webface4m.onnx"


def env(name: str, default: str = "") -> str:
    return (os.environ.get(name) or default).strip()


def resolve_onnx_path() -> Path | None:
    raw = env("FACE_ADAFACE_ONNX")
    if raw:
        p = Path(raw)
        if not p.is_absolute():
            p = (WORKER_DIR / p).resolve()
        return p if p.is_file() else None
    if DEFAULT_ONNX.is_file() and DEFAULT_ONNX.stat().st_size > 1_000_000:
        return DEFAULT_ONNX
    return None


def model_meta(onnx_path: Path) -> dict[str, str]:
    sha = hashlib.sha256(onnx_path.read_bytes()).hexdigest()
    version = env("FACE_ADAFACE_MODEL_VERSION") or "ir50-webface4m-lab"
    return {
        "modelId": env("FACE_ADAFACE_MODEL_ID") or "adaface-ir50",
        "modelVersion": version,
        "preprocessVersion": "face-preprocess-v1",
        "packageSha256": sha,
        "packageRelPath": str(onnx_path),
    }


def yunet_landmarks_to_5pts(face_row: np.ndarray) -> np.ndarray:
    """YuNet order: right_eye, left_eye, nose, right_mouth, left_mouth → ArcFace order."""
    re = face_row[4:6]
    le = face_row[6:8]
    nt = face_row[8:10]
    rm = face_row[10:12]
    lm = face_row[12:14]
    return np.array([le, re, nt, lm, rm], dtype=np.float32)


def align_face_112(bgr: np.ndarray, face_row: np.ndarray) -> np.ndarray | None:
    pts = yunet_landmarks_to_5pts(face_row)
    if not np.isfinite(pts).all():
        return None
    m, _ = cv2.estimateAffinePartial2D(pts, ARC_FACE_5PTS, method=cv2.LMEDS)
    if m is None:
        return None
    return cv2.warpAffine(bgr, m, (112, 112), flags=cv2.INTER_LINEAR, borderValue=0.0)


def preprocess_adaface(face_bgr_112: np.ndarray) -> np.ndarray:
    """BGR 112×112 → NCHW float32 with AdaFace to_input scale."""
    rgb = cv2.cvtColor(face_bgr_112, cv2.COLOR_BGR2RGB).astype(np.float32)
    # (pixel/255 - 0.5) / 0.5  ==  pixel/127.5 - 1.0
    rgb = (rgb / 255.0 - 0.5) / 0.5
    chw = np.transpose(rgb, (2, 0, 1))
    return np.expand_dims(chw, axis=0).astype(np.float32)


def l2_normalize(vec: np.ndarray) -> np.ndarray:
    n = float(np.linalg.norm(vec))
    if n < 1e-12:
        return vec.astype(np.float32)
    return (vec / n).astype(np.float32)


def quality_from_face_row(face_row: np.ndarray, img_w: int, img_h: int) -> float:
    w = float(face_row[2])
    h = float(face_row[3])
    score = float(face_row[14]) if len(face_row) > 14 else 0.0
    area = (w * h) / max(float(img_w * img_h), 1.0)
    return float(max(0.0, min(1.0, 0.5 * score + 0.5 * min(area * 8.0, 1.0))))


class AdaFaceEmbedder:
    def __init__(self, onnx_path: Path) -> None:
        import onnxruntime as ort  # lazy — optional until model configured

        self.path = onnx_path
        self.meta = model_meta(onnx_path)
        self.dims = int(env("FACE_ADAFACE_DIMS") or "512")
        opts = ort.SessionOptions()
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        self.session = ort.InferenceSession(
            str(onnx_path),
            sess_options=opts,
            providers=["CPUExecutionProvider"],
        )
        inputs = self.session.get_inputs()
        if not inputs:
            raise RuntimeError("AdaFace ONNX has no inputs")
        self.input_name = inputs[0].name
        outputs = self.session.get_outputs()
        if not outputs:
            raise RuntimeError("AdaFace ONNX has no outputs")
        self.output_name = outputs[0].name

    def embed_crop(self, face_bgr_112: np.ndarray) -> list[float]:
        tensor = preprocess_adaface(face_bgr_112)
        out = self.session.run([self.output_name], {self.input_name: tensor})[0]
        vec = np.asarray(out, dtype=np.float32).reshape(-1)
        if vec.shape[0] != self.dims:
            raise RuntimeError(f"AdaFace output dims {vec.shape[0]} != {self.dims}")
        return l2_normalize(vec).tolist()


def try_load_embedder() -> AdaFaceEmbedder | None:
    path = resolve_onnx_path()
    if path is None:
        return None
    return AdaFaceEmbedder(path)


def detect_faces_detail(
    detector: cv2.FaceDetectorYN, jpeg_bytes: bytes, min_face_px: int
) -> tuple[np.ndarray | None, list[np.ndarray]]:
    """Returns (bgr image, list of YuNet face rows kept by size)."""
    arr = np.frombuffer(jpeg_bytes, dtype=np.uint8)
    mat = cv2.imdecode(arr, cv2.IMREAD_COLOR)
    if mat is None or mat.size == 0:
        return None, []
    h, w = mat.shape[:2]
    if h < 16 or w < 16:
        return mat, []
    detector.setInputSize((w, h))
    _retval, faces = detector.detect(mat)
    if faces is None or len(faces) == 0:
        return mat, []
    kept: list[np.ndarray] = []
    for row in faces:
        if max(float(row[2]), float(row[3])) < min_face_px:
            continue
        kept.append(np.asarray(row, dtype=np.float32))
    return mat, kept


def embed_single_face(
    embedder: AdaFaceEmbedder,
    bgr: np.ndarray,
    face_row: np.ndarray,
) -> tuple[list[float], float] | None:
    crop = align_face_112(bgr, face_row)
    if crop is None:
        return None
    h, w = bgr.shape[:2]
    quality = quality_from_face_row(face_row, w, h)
    vector = embedder.embed_crop(crop)
    return vector, quality
